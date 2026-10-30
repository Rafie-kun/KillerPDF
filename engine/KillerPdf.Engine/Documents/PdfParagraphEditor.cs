using System.Globalization;
using System.Runtime.CompilerServices;
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

    /// <summary>Gets whether the source mixes fonts, sizes or colors. <see cref="FontName"/>,
    /// <see cref="FontSize"/> and <see cref="Color"/> are then its dominant style, which the
    /// replacement takes.</summary>
    public bool MixedStyles { get; init; }

    /// <summary>Gets where the glyph origins of the paragraph's body lines start, which ink bounds
    /// do not show because side bearings move them.</summary>
    public double TextLeft { get; init; }

    /// <summary>Gets the baseline of the first line.</summary>
    public double FirstBaseline { get; init; }

    /// <summary>Gets where the last glyph advance ends on the longest line.</summary>
    public double TextRight { get; init; }

    /// <summary>Gets how far the first line starts to the right of <see cref="TextLeft"/>.</summary>
    public double FirstLineIndent { get; init; }

    /// <summary>Gets the left edge of the nearest other text to the right on the paragraph's rows,
    /// where a default box has to stop, or null when nothing lies there.</summary>
    public double? RightLimit { get; init; }

    /// <summary>Gets the text of each source line, in reading order.</summary>
    public IReadOnlyList<string> RowTexts { get; init; } = [];

    /// <summary>Gets each word joined across a line end with its hyphen dropped when nothing in the
    /// document shows whether the hyphen belongs to it, as it reads in <see cref="Text"/>.</summary>
    public IReadOnlyList<string> HyphenGuesses { get; init; } = [];

    // Where each source line sits, so an underline drawn beneath it can leave with it.
    internal IReadOnlyList<PdfParagraphRowSpan> RowSpans { get; init; } = [];

    /// <summary>Returns the part of a replacement box that lies inside <see cref="Clip"/>, since
    /// the replacement is painted under the same clip and anything outside it would vanish.</summary>
    public PdfContentBounds FitToClip(PdfContentBounds box) => Clip is { } clip
        ? new PdfContentBounds(Math.Max(box.Left, clip.Left), Math.Max(box.Bottom, clip.Bottom),
            Math.Min(box.Right, clip.Right), Math.Min(box.Top, clip.Top)) : box;
}

internal readonly record struct PdfParagraphRowSpan(double Left, double Right, double Baseline, double Size);

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
    TaggedDocument,
    /// <summary>The page has no text to edit, such as a scan or a page of pictures.</summary>
    NoText
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
/// <remarks>A paragraph is a run of complete left-to-right lines, across any number of top-level
/// text objects, painted upright under one transformation, one rectangular clip and one graphics
/// state; in a tagged document they must share one block element. A first-line indent is kept.
/// Mixed fonts, sizes and colors are accepted, and the replacement takes the dominant style.
/// Encrypted, signed and rotated pages are refused, as are tables, Type 3 fonts, and paragraphs that
/// are soft masked, on an optional content layer, carrying replacement text, or inside reusable
/// form content; the rest of the page does not matter. Replacement text uses one explicitly
/// supplied embeddable font, Latin glyphs, and a small explicit set of ordinary typographic
/// punctuation and symbols.</remarks>
public static class PdfParagraphEditor
{
    private const double WordGapEm = 0.15;
    // A line takes one em whatever the font, so a baseline does not depend on the font's metrics.
    private const double LineAscent = 0.8, LineDescent = 0.2;
    // The least a typeset word space comes to, and what a box leaves before the next column.
    private const double SpaceEm = 0.25;

    private static readonly HashSet<string> InlineElements = new(StringComparer.Ordinal)
        { "Span", "Link", "Em", "Strong", "Quote", "Code", "Reference", "Sub" };

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
        catch (Exception error) when (IsRefusal(error)) { blocked = new PdfParagraphInspection(false, RefusalReason(error), null); }
        if (blocked is not null || context is null)
            return new([], Array.AsReadOnly(new[] { blocked! }));
        PdfPageContent page = context.Content;
        var result = new List<PdfParagraphCandidate>();
        var refusals = new List<PdfParagraphInspection>();
        var group = new List<ParagraphRow>();
        bool tagged = IsTagged(context.Tree);
        List<ParagraphRow> rows = Rows(page.Lines);
        for (int index = 0; index < rows.Count; index++)
        {
            ParagraphRow row = rows[index];
            if (group.Count > 0 && (!Continues(group, group.Count, row, group[^1].Style.Size)
                || !SharesStyle(group, row, index + 1 < rows.Count ? rows[index + 1] : null)
                || tagged && context.Structure.ElementOf(group[^1].Letters[0].MarkedContentId)
                    != context.Structure.ElementOf(row.Letters[0].MarkedContentId))) Flush();
            group.Add(row);
        }
        Flush();
        if (result.Count == 0 && refusals.Count == 0)
        {
            if (page.Letters.Count == 0)
                return new([], Array.AsReadOnly(new[] { new PdfParagraphInspection(false, "The page has no text to edit.", null)
                    { RefusalCode = PdfParagraphRefusalCode.NoText } }));
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
            { inspection = new PdfParagraphInspection(false, RefusalReason(error), null) { Region = bounds }; }
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
        { return new PdfParagraphInspection(false, RefusalReason(error), null); }
    }

    private sealed record ParagraphPage(PdfPageTree Tree, PdfPageContent Content, PdfContentRedaction.ParagraphPaintMap Paint,
        ParagraphStructure Structure);

    // What the structure tree says about this page's marked content.
    private sealed class ParagraphStructure
    {
        // Ids whose element, or an ancestor, carries replacement text. Assistive technology reads
        // that in place of the page text, so an edit would leave it stale.
        internal HashSet<int> Described { get; } = [];
        // The block element holding each id, since one paragraph can own several ids and inline
        // elements such as links sit inside it.
        internal Dictionary<int, int> Elements { get; } = [];

        internal int? ElementOf(int? id) => id is int known && Elements.TryGetValue(known, out int element) ? element
            : id is null ? null : -1 - id;
    }

    private static ParagraphStructure ReadStructure(PdfDocument document, PdfPageTree tree, int pageIndex)
    {
        var structure = new ParagraphStructure();
        if (!IsTagged(tree)) return structure;
        int page = tree.Pages[pageIndex].Reference.ObjectNumber, elements = 0;
        var visited = new HashSet<(int Object, bool Covered, bool OnPage)>();
        Visit(tree.Catalog[Name("StructTreeRoot")], false, true, 0, 0);
        return structure;

        void Visit(PdfObject value, bool covered, bool onPage, int block, int depth)
        {
            if (depth > 256) throw new NotSupportedException("The document structure is too deeply nested to edit a paragraph.");
            // A shared element can gain replacement text or its page on a later path, so the state counts too.
            if (value is PdfIndirectReference reference && !visited.Add((reference.ObjectNumber, covered, onPage))) return;
            switch (Resolve(document, value))
            {
                case PdfInteger id when onPage:
                    if (covered) structure.Described.Add((int)id.Value);
                    structure.Elements.TryAdd((int)id.Value, block);
                    break;
                case PdfArray array:
                    foreach (PdfObject item in array) Visit(item, covered, onPage, block, depth + 1);
                    break;
                case PdfDictionary element:
                    // An MCR with /Stm counts inside that form, in numbering the page does not share.
                    if (element.ContainsKey(Name("Stm"))) return;
                    bool here = onPage;
                    if (element.TryGetValue(Name("Pg"), out PdfObject? pageValue))
                        here = pageValue is PdfIndirectReference pageReference && pageReference.ObjectNumber == page;
                    bool carries = covered || element.ContainsKey(Name("ActualText")) || element.ContainsKey(Name("Alt"))
                        || element.ContainsKey(Name("E"));
                    if (element.TryGetValue(Name("S"), out PdfObject? type)
                        && !(Resolve(document, type) is PdfName name && InlineElements.Contains(name.ValueAsLatin1())))
                        block = ++elements;
                    if (element.TryGetValue(Name("MCID"), out PdfObject? content)) Visit(content, carries, here, block, depth + 1);
                    if (element.TryGetValue(Name("K"), out PdfObject? kids)) Visit(kids, carries, here, block, depth + 1);
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
        // Where the glyph origins start. Ink starts elsewhere, by each glyph's side bearing.
        internal double Left { get; private set; }
        internal double Right => Lines.Max(line => line.BoundingBox.Right);
        internal double End => Letters.Max(letter => letter.EndBaseLine.X);

        // Only explicit spaces and gaps between glyphs separate words here, so gaps count as spaces.
        // The threshold sits below the one word search uses, because a tight typeset line keeps its
        // word gaps close to 0.15 em and a kern stays well under it.
        internal string Text
        {
            get
            {
                var text = new StringBuilder();
                double tracking = Tracking();
                PdfExtractedLetter? previous = null;
                foreach (PdfExtractedLetter letter in Letters)
                {
                    if (string.IsNullOrWhiteSpace(letter.Value))
                    {
                        if (letter.Value.Length != 0 && text.Length > 0 && text[^1] != ' ') text.Append(' ');
                        previous = null;
                        continue;
                    }
                    if (previous is not null && WordGap(previous, letter, tracking)) text.Append(' ');
                    text.Append(letter.Value);
                    previous = letter;
                }
                return ExpandLigatures(text.ToString().Trim());
            }
        }

        // How wide the row's first word is, for telling whether it would have fit on the row above.
        internal double FirstWordWidth
        {
            get
            {
                double tracking = Tracking();
                PdfExtractedLetter? first = null, previous = null;
                foreach (PdfExtractedLetter letter in Letters)
                {
                    bool blank = string.IsNullOrWhiteSpace(letter.Value);
                    if (first is null && blank) continue;
                    if (blank || previous is not null && WordGap(previous, letter, tracking)) break;
                    first ??= letter;
                    previous = letter;
                }
                return previous is null ? 0 : previous.EndBaseLine.X - first!.StartBaseLine.X;
            }
        }

        // What the row spends on a word space, which a monospaced font makes far wider than usual.
        internal double SpaceWidth => Math.Max(Style.Size * SpaceEm, Letters.Where(letter => letter.Value == " ")
            .Select(letter => letter.EndBaseLine.X - letter.StartBaseLine.X).DefaultIfEmpty().Max());

        private static bool WordGap(PdfExtractedLetter previous, PdfExtractedLetter letter, double tracking) =>
            letter.StartBaseLine.X - previous.EndBaseLine.X
                > tracking + Math.Max(1, Math.Min(previous.PointSize, letter.PointSize)) * WordGapEm;

        // Letter spacing widens every gap in a row, so word gaps are told from the row's usual one.
        // The lower median is zero for ordinary text, even set with no space glyphs at all. Three
        // pairs is the least that shows a habit, and digits do not count, since a run of spaced
        // figures looks just like tracked letters and is far more often separate numbers.
        private double Tracking()
        {
            var gaps = new List<double>();
            PdfExtractedLetter? previous = null;
            foreach (PdfExtractedLetter letter in Letters)
            {
                bool counts = letter.Value.Length != 0 && char.IsLetter(letter.Value[0]);
                if (counts && previous is not null) gaps.Add(letter.StartBaseLine.X - previous.EndBaseLine.X);
                previous = counts ? letter : null;
            }
            if (gaps.Count < 3) return 0;
            gaps.Sort();
            return Math.Max(0, gaps[(gaps.Count - 1) / 2]);
        }

        internal void Add(PdfExtractedLine line)
        {
            Lines.Add(line);
            Lines.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
            Letters = [.. Lines.SelectMany(item => item.Runs).SelectMany(run => run.Letters)];
            Style = DominantStyle(Letters);
            (string font, double size) = Style;
            Baseline = Letters.First(letter => letter.FontName == font
                && Math.Round(letter.PointSize, 2) == size).StartBaseLine.Y;
            IEnumerable<PdfExtractedLetter> shown = Letters.Where(letter => !string.IsNullOrWhiteSpace(letter.Value));
            Left = (shown.Any() ? shown : Letters).Min(letter => letter.StartBaseLine.X);
        }
    }

    // Only the first row may be indented, and only rightward, so a row that starts to the right of
    // the one above opens a new paragraph. Every later row lines up with the second.
    private static bool Continues(IReadOnlyList<ParagraphRow> rows, int count, ParagraphRow row, double size)
    {
        double gap = rows[count - 1].Baseline - row.Baseline;
        if (gap < size * 0.8 || gap > size * 1.8) return false;
        if (count >= 2) return Math.Abs(rows[1].Left - row.Left) <= 1;
        double indent = rows[0].Left - row.Left;
        return Math.Abs(indent) <= 1 || indent > 1 && indent <= size * 5;
    }

    // A whole line in another font, such as one italic line, stays when the next row returns to
    // the paragraph's font. That is what tells it from a bold heading above its body text.
    private static bool SharesStyle(List<ParagraphRow> group, ParagraphRow row, ParagraphRow? next)
    {
        (string font, double size) = group[0].Style;
        if (Math.Abs(size - row.Style.Size) > 0.01) return false;
        return row.Style.Font == font || next is not null && next.Style.Font == font
            && Math.Abs(size - next.Style.Size) <= 0.01 && Math.Abs(next.Left - row.Left) <= 1;
    }

    // Table columns line up from row to row, and the wide gaps of justified or typewritten text only
    // do by chance, so every wide gap of a row must find one in the next row. A wide gap that sits
    // where all the other rows leave any gap is a column too, since cells can nearly touch. Right
    // aligned cells end together instead, but so does every justified line, so all the rows have
    // to agree before that counts.
    private static bool LooksLikeTable(IReadOnlyList<ParagraphRow> rows, double size)
    {
        // A monospaced word space is 0.6 em.
        const double ColumnGapEm = 0.65;
        (double X, double Gap, double End)[][] pieces = [.. rows.Select(row => Pieces(row, size))];
        double[][] wide = [.. pieces.Select(row => row.Where(piece => piece.Gap > size * ColumnGapEm).Select(piece => piece.X).ToArray())];
        for (int index = 1; index < wide.Length; index++)
            if (wide[index].Length > 0 && wide[index - 1].Length > 0
                && (AllFound(wide[index], wide[index - 1]) || AllFound(wide[index - 1], wide[index]))) return true;
        if (rows.Count > 1 && wide.SelectMany(row => row).Any(x => pieces.All(row => AllFound([x], [.. row.Select(piece => piece.X)])))) return true;
        double[][] ends = [.. pieces.Select(row => row.Where(piece => piece.Gap > size * ColumnGapEm).Select(piece => piece.End).ToArray())];
        return rows.Count > 1 && ends[0].Length > 0 && ends.All(row => row.Length == ends[0].Length && AllFound(row, ends[0]));

        static bool AllFound(double[] some, double[] others) =>
            some.All(x => others.Any(other => Math.Abs(other - x) <= 1));
    }

    // The pieces of a row between gaps. Spaces leave no gap of their own between their neighbours, so
    // two or more together, which pad a column out to its place, count as one gap across the run. A
    // single space is a word space however justification stretches it.
    private static (double X, double Gap, double End)[] Pieces(ParagraphRow row, double size)
    {
        var pieces = new List<(double X, double Gap, double End)>();
        PdfExtractedLetter? mark = null, previous = null;
        int spaces = 0;
        foreach (PdfExtractedLetter letter in row.Letters)
        {
            if (string.IsNullOrWhiteSpace(letter.Value))
            {
                spaces++;
                previous = letter;
                continue;
            }
            if (mark is not null && letter.StartBaseLine.X - (spaces >= 2 ? mark : previous!).EndBaseLine.X is var gap && gap >= size * 0.1)
            {
                if (pieces.Count > 0) pieces[^1] = pieces[^1] with { End = mark.EndBaseLine.X };
                pieces.Add((letter.StartBaseLine.X, gap, 0));
            }
            mark = previous = letter;
            spaces = 0;
        }
        if (pieces.Count > 0) pieces[^1] = pieces[^1] with { End = mark!.EndBaseLine.X };
        return [.. pieces];
    }

    // A word broken across two rows is one word again, and a row that stopped with room for the next
    // word ended on purpose, so it stays a row. A list or an address does that on most of its rows,
    // which says the same of the longest one, where there was no room to tell by. The room is judged
    // against the longest row, which is never wider than the column the text was set in.
    private static string JoinRows(IReadOnlyList<ParagraphRow> rows, IReadOnlyList<string> texts, ParagraphWords words,
        int pageIndex, List<string> guesses)
    {
        double longest = rows.Max(row => row.End);
        bool[] ended = [.. rows.Select((row, index) => index + 1 < rows.Count
            && longest - row.End > row.SpaceWidth + rows[index + 1].FirstWordWidth)];
        bool allRows = ended.Count(end => end) * 2 >= rows.Count - 1 && ended.Any(end => end);
        var text = new StringBuilder(texts[0]);
        for (int index = 1; index < rows.Count; index++)
        {
            string previous = texts[index - 1], row = texts[index];
            bool hyphen = previous.Length >= 2 && previous[^1] is '-' or '‐' && char.IsLetterOrDigit(previous[^2]) && row.Length > 0;
            // A digit carries a compound on, whatever room the row had: COVID- ahead of 19 is no list item.
            if (hyphen && (!ended[index - 1] || !char.IsLetter(row[0])))
            {
                bool? keeps = KeepsHyphen(previous, row, words, pageIndex);
                if (keeps == true) text[^1] = '-';
                else text.Length--;
                if (keeps is null) guesses.Add(Bare(previous[(previous.LastIndexOf(' ') + 1)..^1]) + string.Concat(row.TakeWhile(char.IsLetter)));
            }
            else if (previous[^1] == '\u00AD') text.Length--;
            else if (ended[index - 1] || allRows || previous.EndsWith(" \\", StringComparison.Ordinal)) text.Append('\n');
            else if (!ContinuesAddress(previous, row)) text.Append(' ');
            text.Append(row);
        }
        return text.ToString();
    }

    // Any word with a slash can break after it, as and/or does. Past that, the row has to stop
    // inside a web address and the next one has to go on as a path.
    private static bool ContinuesAddress(string previous, string row) =>
        previous.Length >= 2 && (previous[^1] == '/' && previous[^2] != ' '
            || char.IsLetterOrDigit(previous[^1]) && row.Split(' ')[0].Contains('/')
                && previous[(previous.LastIndexOf(' ') + 1)..].Contains("://", StringComparison.Ordinal));

    // Soft breaks fall between lowercase letters of a word, and an address holds no words. After
    // that the document's own spelling decides, the hyphenated form first because a real hyphen is
    // the one never to lose. When neither form is written anywhere, a longer compound that holds it
    // or both halves being words on their own shows a compound. Null means nothing shows either.
    private static bool? KeepsHyphen(string previous, string row, ParagraphWords words, int pageIndex)
    {
        if (!char.IsLower(previous[^2]) || !char.IsLower(row[0])) return true;
        string left = Bare(previous[(previous.LastIndexOf(' ') + 1)..^1]).Replace('‐', '-');
        if ((left + row.Split(' ')[0]).AsSpan().IndexOfAny("/@=%") >= 0) return true;
        string right = string.Concat(row.TakeWhile(char.IsLetter));
        if (words.Contains(left + "-" + right, pageIndex)) return true;
        if (words.Contains(left + right, pageIndex)) return false;
        if (words.InCompound(left + "-" + right, pageIndex)) return true;
        string half = left[(left.LastIndexOf('-') + 1)..];
        return half.Length >= 2 && right.Length >= 2 && words.Contains(half, pageIndex) && words.Contains(right, pageIndex)
            ? true : null;
    }

    private static string Bare(string word)
    {
        int start = 0, end = word.Length;
        while (start < end && !char.IsLetterOrDigit(word[start])) start++;
        while (end > start && !char.IsLetterOrDigit(word[end - 1])) end--;
        return word[start..end];
    }

    // The words a document writes whole, for settling line-end hyphens. Reading them costs a pass
    // over the pages, so it waits for the first hyphen that asks and then lasts as long as the
    // document. The two pieces of a word broken across rows are not words, and they would pass for
    // proof of each other, though a hyphen inside one is still a real one.
    private sealed class ParagraphWords(PdfDocument document)
    {
        private const int PagesRead = 300;
        private static readonly ConditionalWeakTable<PdfDocument, ParagraphWords> ByDocument = [];
        private readonly HashSet<string> _words = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _compounds = [];
        private readonly HashSet<int> _read = [];

        internal static ParagraphWords Of(PdfDocument document) => ByDocument.GetValue(document, item => new ParagraphWords(item));

        internal bool Contains(string word, int pageIndex)
        {
            lock (_words)
            {
                ReadPages(pageIndex);
                return _words.Contains(word);
            }
        }

        // Whether a longer compound holds this one, as non-sub-graphs does sub-graph. Tokens can run
        // together where a page lost its word spaces, so it may sit anywhere inside.
        internal bool InCompound(string word, int pageIndex)
        {
            lock (_words)
            {
                ReadPages(pageIndex);
                return _compounds.Any(item => item.Contains(word, StringComparison.OrdinalIgnoreCase));
            }
        }

        private void ReadPages(int pageIndex)
        {
            if (_read.Count == 0)
            {
                var reader = new PdfPageContentReader(document);
                for (int index = 0; index < Math.Min(PagesRead, reader.PageCount); index++) Read(reader, index);
            }
            if (!_read.Contains(pageIndex)) Read(new PdfPageContentReader(document), pageIndex);
        }

        private void Read(PdfPageContentReader reader, int pageIndex)
        {
            _read.Add(pageIndex);
            IReadOnlyList<PdfExtractedWord> words;
            try { words = reader.Read(pageIndex).Words; }
            catch (Exception error) when (IsRefusal(error) || error is Filters.PdfFilterException) { return; }
            bool broken = false;
            for (int index = 0; index < words.Count; index++)
            {
                PdfExtractedWord word = words[index];
                bool breaks = index + 1 < words.Count && word.Text.TrimEnd() is { Length: >= 2 } text && text[^1] is '-' or '‐'
                    && char.IsLetter(text[^2]) && words[index + 1].BoundingBox.Left < word.BoundingBox.Right
                    && words[index + 1].BoundingBox.Top < word.BoundingBox.Bottom + 1;
                string whole = Bare(ExpandLigatures(word.Text)).Replace('‐', '-');
                if (!broken && !breaks) _words.Add(whole);
                if (whole.Contains('-')) _compounds.Add(whole);
                broken = breaks;
            }
        }
    }

    private static string ExpandLigatures(string text) => text.Replace("ﬀ", "ff", StringComparison.Ordinal)
        .Replace("ﬁ", "fi", StringComparison.Ordinal).Replace("ﬂ", "fl", StringComparison.Ordinal)
        .Replace("ﬃ", "ffi", StringComparison.Ordinal).Replace("ﬄ", "ffl", StringComparison.Ordinal)
        .Replace("ﬅ", "st", StringComparison.Ordinal).Replace("ﬆ", "st", StringComparison.Ordinal);

    private static List<ParagraphRow> Rows(IEnumerable<PdfExtractedLine> lines)
    {
        var rows = new List<ParagraphRow>();
        foreach (PdfExtractedLine line in lines)
        {
            if (line.Runs.Count == 0 || line.Runs.Any(run => run.Letters.Count == 0)) continue;
            // A line of spaces is not a row of the paragraph, and it would stand in for the first baseline.
            if (string.IsNullOrWhiteSpace(line.Text)) continue;
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
        error is NotSupportedException or FormatException or InvalidOperationException or ArgumentException
            or ArithmeticException;

    // A damaged font length surfaces as an arithmetic error, and its text would mean nothing to a reader.
    private static string RefusalReason(Exception error) => error is ArithmeticException
        ? "The page uses a font or other data that is damaged." : error.Message;

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
            ReadStructure(document, tree, pageIndex));
    }

    private static PdfParagraphInspection InspectRegion(PdfDocument document, int pageIndex, PdfContentBounds region,
        ParagraphPage context)
    {
        (PdfPageTree tree, PdfPageContent page, _, _) = context;
        ValidateBounds(region, page.Width, page.Height);
        var selected = page.Letters.Where(letter => CenterInside(letter.BoundingBox, region)).ToArray();
        if (selected.Length == 0) throw new NotSupportedException("Select a complete paragraph of readable text.");
        if (selected.Length > 100_000) throw new NotSupportedException("The source paragraph exceeds the size limit.");
        // The replacement goes into the last glyph's sequence, so one element keeps the structure true.
        if (IsTagged(tree) && selected.Select(letter => context.Structure.ElementOf(letter.MarkedContentId)).Distinct().Skip(1).Any())
            throw new NotSupportedException("The selection spans several tagged elements.");
        if (selected.Any(letter => letter.MarkedContentId is int element && context.Structure.Described.Contains(element)))
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
        if (rows.Count == 0) throw new NotSupportedException("Select a complete paragraph of readable text.");
        foreach (ParagraphRow row in rows)
            for (int index = 1; index < row.Letters.Length; index++)
                if (row.Letters[index].StartBaseLine.X - row.Letters[index - 1].EndBaseLine.X > first.Size * 1.5)
                    throw new NotSupportedException("The source line has separated columns or uncertain spacing.");
        for (int index = 1; index < rows.Count; index++)
            if (!Continues(rows, index, rows[index], first.Size))
                throw new NotSupportedException("The selection is not one simple aligned paragraph.");
        if (LooksLikeTable(rows, first.Size))
            throw new NotSupportedException("This looks like a table, which paragraph editing does not support.");
        var bounds = new PdfContentBounds(selected.Min(letter => letter.BoundingBox.Left),
            selected.Min(letter => letter.BoundingBox.Bottom), selected.Max(letter => letter.BoundingBox.Right),
            selected.Max(letter => letter.BoundingBox.Top));
        (PdfRgbColor color, PdfContentBounds clip, bool mixedFills) = context.Paint.Inspect(region);
        if (!PdfContentRedaction.ClipHolds(clip, bounds)) throw new NotSupportedException("The paragraph is clipped.");
        if (page.Letters.Any(letter => CenterInside(letter.BoundingBox, bounds) && !selectedSet.Contains(letter)))
            throw new NotSupportedException("The source bounds overlap other text and cannot identify one paragraph safely.");
        double leading = rows.Count > 1 ? rows[0].Baseline - rows[1].Baseline : first.Size * 1.2;
        string[] rowTexts = [.. rows.Select(row => row.Text)];
        double indent = rows.Count > 1 && rows[0].Left - rows[1].Left > 1 ? rows[0].Left - rows[1].Left : 0;
        var guesses = new List<string>();
        string text = JoinRows(rows, rowTexts, ParagraphWords.Of(document), pageIndex, guesses);
        var candidate = new PdfParagraphCandidate(pageIndex, bounds, text, first.Font, first.Size, leading, color)
        {
            Clip = clip,
            MixedStyles = mixedFills || selected.Select(letter => (letter.FontName, Math.Round(letter.PointSize, 2))).Distinct().Skip(1).Any(),
            TextLeft = rows[0].Left - indent,
            FirstBaseline = rows[0].Baseline,
            TextRight = selected.Max(letter => letter.EndBaseLine.X),
            FirstLineIndent = indent,
            RightLimit = page.Letters.Where(letter => !string.IsNullOrWhiteSpace(letter.Value)
                && (letter.BoundingBox.Left + letter.BoundingBox.Right) / 2 > bounds.Right
                && (letter.BoundingBox.Bottom + letter.BoundingBox.Top) / 2 >= bounds.Bottom
                && (letter.BoundingBox.Bottom + letter.BoundingBox.Top) / 2 <= bounds.Top)
                .Select(letter => (double?)letter.BoundingBox.Left).Min(),
            RowTexts = Array.AsReadOnly(rowTexts),
            HyphenGuesses = Array.AsReadOnly(guesses.ToArray()),
            RowSpans = Array.AsReadOnly(rows.Select(row => new PdfParagraphRowSpan(row.Left, row.End, row.Baseline, row.Style.Size)).ToArray())
        };
        return new PdfParagraphInspection(true, string.Empty, candidate);
    }

    /// <summary>Returns the box that holds a paragraph's text in place in the replacement font.</summary>
    /// <param name="candidate">The source paragraph.</param>
    /// <param name="pageWidth">The page width in points.</param>
    /// <param name="pageHeight">The page height in points.</param>
    /// <param name="text">The replacement text, or null for the source text.</param>
    /// <param name="font">The replacement font, or null to measure the source geometry alone.</param>
    /// <param name="fontSize">The replacement size, or null for the source size.</param>
    /// <param name="leading">The distance between baselines, or null for the source's.</param>
    public static PdfContentBounds DefaultBox(PdfParagraphCandidate candidate, double pageWidth, double pageHeight,
        string? text = null, TrueTypeFont? font = null, double? fontSize = null, double? leading = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        double size = fontSize ?? candidate.FontSize;
        double left = Math.Max(0, candidate.TextLeft), right = candidate.TextRight;
        // The replacement paints under the source's clip, so more width than that would only vanish.
        double limit = Math.Min(pageWidth, candidate.Clip?.Right ?? pageWidth);
        // Text to the right stays where it is, so the box stops short of it and the lines wrap instead.
        if (candidate.RightLimit is double next) limit = Math.Min(limit, Math.Max(next - size * SpaceEm, right));
        int lines = Math.Max(1, candidate.RowTexts.Count);
        bool kept = false;
        if (font is not null)
        {
            // Measured at the source size, so a larger size wraps within the column instead of widening it.
            for (int index = 0; index < candidate.RowTexts.Count; index++)
                right = Math.Max(right, left + (index == 0 ? candidate.FirstLineIndent : 0)
                    + PdfTextReflow.MeasureLine(candidate.RowTexts[index], font, candidate.FontSize));
            right = Math.Min(right + 0.01, limit);
            string wrapped = Normalized(text ?? candidate.Text);
            int LinesWithin(double edge)
            {
                double indent = FittedIndent(wrapped, font, size, edge - left, candidate.FirstLineIndent);
                return (SourceRows(candidate, wrapped, font, size, edge - left, indent)
                    ?? PdfTextReflow.Wrap(wrapped, font, size, edge - left, indent)).Count;
            }
            lines = LinesWithin(right);
            // A rejoined hyphenated word can push the source's own text one row further. A little
            // more width costs less than a new row over whatever sits below.
            if (wrapped == candidate.Text && size == candidate.FontSize)
                for (int step = 1; step <= 8 && lines > candidate.RowTexts.Count; step++)
                {
                    double wider = Math.Min(right + step * size * 0.25, limit);
                    if (LinesWithin(wider) > candidate.RowTexts.Count) continue;
                    right = wider;
                    lines = candidate.RowTexts.Count;
                }
            kept = SourceRows(candidate, wrapped, font, size, right - left,
                FittedIndent(wrapped, font, size, right - left, candidate.FirstLineIndent)) is not null;
        }
        double top = Math.Min(pageHeight, candidate.FirstBaseline + size * LineAscent);
        return new PdfContentBounds(left, Math.Max(0, top - size - Drop(candidate, kept, lines - 1, leading ?? candidate.Leading)),
            Math.Min(right, limit), top);
    }

    // How far a line sits below the first. Rows kept from the source stay on their own baselines,
    // which need not be evenly spaced.
    private static double Drop(PdfParagraphCandidate source, bool kept, int index, double leading) =>
        kept && leading == source.Leading ? source.RowSpans[0].Baseline - source.RowSpans[index].Baseline : index * leading;

    private static string Normalized(string text) =>
        ExpandLigatures(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'));

    // The first line is shorter by the indent. A box too narrow for the indent has no first line to
    // shorten, and a first word that fits the box but not that line would be split, so the indent gives way.
    private static double FittedIndent(string text, TrueTypeFont font, double size, double width, double indent)
    {
        if (indent >= width) return 0;
        double word = PdfTextReflow.MeasureLine(text.TrimStart().Split(' ', '\n')[0], font, size);
        return word > width - indent && word <= width ? 0 : indent;
    }

    // Text nobody changed goes back in the rows the page has it in, line-end hyphens included, so it
    // does not move however the replacement font's widths would wrap it. Null means a row will not fit.
    private static IReadOnlyList<PdfReflowLine>? SourceRows(PdfParagraphCandidate source, string text, TrueTypeFont font,
        double size, double width, double indent)
    {
        if (text != source.Text || size != source.FontSize) return null;
        PdfReflowLine[] rows = [.. source.RowTexts.Select(row => new PdfReflowLine(row, PdfTextReflow.MeasureLine(row, font, size)))];
        bool fits = rows.Select((row, index) => row.Width + (index == 0 ? indent : 0)).All(row => row <= width + 0.0001);
        return fits && rows.All(row => row.Text.EnumerateRunes().All(rune => rune.Value == ' ' || font.GetGlyphId(rune.Value) != 0))
            ? rows : null;
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
        string normalized = Normalized(text);
        foreach (Rune rune in normalized.EnumerateRunes())
        {
            if (rune.Value == '\n') continue;
            // This slice has no shaping engine. Combining marks, bidi text, and contextual scripts need one.
            bool ordinaryPunctuation = rune.Value is 0x2018 or 0x2019 or 0x201C or 0x201D
                or 0x2022 or 0x2026 or 0x20AC or 0x2122 or >= 0x2010 and <= 0x2015 or 0x2212;
            if ((rune.Value > 0x024F && !ordinaryPunctuation) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
                or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                throw new NotSupportedException("This text needs shaping that paragraph editing does not yet support.");
            if (rune.Value != ' ' && font.GetGlyphId(rune.Value) == 0)
                throw new NotSupportedException($"{font.PostScriptName} has no glyph for {rune}.");
        }
        // Reflow and authoring must use the same space glyph. Missing spaces must not silently become .notdef.
        if (normalized.Contains(' ') && font.GetGlyphId(' ') == 0)
            throw new NotSupportedException("The replacement font has no space glyph.");
        double indent = alignment == PdfParagraphAlignment.Left
            ? FittedIndent(normalized, font, fontSize, targetBounds.Width, source.FirstLineIndent) : 0;
        IReadOnlyList<PdfReflowLine>? kept = SourceRows(source, normalized, font, fontSize, targetBounds.Width, indent);
        IReadOnlyList<PdfReflowLine> wrapped = normalized.Length == 0 ? []
            : kept ?? PdfTextReflow.Wrap(normalized, font, fontSize, targetBounds.Width, indent);
        var lines = wrapped.Select((line, index) => new PdfParagraphLine(line.Text,
            targetBounds.Left + (alignment == PdfParagraphAlignment.Center ? (targetBounds.Width - line.Width) / 2
                : alignment == PdfParagraphAlignment.Right ? targetBounds.Width - line.Width : index == 0 ? indent : 0),
            targetBounds.Top - fontSize * LineAscent - Drop(source, kept is not null, index, leading), line.Width)).ToArray();
        string overflow = lines.Any(line => line.X + line.Width > targetBounds.Right + 0.0001
            || line.Baseline - fontSize * LineDescent < targetBounds.Bottom - 0.0001)
            ? "The text overflows the paragraph box. Resize the box or adjust the font size or line spacing." : string.Empty;
        PdfContentBounds? ink = null;
        if (overflow.Length == 0 && lines.Any(line => line.Text.Length != 0))
        {
            // The box lays out advances and baselines, and glyphs may overhang it. Only ink that
            // leaves the page or the source's clip is lost, since the replacement paints under that clip.
            var content = new PdfContentStreamBuilder().BeginText().SetFont(font, fontSize);
            foreach (PdfParagraphLine line in lines)
                if (line.Text.Length != 0)
                    content.SetTextMatrix(1, 0, 0, 1, line.X, line.Baseline).ShowUnicodeText(line.Text);
            content.EndText();
            PdfDocument layout = PdfDocument.Open(new PdfDocumentBuilder().AddPage(page.Width, page.Height, content).Build());
            PdfContentBounds laid = PdfContentBounds.Union(new PdfPageContentReader(layout).Read(0).Letters.Select(letter => letter.BoundingBox));
            ink = laid;
            if (laid.Left < 0 || laid.Bottom < 0 || laid.Right > page.Width || laid.Top > page.Height)
                overflow = "The glyphs extend outside the page. Move the box or adjust the text.";
            else if (source.Clip is { } clip && !PdfContentRedaction.ClipHolds(clip, laid))
                throw new NotSupportedException("The new text extends past the clipped area of the source text. Move the box or adjust the text.");
        }
        byte[]? preview = null;
        if (overflow.Length == 0)
        {
            // The new text goes in at the paragraph's last glyph. The cover check judges what is painted
            // after that, and text from before it would be printed over, which extraction would never show.
            PdfExtractedLetter[] under = [.. page.Letters.Reverse().SkipWhile(letter => !CenterInside(letter.BoundingBox, source.SourceRegion))
                .Where(letter => !string.IsNullOrWhiteSpace(letter.Value) && !CenterInside(letter.BoundingBox, source.SourceRegion))];
            if (lines.Any(line => line.Text.Length != 0 && under.Any(letter => CenterInside(letter.BoundingBox, new PdfContentBounds(
                line.X, line.Baseline - fontSize * LineDescent, line.X + line.Width, line.Baseline + fontSize * 0.7)))))
                throw new NotSupportedException("The new text would land on other text on the page. Move the box or adjust the text.");
            var edit = new PdfTextLineEdit(pageIndex, source.SourceRegion, string.Join("\n", lines.Select(line => line.Text)))
            {
                Substitute = font, UseSubstitute = true, FontSize = fontSize,
                ParagraphLines = Array.AsReadOnly(lines),
                ParagraphBox = ink,
                ParagraphRows = source.RowSpans
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

    private static (PdfRgbColor Color, PdfContentBounds Clip, bool MixedFills) ParagraphPaint(PdfDocument document, LineProbe probe)
    {
        if (probe.Inspection.CanEdit && probe.Insertion.ClipUnsafe)
            throw new NotSupportedException("The paragraph is clipped by a shape that is not a single rectangle.");
        if (!probe.Inspection.CanEdit || probe.Insertion.ParagraphUnsafe)
            throw new NotSupportedException("The paragraph has unsupported transformations or rendering.");
        TextState state = probe.Insertion.State!;
        if (probe.Insertion.HasType3 || state.FontObject is null || Resolve(document, state.FontObject) is not PdfDictionary)
            throw new NotSupportedException("The paragraph uses a font whose painted appearance cannot be preserved.");
        // The replacement replays the source's own fill operators, so this value only describes it.
        PdfContentInstruction[] fill = probe.Insertion.DominantFill!;
        PdfRgbColor color = new(0, 0, 0);
        if (fill.Length == 1 && fill[0].Operator == "rg" && fill[0].Operands.Count == 3)
            color = new PdfRgbColor(Number(document, fill[0].Operands[0]), Number(document, fill[0].Operands[1]),
                Number(document, fill[0].Operands[2]));
        else if (fill.Length == 1 && fill[0].Operator == "g" && fill[0].Operands.Count == 1)
        {
            double gray = Number(document, fill[0].Operands[0]);
            color = new PdfRgbColor(gray, gray, gray);
        }
        return (color, state.Clip, probe.Insertion.MixedFills);
    }
}
