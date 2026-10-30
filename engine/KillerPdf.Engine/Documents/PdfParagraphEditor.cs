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
    string FontName, double FontSize, double Leading, PdfRgbColor Color);

/// <summary>The eligibility of the complete source lines inside a selected area.</summary>
public sealed record PdfParagraphInspection(bool CanEdit, string Reason, PdfParagraphCandidate? Candidate)
{
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
        PdfPageContent page = new PdfPageContentReader(document).Read(pageIndex);
        var result = new List<PdfParagraphCandidate>();
        var refusals = new List<PdfParagraphInspection>();
        var group = new List<PdfExtractedLine>();
        bool tagged = IsTagged(PdfPageTree.Read(document));
        foreach (PdfExtractedLine line in page.Lines)
        {
            if (line.Runs.Count == 0 || line.Runs.Any(run => run.Letters.Count == 0)) { Flush(); continue; }
            if (group.Count > 0)
            {
                PdfExtractedLine previous = group[^1];
                double size = previous.Runs[0].PointSize;
                double gap = previous.Runs[0].Letters[0].StartBaseLine.Y - line.Runs[0].Letters[0].StartBaseLine.Y;
                if (Math.Abs(previous.BoundingBox.Left - line.BoundingBox.Left) > 1
                    || gap < size * 0.8 || gap > size * 1.8
                    || previous.Runs[0].FontName != line.Runs[0].FontName
                    || Math.Abs(size - line.Runs[0].PointSize) > 0.01
                    || tagged && previous.Runs[0].Letters[0].MarkedContentId != line.Runs[0].Letters[0].MarkedContentId) Flush();
            }
            group.Add(line);
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
            var bounds = new PdfContentBounds(group.Min(line => line.BoundingBox.Left),
                group.Min(line => line.BoundingBox.Bottom), group.Max(line => line.BoundingBox.Right),
                group.Max(line => line.BoundingBox.Top));
            PdfParagraphInspection inspection = Inspect(document, pageIndex, bounds);
            if (inspection.Candidate is { } candidate) result.Add(candidate);
            else refusals.Add(inspection);
            group.Clear();
        }
    }

    /// <summary>Inspects complete source lines and reports unsupported cases without mutation.</summary>
    public static PdfParagraphInspection Inspect(PdfDocument document, int pageIndex, PdfContentBounds sourceRegion)
    {
        ArgumentNullException.ThrowIfNull(document);
        try { return InspectCore(document, pageIndex, sourceRegion); }
        catch (Exception error) when (error is NotSupportedException or FormatException or InvalidOperationException
            or ArgumentException)
        { return new PdfParagraphInspection(false, error.Message, null); }
    }

    private static PdfParagraphInspection InspectCore(PdfDocument document, int pageIndex, PdfContentBounds region)
    {
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
            return new PdfParagraphInspection(false, "Paragraph editing is not supported on rotated pages.", null)
                { RefusalCode = PdfParagraphRefusalCode.RotatedPage };
        PdfPageContent page = new PdfPageContentReader(document).Read(pageIndex);
        ValidateBounds(region, page.Width, page.Height);
        if (page.Diagnostics.Count != 0)
            throw new NotSupportedException("The page has extraction diagnostics and cannot be edited as a paragraph.");
        var selected = page.Letters.Where(letter => CenterInside(letter.BoundingBox, region)).ToArray();
        if (selected.Length == 0) throw new NotSupportedException("Select a complete paragraph of readable text.");
        if (selected.Length > 100_000) throw new NotSupportedException("The source paragraph exceeds the size limit.");
        // The replacement goes into the first glyph's element, so one element keeps the structure true.
        if (IsTagged(tree) && selected.Select(letter => letter.MarkedContentId).Distinct().Skip(1).Any())
            throw new NotSupportedException("The selection spans several tagged elements.");
        var selectedSet = new HashSet<PdfExtractedLetter>(selected, ReferenceEqualityComparer.Instance);
        PdfExtractedLine[] lines = [.. page.Lines.Where(line => line.Runs.SelectMany(run => run.Letters)
            .Any(selectedSet.Contains))];
        if (lines.SelectMany(line => line.Runs).SelectMany(run => run.Letters).Any(letter => !selectedSet.Contains(letter)))
            throw new NotSupportedException("The selection cuts a source line. Select complete lines.");
        PdfExtractedLetter first = selected[0];
        if (selected.Any(letter => letter.WritingDirection != PdfWritingDirection.LeftToRight
            || Math.Abs(letter.EndBaseLine.Y - letter.StartBaseLine.Y) > 0.001
            || letter.FontName != first.FontName || Math.Abs(letter.PointSize - first.PointSize) > 0.001))
            throw new NotSupportedException("Only horizontal paragraphs with one font and size are supported.");
        foreach (PdfExtractedLine line in lines)
        {
            PdfExtractedLetter[] letters = [.. line.Runs.SelectMany(run => run.Letters)];
            for (int index = 1; index < letters.Length; index++)
                if (letters[index].StartBaseLine.X - letters[index - 1].EndBaseLine.X > first.PointSize * 1.5)
                    throw new NotSupportedException("The source line has separated columns or uncertain spacing.");
        }
        for (int index = 1; index < lines.Length; index++)
        {
            double gap = lines[index - 1].Runs[0].Letters[0].StartBaseLine.Y - lines[index].Runs[0].Letters[0].StartBaseLine.Y;
            if (Math.Abs(lines[index].BoundingBox.Left - lines[0].BoundingBox.Left) > 1
                || gap < first.PointSize * 0.8 || gap > first.PointSize * 1.8)
                throw new NotSupportedException("The selection is not one simple aligned paragraph.");
        }
        var bounds = new PdfContentBounds(selected.Min(letter => letter.BoundingBox.Left),
            selected.Min(letter => letter.BoundingBox.Bottom), selected.Max(letter => letter.BoundingBox.Right),
            selected.Max(letter => letter.BoundingBox.Top));
        var (color, clip) = PdfContentRedaction.InspectParagraphPaint(document, pageIndex, region);
        if (!PdfContentRedaction.ClipHolds(clip, bounds)) throw new NotSupportedException("The paragraph is clipped.");
        if (page.Letters.Any(letter => CenterInside(letter.BoundingBox, bounds) && !selectedSet.Contains(letter)))
            throw new NotSupportedException("The source bounds overlap other text and cannot identify one paragraph safely.");
        double leading = lines.Length > 1
            ? lines[0].Runs[0].Letters[0].StartBaseLine.Y - lines[1].Runs[0].Letters[0].StartBaseLine.Y
            : first.PointSize * 1.2;
        string text = string.Join(" ", lines.Select(line => line.Text.Trim()));
        var candidate = new PdfParagraphCandidate(pageIndex, bounds, text, first.FontName, first.PointSize, leading, color);
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
                ParagraphLines = Array.AsReadOnly(lines)
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
        PdfContentBounds region)
    {
        LineProbe probe = Probe(document, pageIndex, region, null, default, paragraph: true);
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
