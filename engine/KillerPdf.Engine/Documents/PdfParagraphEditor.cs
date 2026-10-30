using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Signing;

namespace KillerPdf.Engine.Documents;

/// <summary>Horizontal placement of each line inside a paragraph box.</summary>
public enum PdfParagraphAlignment
{
    /// <summary>Align to the left edge.</summary>
    Left,
    /// <summary>Center each line.</summary>
    Center,
    /// <summary>Align to the right edge.</summary>
    Right
}

/// <summary>A supported source paragraph in unrotated, crop-relative page points.</summary>
public sealed record PdfParagraphCandidate(int PageIndex, PdfContentBounds SourceRegion, string Text,
    string FontName, double FontSize, double Leading, PdfRgbColor Color)
{
    /// <summary>Gets the rectangle that clips the paragraph's text, in the same points as
    /// <see cref="SourceRegion"/>, or null when it is not known.</summary>
    public PdfContentBounds? Clip { get; init; }

    /// <summary>Gets whether the source mixes fonts or sizes. <see cref="FontName"/> and
    /// <see cref="FontSize"/> are then its dominant style, which the replacement takes.</summary>
    public bool MixedStyles { get; init; }

    /// <summary>Returns the part of a replacement box that lies inside <see cref="Clip"/>, since
    /// the replacement is painted under the same clip and anything outside it would vanish.</summary>
    public PdfContentBounds FitToClip(PdfContentBounds box) => Clip is { } clip
        ? new PdfContentBounds(Math.Max(box.Left, clip.Left), Math.Max(box.Bottom, clip.Bottom),
            Math.Min(box.Right, clip.Right), Math.Min(box.Top, clip.Top)) : box;
}

/// <summary>The eligibility of the complete source lines inside a selected area.</summary>
public sealed record PdfParagraphInspection(bool CanEdit, string Reason, PdfParagraphCandidate? Candidate)
{
    /// <summary>Gets where the refused source lines are, when the refusal is about one paragraph
    /// and not the whole page.</summary>
    public PdfContentBounds? Region { get; init; }

    /// <summary>Gets a stable refusal code, or null for an eligible paragraph.</summary>
    public PdfParagraphRefusalCode? RefusalCode { get; init; } = CanEdit ? null : PdfParagraphRefusalCode.Unsupported;
}

/// <summary>Stable reasons why source paragraphs cannot be edited.</summary>
public enum PdfParagraphRefusalCode
{
    /// <summary>The source does not meet the supported paragraph constraints.</summary>
    Unsupported,
    /// <summary>The page has a nonzero rotation.</summary>
    RotatedPage,
    /// <summary>No longer reported, since tagged paragraphs can be edited within their own element.</summary>
    [Obsolete("Tagged documents are supported, so this code is no longer reported.")]
    TaggedDocument
}

/// <summary>Supported candidates and retained refusals from page inspection.</summary>
public sealed record PdfParagraphCandidateInspection(IReadOnlyList<PdfParagraphCandidate> Candidates,
    IReadOnlyList<PdfParagraphInspection> Refusals);

/// <summary>A line laid out in crop-relative page points.</summary>
public sealed record PdfParagraphLine(string Text, double X, double Baseline, double Width);

/// <summary>An immutable source-bound layout and its exact PDF preview.</summary>
public sealed class PdfParagraphPlan
{
    private readonly byte[]? _preview;
    internal byte[] SourceHash { get; }
    /// <summary>Gets the original source paragraph.</summary>
    public PdfParagraphCandidate Source { get; }
    /// <summary>Gets the replacement box.</summary>
    public PdfContentBounds TargetBounds { get; }
    /// <summary>Gets the replacement text.</summary>
    public string Text { get; }
    /// <summary>Gets the replacement font name.</summary>
    public string FontName { get; }
    /// <summary>Gets the replacement size in points.</summary>
    public double FontSize { get; }
    /// <summary>Gets the distance between baselines in points.</summary>
    public double Leading { get; }
    /// <summary>Gets the horizontal line alignment.</summary>
    public PdfParagraphAlignment Alignment { get; }
    /// <summary>Gets the planned line positions.</summary>
    public IReadOnlyList<PdfParagraphLine> Lines { get; }
    /// <summary>Gets whether the replacement does not fit its box.</summary>
    public bool HasOverflow => OverflowReason.Length != 0;
    /// <summary>Gets the reason the replacement cannot fit.</summary>
    public string OverflowReason { get; }

    internal PdfParagraphPlan(PdfDocument document, PdfParagraphCandidate source, PdfContentBounds target,
        string text, TrueTypeFont font, double size, double leading, PdfParagraphAlignment alignment,
        IReadOnlyList<PdfParagraphLine> lines, string overflow, byte[]? preview)
    {
        SourceHash = SHA256.HashData(document.Source.Span);
        Source = source;
        TargetBounds = target;
        Text = text;
        FontName = font.PostScriptName;
        FontSize = size;
        Leading = leading;
        Alignment = alignment;
        Lines = Array.AsReadOnly(lines.ToArray());
        OverflowReason = overflow;
        _preview = preview;
    }

    /// <summary>Returns a private copy of the exact document that Apply will return.</summary>
    public byte[] GetPreviewDocument() => _preview?.ToArray()
        ?? throw new InvalidOperationException(OverflowReason);
}

/// <summary>Plans and replaces simple horizontal paragraphs without moving other page content.</summary>
/// <remarks>The initial slice accepts uniform left-to-right source lines painted in one graphics
/// state, across any number of top-level text objects; in a tagged document they must share one
/// element. Encrypted, signed and rotated pages are refused, as are paragraphs that are clipped,
/// transformed, soft masked, on an optional content layer, carrying replacement text, or inside
/// reusable form content; the rest of the page does not matter. Replacement text uses one explicitly supplied embeddable font, Latin glyphs, and a
/// small explicit set of ordinary typographic punctuation and symbols.</remarks>
public static class PdfParagraphEditor
{
    /// <summary>Finds conservative groups of complete, aligned source lines.</summary>
    public static IReadOnlyList<PdfParagraphCandidate> FindParagraphs(PdfDocument document, int pageIndex)
        => InspectCandidates(document, pageIndex).Candidates;

    /// <summary>Finds candidates while retaining reasons for rejected source groups.</summary>
    public static PdfParagraphCandidateInspection InspectCandidates(PdfDocument document, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        // One read of the page serves every group. A busy page has hundreds of them, and a reason
        // that covers the whole document is one refusal, not one per group.
        ParagraphPage? context = null;
        PdfParagraphInspection? blocked;
        try { context = ReadParagraphPage(document, pageIndex, out blocked); }
        catch (Exception error) when (IsRefusal(error)) { blocked = new PdfParagraphInspection(false, error.Message, null); }
        if (blocked is not null || context is null)
            return new([], Array.AsReadOnly(new[] { blocked! }));
        PdfPageContent page = context.Content;
        var result = new List<PdfParagraphCandidate>();
        var refusals = new List<PdfParagraphInspection>();
        var group = new List<ParagraphRow>();
        bool tagged = IsTagged(context.Tree);
        foreach (ParagraphRow row in Rows(page.Lines))
        {
            if (group.Count > 0)
            {
                ParagraphRow previous = group[^1];
                double size = previous.Style.Size;
                double gap = previous.Baseline - row.Baseline;
                if (Math.Abs(previous.Left - row.Left) > 1 || gap < size * 0.8 || gap > size * 1.8
                    || previous.Style.Font != row.Style.Font || Math.Abs(size - row.Style.Size) > 0.01
                    || tagged && previous.Letters[0].MarkedContentId != row.Letters[0].MarkedContentId) Flush();
            }
            group.Add(row);
        }
        Flush();
        if (result.Count == 0 && refusals.Count == 0)
        {
            PdfParagraphInspection emptyPage = Inspect(document, pageIndex, new PdfContentBounds(0, 0, page.Width, page.Height));
            if (!emptyPage.CanEdit) refusals.Add(emptyPage);
        }
        return new(Array.AsReadOnly(result.ToArray()), Array.AsReadOnly(refusals.ToArray()));

        void Flush()
        {
            if (group.Count == 0) return;
            PdfExtractedLine[] lines = [.. group.SelectMany(row => row.Lines)];
            var bounds = new PdfContentBounds(lines.Min(line => line.BoundingBox.Left),
                lines.Min(line => line.BoundingBox.Bottom), lines.Max(line => line.BoundingBox.Right),
                lines.Max(line => line.BoundingBox.Top));
            PdfParagraphInspection inspection;
            try { inspection = InspectRegion(document, pageIndex, bounds, context); }
            catch (Exception error) when (IsRefusal(error))
            { inspection = new PdfParagraphInspection(false, error.Message, null) { Region = bounds }; }
            if (inspection.Candidate is { } candidate) result.Add(candidate);
            else refusals.Add(inspection);
            group.Clear();
        }
    }

    /// <summary>Inspects complete source lines and reports unsupported cases without mutation.</summary>
    public static PdfParagraphInspection Inspect(PdfDocument document, int pageIndex, PdfContentBounds sourceRegion)
    {
        ArgumentNullException.ThrowIfNull(document);
        try
        {
            ParagraphPage page = ReadParagraphPage(document, pageIndex, out PdfParagraphInspection? blocked);
            return blocked ?? InspectRegion(document, pageIndex, sourceRegion, page);
        }
        catch (Exception error) when (IsRefusal(error))
        { return new PdfParagraphInspection(false, error.Message, null); }
    }

    private sealed record ParagraphPage(PdfPageTree Tree, PdfPageContent Content, PdfContentRedaction.ParagraphPaintMap Paint,
        IReadOnlySet<int> DescribedElements);

    // Marked content ids on this page whose structure element, or an ancestor, carries replacement
    // text. Assistive technology reads that in place of the page text, so an edit would leave it stale.
    private static HashSet<int> DescribedElements(PdfDocument document, PdfPageTree tree, int pageIndex)
    {
        var described = new HashSet<int>();
        if (!IsTagged(tree)) return described;
        int page = tree.Pages[pageIndex].Reference.ObjectNumber;
        var visited = new HashSet<int>();
        Visit(tree.Catalog[Name("StructTreeRoot")], false, true, 0);
        return described;

        void Visit(PdfObject value, bool covered, bool onPage, int depth)
        {
            if (depth > 256) throw new NotSupportedException("The document structure is too deeply nested to edit a paragraph.");
            if (value is PdfIndirectReference reference && !visited.Add(reference.ObjectNumber)) return;
            switch (Resolve(document, value))
            {
                case PdfInteger id when covered && onPage:
                    described.Add((int)id.Value);
                    break;
                case PdfArray array:
                    foreach (PdfObject item in array) Visit(item, covered, onPage, depth + 1);
                    break;
                case PdfDictionary element:
                    bool here = onPage;
                    if (element.TryGetValue(Name("Pg"), out PdfObject? pageValue))
                        here = pageValue is PdfIndirectReference pageReference && pageReference.ObjectNumber == page;
                    bool carries = covered || element.ContainsKey(Name("ActualText")) || element.ContainsKey(Name("Alt"))
                        || element.ContainsKey(Name("E"));
                    if (element.TryGetValue(Name("MCID"), out PdfObject? content)) Visit(content, carries, here, depth + 1);
                    if (element.TryGetValue(Name("K"), out PdfObject? kids)) Visit(kids, carries, here, depth + 1);
                    break;
            }
        }
    }

    // One visual line. Extraction ends a line at a superscript or a font change, so a sentence with
    // a citation arrives in pieces that have to be put back before paragraphs can be found.
    private sealed class ParagraphRow
    {
        internal List<PdfExtractedLine> Lines { get; } = [];
        internal PdfExtractedLetter[] Letters { get; private set; } = [];
        internal (string Font, double Size) Style { get; private set; }
        internal double Baseline { get; private set; }
        internal double Left => Lines[0].BoundingBox.Left;
        internal double Right => Lines.Max(line => line.BoundingBox.Right);
        internal string Text => string.Concat(Lines.Select(line => line.Text)).Trim();

        internal void Add(PdfExtractedLine line)
        {
            Lines.Add(line);
            Lines.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
            Letters = [.. Lines.SelectMany(item => item.Runs).SelectMany(run => run.Letters)];
            Style = DominantStyle(Letters);
            (string font, double size) = Style;
            Baseline = Letters.First(letter => letter.FontName == font
                && Math.Round(letter.PointSize, 2) == size).StartBaseLine.Y;
        }
    }

    private static List<ParagraphRow> Rows(IEnumerable<PdfExtractedLine> lines)
    {
        var rows = new List<ParagraphRow>();
        foreach (PdfExtractedLine line in lines)
        {
            if (line.Runs.Count == 0 || line.Runs.Any(run => run.Letters.Count == 0)) continue;
            double baseline = line.Runs[0].Letters[0].StartBaseLine.Y;
            ParagraphRow? row = rows.TakeLast(4).FirstOrDefault(candidate =>
            {
                double size = Math.Max(candidate.Style.Size, line.Runs[0].PointSize);
                double before = candidate.Left - line.BoundingBox.Right, after = line.BoundingBox.Left - candidate.Right;
                return Math.Abs(candidate.Baseline - baseline) < size * 0.6 && Math.Max(before, after) is > -1 and var gap
                    && gap < size * 1.5;
            });
            if (row is null) rows.Add(row = new ParagraphRow());
            row.Add(line);
        }
        return rows;
    }

    private static (string Font, double Size) DominantStyle(IEnumerable<PdfExtractedLetter> letters) =>
        letters.GroupBy(letter => (letter.FontName, Size: Math.Round(letter.PointSize, 2)))
            .OrderByDescending(style => style.Count()).Select(style => style.Key).First();

    private static bool IsRefusal(Exception error) =>
        error is NotSupportedException or FormatException or InvalidOperationException or ArgumentException;

    // Checks everything that holds for the whole page, so each region only pays for its own.
    private static ParagraphPage ReadParagraphPage(PdfDocument document, int pageIndex, out PdfParagraphInspection? blocked)
    {
        blocked = null;
        PdfPageTree tree = PdfPageTree.Read(document);
        if (pageIndex < 0 || pageIndex >= tree.Pages.Count) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (document.IsEncrypted)
            throw new NotSupportedException("Remove document encryption before editing paragraphs.");
        if (tree.Catalog.ContainsKey(Name("Perms")))
            throw new NotSupportedException("Paragraph editing is not supported in documents with certification or usage rights.");
        if (PdfSignatureReader.Read(document).Any(signature => signature.IsSigned))
            throw new NotSupportedException("Paragraph editing is not supported in signed documents.");
        PdfPageTreeEntry entry = tree.Pages[pageIndex];
        if (entry.InheritedValues.TryGetValue(Name("Rotate"), out PdfObject? rotation)
            && Number(document, rotation) % 360 != 0)
        {
            blocked = new PdfParagraphInspection(false, "Paragraph editing is not supported on rotated pages.", null)
                { RefusalCode = PdfParagraphRefusalCode.RotatedPage };
            return new ParagraphPage(tree, null!, null!, null!);
        }
        PdfPageContent content = new PdfPageContentReader(document).Read(pageIndex);
        if (content.Diagnostics.Count != 0)
            throw new NotSupportedException("The page has extraction diagnostics and cannot be edited as a paragraph.");
        return new ParagraphPage(tree, content, new PdfContentRedaction.ParagraphPaintMap(document, pageIndex),
            DescribedElements(document, tree, pageIndex));
    }

    private static PdfParagraphInspection InspectRegion(PdfDocument document, int pageIndex, PdfContentBounds region,
        ParagraphPage context)
    {
        (PdfPageTree tree, PdfPageContent page, _, _) = context;
        ValidateBounds(region, page.Width, page.Height);
        var selected = page.Letters.Where(letter => CenterInside(letter.BoundingBox, region)).ToArray();
        if (selected.Length == 0) throw new NotSupportedException("Select a complete paragraph of readable text.");
        if (selected.Length > 100_000) throw new NotSupportedException("The source paragraph exceeds the size limit.");
        // The replacement goes into the first glyph's element, so one element keeps the structure true.
        if (IsTagged(tree) && selected.Select(letter => letter.MarkedContentId).Distinct().Skip(1).Any())
            throw new NotSupportedException("The selection spans several tagged elements.");
        if (selected[0].MarkedContentId is int element && context.DescribedElements.Contains(element))
            throw new NotSupportedException("The tagged element carries its own replacement text.");
        var selectedSet = new HashSet<PdfExtractedLetter>(selected, ReferenceEqualityComparer.Instance);
        PdfExtractedLine[] lines = [.. page.Lines.Where(line => line.Runs.SelectMany(run => run.Letters)
            .Any(selectedSet.Contains))];
        if (lines.SelectMany(line => line.Runs).SelectMany(run => run.Letters).Any(letter => !selectedSet.Contains(letter)))
            throw new NotSupportedException("The selection cuts a source line. Select complete lines.");
        if (selected.Any(letter => letter.WritingDirection != PdfWritingDirection.LeftToRight
            || Math.Abs(letter.EndBaseLine.Y - letter.StartBaseLine.Y) > 0.001))
            throw new NotSupportedException("Only horizontal paragraphs are supported.");
        // One box holds one style. Citations and bold words do not stop an edit: the replacement
        // takes the dominant style, and the candidate says the rest will not carry over.
        var first = DominantStyle(selected);
        List<ParagraphRow> rows = Rows(lines);
        foreach (ParagraphRow row in rows)
            for (int index = 1; index < row.Letters.Length; index++)
                if (row.Letters[index].StartBaseLine.X - row.Letters[index - 1].EndBaseLine.X > first.Size * 1.5)
                    throw new NotSupportedException("The source line has separated columns or uncertain spacing.");
        for (int index = 1; index < rows.Count; index++)
        {
            double gap = rows[index - 1].Baseline - rows[index].Baseline;
            if (Math.Abs(rows[index].Left - rows[0].Left) > 1 || gap < first.Size * 0.8 || gap > first.Size * 1.8)
                throw new NotSupportedException("The selection is not one simple aligned paragraph.");
        }
        var bounds = new PdfContentBounds(selected.Min(letter => letter.BoundingBox.Left),
            selected.Min(letter => letter.BoundingBox.Bottom), selected.Max(letter => letter.BoundingBox.Right),
            selected.Max(letter => letter.BoundingBox.Top));
        var (color, clip) = context.Paint.Inspect(region);
        if (!PdfContentRedaction.ClipHolds(clip, bounds)) throw new NotSupportedException("The paragraph is clipped.");
        if (page.Letters.Any(letter => CenterInside(letter.BoundingBox, bounds) && !selectedSet.Contains(letter)))
            throw new NotSupportedException("The source bounds overlap other text and cannot identify one paragraph safely.");
        double leading = rows.Count > 1 ? rows[0].Baseline - rows[1].Baseline : first.Size * 1.2;
        string text = string.Join(" ", rows.Select(row => row.Text));
        var candidate = new PdfParagraphCandidate(pageIndex, bounds, text, first.Font, first.Size, leading, color)
        {
            Clip = clip,
            MixedStyles = selected.Select(letter => (letter.FontName, Math.Round(letter.PointSize, 2))).Distinct().Skip(1).Any()
        };
        return new PdfParagraphInspection(true, string.Empty, candidate);
    }

    /// <summary>Creates a source-bound layout and an exact, unapplied preview when it fits.</summary>
    public static PdfParagraphPlan Plan(PdfDocument document, int pageIndex, PdfContentBounds sourceRegion,
        PdfContentBounds targetBounds, string text, TrueTypeFont font, double fontSize, double leading,
        PdfParagraphAlignment alignment)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        if (text.Length > 100_000) throw new ArgumentException("Paragraph text exceeds the size limit.", nameof(text));
        PdfParagraphInspection inspection = Inspect(document, pageIndex, sourceRegion);
        PdfParagraphCandidate source = inspection.Candidate ?? throw new NotSupportedException(inspection.Reason);
        PdfPageContent page = new PdfPageContentReader(document).Read(pageIndex);
        ValidateBounds(targetBounds, page.Width, page.Height);
        if (!double.IsFinite(fontSize) || fontSize <= 0 || fontSize > 1000)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (!double.IsFinite(leading) || leading <= 0 || leading > 3000)
            throw new ArgumentOutOfRangeException(nameof(leading));
        if (!Enum.IsDefined(alignment)) throw new ArgumentOutOfRangeException(nameof(alignment));
        if (!font.EmbeddingAllowed) throw new NotSupportedException("The replacement font does not permit embedding.");
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        // The replacement paints under the source's clip, so a box outside it would vanish.
        if (normalized.Length != 0 && !PdfContentRedaction.ClipHolds(
            PdfContentRedaction.InspectParagraphPaint(document, pageIndex, source.SourceRegion).Clip, targetBounds))
            throw new NotSupportedException("The paragraph box extends past the clipped area of the source text.");
        foreach (Rune rune in normalized.EnumerateRunes())
        {
            if (rune.Value == '\n') continue;
            // This slice has no shaping engine. Combining marks, bidi text, and contextual scripts need one.
            bool ordinaryPunctuation = rune.Value is 0x2018 or 0x2019 or 0x201C or 0x201D
                or 0x2022 or 0x2026 or 0x20AC or 0x2122;
            if ((rune.Value > 0x024F && !ordinaryPunctuation) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
                or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                throw new NotSupportedException("This text needs shaping that paragraph editing does not yet support.");
            if (rune.Value != ' ' && font.GetGlyphId(rune.Value) == 0)
                throw new NotSupportedException($"{font.PostScriptName} has no glyph for {rune}.");
        }
        // Reflow and authoring must use the same space glyph. Missing spaces must not silently become .notdef.
        if (normalized.Contains(' ') && font.GetGlyphId(' ') == 0)
            throw new NotSupportedException("The replacement font has no space glyph.");
        IReadOnlyList<PdfReflowLine> wrapped = normalized.Length == 0 ? []
            : PdfTextReflow.Wrap(normalized, font, fontSize, targetBounds.Width);
        double ascent = Math.Max(font.Ascender, font.Bounds.YMax) * fontSize / font.UnitsPerEm;
        double descent = Math.Min(font.Descender, font.Bounds.YMin) * fontSize / font.UnitsPerEm;
        var lines = wrapped.Select((line, index) => new PdfParagraphLine(line.Text,
            targetBounds.Left + (alignment == PdfParagraphAlignment.Center ? (targetBounds.Width - line.Width) / 2
                : alignment == PdfParagraphAlignment.Right ? targetBounds.Width - line.Width : 0),
            targetBounds.Top - ascent - index * leading, line.Width)).ToArray();
        string overflow = lines.Any(line => line.Width > targetBounds.Width + 0.0001
            || line.Baseline + descent < targetBounds.Bottom - 0.0001)
            ? "The text overflows the paragraph box. Resize the box or adjust the font size or line spacing." : string.Empty;
        if (overflow.Length == 0 && lines.Any(line => line.Text.Length != 0))
        {
            // Check actual glyph ink as well as advances, since italic overhangs can cross a box edge.
            var content = new PdfContentStreamBuilder().BeginText().SetFont(font, fontSize);
            foreach (PdfParagraphLine line in lines)
                if (line.Text.Length != 0)
                    content.SetTextMatrix(1, 0, 0, 1, line.X, line.Baseline).ShowUnicodeText(line.Text);
            content.EndText();
            PdfDocument layout = PdfDocument.Open(new PdfDocumentBuilder().AddPage(page.Width, page.Height, content).Build());
            if (new PdfPageContentReader(layout).Read(0).Letters.Any(letter =>
                    letter.BoundingBox.Left < targetBounds.Left - 0.0001
                    || letter.BoundingBox.Right > targetBounds.Right + 0.0001
                    || letter.BoundingBox.Bottom < targetBounds.Bottom - 0.0001
                    || letter.BoundingBox.Top > targetBounds.Top + 0.0001))
                overflow = "The glyphs extend outside the paragraph box. Resize the box or adjust the text.";
        }
        byte[]? preview = null;
        if (overflow.Length == 0)
        {
            var edit = new PdfTextLineEdit(pageIndex, source.SourceRegion, string.Join("\n", lines.Select(line => line.Text)))
            {
                Substitute = font, UseSubstitute = true, FontSize = fontSize,
                ParagraphLines = Array.AsReadOnly(lines),
                ParagraphBox = lines.Any(line => line.Text.Length != 0) ? targetBounds : null
            };
            preview = PdfTextLineEditor.Replace(document, edit).Document;
        }
        return new PdfParagraphPlan(document, source, targetBounds, normalized, font, fontSize, leading,
            alignment, lines, overflow, preview);
    }

    /// <summary>Returns the preview bytes after verifying that the source has not changed.</summary>
    public static byte[] Apply(PdfDocument document, PdfParagraphPlan plan)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(plan);
        if (!SHA256.HashData(document.Source.Span).AsSpan().SequenceEqual(plan.SourceHash))
            throw new InvalidOperationException("The document changed after the paragraph preview was prepared.");
        return plan.GetPreviewDocument();
    }

    private static void ValidateBounds(PdfContentBounds bounds, double width, double height)
    {
        if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Bottom) || !double.IsFinite(bounds.Right)
            || !double.IsFinite(bounds.Top) || bounds.Width <= 0 || bounds.Height <= 0
            || bounds.Left < 0 || bounds.Bottom < 0 || bounds.Right > width || bounds.Top > height)
            throw new ArgumentOutOfRangeException(nameof(bounds), "The paragraph box must be finite, nonempty, and inside the page.");
    }

    private static bool CenterInside(PdfContentBounds glyph, PdfContentBounds area) =>
        (glyph.Left + glyph.Right) / 2 >= area.Left && (glyph.Left + glyph.Right) / 2 <= area.Right
        && (glyph.Bottom + glyph.Top) / 2 >= area.Bottom && (glyph.Bottom + glyph.Top) / 2 <= area.Top;
    private static PdfName Name(string text) => new(Encoding.ASCII.GetBytes(text));
    private static bool IsTagged(PdfPageTree tree) => tree.Catalog.ContainsKey(Name("StructTreeRoot"));
    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32) throw new FormatException("Reference nesting limit exceeded.");
            value = document.Resolve(reference);
        }
        return value;
    }
    private static double Number(PdfDocument document, PdfObject value) => Resolve(document, value) switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => throw new FormatException("Expected a number.")
    };
}

public static partial class PdfContentRedaction
{
    internal static bool ClipHolds(PdfContentBounds clip, PdfContentBounds area) =>
        area.Left >= clip.Left - 0.01 && area.Bottom >= clip.Bottom - 0.01
        && area.Right <= clip.Right + 0.01 && area.Top <= clip.Top + 0.01;

    internal static (PdfRgbColor Color, PdfContentBounds Clip) InspectParagraphPaint(PdfDocument document, int pageIndex,
        PdfContentBounds region) => ParagraphPaint(document, Probe(document, pageIndex, region, null, default, paragraph: true));

    private static (PdfRgbColor Color, PdfContentBounds Clip) ParagraphPaint(PdfDocument document, LineProbe probe)
    {
        if (probe.Inspection.CanEdit && probe.Insertion.ClipUnsafe)
            throw new NotSupportedException("The paragraph is clipped by a shape that is not a single rectangle.");
        if (!probe.Inspection.CanEdit || probe.Insertion.ParagraphUnsafe)
            throw new NotSupportedException("The paragraph has unsupported transformations, rendering, or mixed styles.");
        TextState state = probe.Insertion.State!;
        if (state.FontObject is null || Resolve(document, state.FontObject) is not PdfDictionary sourceFont
            || IsNamed(sourceFont, SubtypeName, "Type3"))
            throw new NotSupportedException("The paragraph uses a font whose painted appearance cannot be preserved.");
        PdfContentInstruction[] fill = state.Fill;
        if (fill.Length == 0) return (new PdfRgbColor(0, 0, 0), state.Clip);
        if (fill.Length == 1 && fill[0].Operator == "rg" && fill[0].Operands.Count == 3)
            return (new PdfRgbColor(Number(document, fill[0].Operands[0]), Number(document, fill[0].Operands[1]),
                Number(document, fill[0].Operands[2])), state.Clip);
        if (fill.Length == 1 && fill[0].Operator == "g" && fill[0].Operands.Count == 1)
        {
            double gray = Number(document, fill[0].Operands[0]);
            return (new PdfRgbColor(gray, gray, gray), state.Clip);
        }
        throw new NotSupportedException("The paragraph uses a color space that is not supported for paragraph editing.");
    }
}
