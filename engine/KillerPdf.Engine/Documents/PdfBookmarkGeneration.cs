using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace KillerPdf.Engine.Documents;

/// <summary>Detects heading-like page text and authors reviewed bookmark proposals.</summary>
public static class PdfBookmarkGeneration
{
    // "2.3.1 Scope", "4. Results", "Chapter 7", "Part IV", "Appendix B".
    private static readonly Regex Numbering = new(
        @"^(?:(?i:chapter|section|part|appendix)\s+(?:[0-9]{1,3}|[IVXLC]{1,6}|[A-Z])\b|([0-9]{1,3}(?:\.[0-9]{1,3})*)\.?\s+\S)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex PageNumber = new(
        @"^(?:page\s+)?(?:[0-9]+|[ivxlcdm]+)(?:\s+of\s+[0-9]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Digits = new("[0-9]+", RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));

    /// <summary>Creates reviewable bookmark proposals from the document's headings.</summary>
    /// <remarks>
    /// A tagged document's heading elements (H1, H2 and onward, through its role map) are used
    /// when they exist. Otherwise headings are found from how lines look: larger text, bold text
    /// at body size and numbered titles, with lines that repeat across pages (running headers,
    /// footers and page numbers) left out. The hierarchy follows heading numbers where a title has
    /// one, and text size and weight otherwise.
    /// </remarks>
    public static IReadOnlyList<PdfBookmarkProposal> DetectHeadings(
        PdfDocument document, PdfBookmarkDetectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new PdfBookmarkDetectionOptions();
        Validate(options);
        Regex? titlePattern = options.TitlePattern is null ? null : new Regex(
            options.TitlePattern, RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        var reader = new PdfPageContentReader(document);
        if (options.PageRegions is not null
            && options.PageRegions.Keys.Any(page => page < 0 || page >= reader.PageCount))
            throw new ArgumentOutOfRangeException(nameof(options),
                "A heading region page is outside the document.");
        var pages = new PdfPageContent[reader.PageCount];
        for (int pageIndex = 0; pageIndex < reader.PageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pages[pageIndex] = reader.Read(pageIndex, cancellationToken);
        }

        bool Accepts(int pageIndex, string title, PdfContentBounds bounds) =>
            title.Length > 0 && title.Length <= options.MaximumTitleLength
            && (titlePattern is null || titlePattern.IsMatch(title))
            && (options.PageRegions is null
                || options.PageRegions.TryGetValue(pageIndex, out PdfContentBounds region)
                    && Intersects(region, bounds));

        if (options.UseDocumentTags
            && TaggedHeadings(document, pages, options, Accepts, cancellationToken) is { Count: > 0 } tagged)
            return tagged;
        return VisualHeadings(pages, options, Accepts, cancellationToken);
    }

    private static List<PdfBookmarkProposal>? TaggedHeadings(PdfDocument document,
        PdfPageContent[] pages, PdfBookmarkDetectionOptions options,
        Func<int, string, PdfContentBounds, bool> accepts, CancellationToken cancellationToken)
    {
        PdfPageTree tree = PdfPageTree.Read(document);
        if (!tree.Catalog.TryGetValue(new PdfName("StructTreeRoot"u8), out PdfObject? rootValue)
            || Resolve(document, rootValue) is not PdfDictionary root)
            return null;
        Dictionary<string, string> roleMap = [];
        if (root.TryGetValue(new PdfName("RoleMap"u8), out PdfObject? mapValue)
            && Resolve(document, mapValue) is PdfDictionary map)
            foreach ((PdfName key, PdfObject value) in map)
                if (Resolve(document, value) is PdfName target)
                    roleMap[key.ValueAsLatin1()] = target.ValueAsLatin1();
        IReadOnlyList<Diagnostics.PdfAccessibilityReadingOrderItem> items;
        try { items = Diagnostics.PdfAccessibilityReadingOrder.Read(document).Items; }
        catch (Exception error) when (error is InvalidOperationException or FormatException
            or NotSupportedException or ArgumentException)
        {
            return null;
        }

        var proposals = new List<PdfBookmarkProposal>();
        // Consecutive references of one element make one heading, which may span pages.
        int index = 0;
        while (index < items.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Diagnostics.PdfAccessibilityReadingOrderItem first = items[index];
            int end = index + 1;
            while (end < items.Count && first.StructureObjectNumber is not null
                && items[end].StructureObjectNumber == first.StructureObjectNumber)
                end++;
            int? level = HeadingLevel(StandardRole(first.Role, roleMap));
            if (level is int headingLevel && headingLevel < options.MaximumDepth
                && first.PageIndex >= 0 && first.PageIndex < pages.Length)
            {
                var ids = new HashSet<int>();
                for (int item = index; item < end; item++)
                    if (items[item].PageIndex == first.PageIndex
                        && items[item].MarkedContentId is int id)
                        ids.Add(id);
                PdfExtractedWord[] words = [.. pages[first.PageIndex].Words.Where(word =>
                    word.Letters.All(letter => letter.MarkedContentId is int id && ids.Contains(id)))];
                string title = Clean(first.ActualText ?? string.Join(" ", words.Select(word => word.Text)));
                if (words.Length > 0)
                {
                    PdfContentBounds bounds = PdfContentBounds.Union(words.Select(word => word.BoundingBox));
                    if (accepts(first.PageIndex, title, bounds))
                        proposals.Add(new PdfBookmarkProposal
                        {
                            Title = title,
                            PageIndex = first.PageIndex,
                            Bounds = bounds,
                            PointSize = words.SelectMany(word => word.Letters).Max(letter => letter.PointSize),
                            Level = headingLevel,
                            Source = PdfBookmarkProposalSource.DocumentTags
                        });
                }
            }
            index = end;
        }
        return proposals;
    }

    private static string StandardRole(string role, Dictionary<string, string> roleMap)
    {
        for (int hop = 0; hop < 16 && roleMap.TryGetValue(role, out string? mapped)
            && !string.Equals(mapped, role, StringComparison.Ordinal); hop++)
            role = mapped;
        return role;
    }

    // H1 is the top level. PDF 2.0 allows any depth (H7, H10 and so on); a bare H counts as top level.
    private static int? HeadingLevel(string role)
    {
        if (role == "H") return 0;
        if (role.Length < 2 || role[0] != 'H') return null;
        return int.TryParse(role.AsSpan(1), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int level) && level >= 1
            ? level - 1 : null;
    }

    private sealed record VisualLine(int Page, PdfContentBounds Bounds, string Title,
        double Size, bool Bold, int? NumberDepth);

    private static ReadOnlyCollection<PdfBookmarkProposal> VisualHeadings(PdfPageContent[] pages,
        PdfBookmarkDetectionOptions options, Func<int, string, PdfContentBounds, bool> accepts,
        CancellationToken cancellationToken)
    {
        // Body size: the size that carries the most regular-weight text, or all text if none is regular.
        var regularWeight = new Dictionary<double, long>();
        var anyWeight = new Dictionary<double, long>();
        // Running headers and footers: the same text, with numbers ignored, at about the same height on many pages.
        var repeats = new Dictionary<string, (HashSet<int> Pages, double Low, double High)>(StringComparer.Ordinal);
        var lines = new List<(int Page, PdfExtractedLine Line, string Title, double Size, bool Bold)>();
        for (int pageIndex = 0; pageIndex < pages.Length; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (PdfExtractedLine line in pages[pageIndex].Lines)
            {
                if (line.WritingDirection is not (PdfWritingDirection.LeftToRight
                    or PdfWritingDirection.RightToLeft))
                    continue;
                string title = Clean(line.Text);
                if (title.Length == 0) continue;
                double size = Math.Round(line.Runs.Max(run => run.PointSize) * 2) / 2;
                bool bold = line.Runs.Where(run => !string.IsNullOrWhiteSpace(run.Text))
                    .All(run => IsBold(run.FontName));
                foreach (PdfExtractedTextRun run in line.Runs)
                {
                    double runSize = Math.Round(run.PointSize * 2) / 2;
                    long count = run.Text.Count(character => !char.IsWhiteSpace(character));
                    anyWeight[runSize] = anyWeight.GetValueOrDefault(runSize) + count;
                    if (!IsBold(run.FontName))
                        regularWeight[runSize] = regularWeight.GetValueOrDefault(runSize) + count;
                }
                string key = Digits.Replace(title, "#");
                double top = line.BoundingBox.Top;
                repeats[key] = repeats.TryGetValue(key, out var seen)
                    ? (seen.Pages, Math.Min(seen.Low, top), Math.Max(seen.High, top))
                    : ([], top, top);
                repeats[key].Pages.Add(pageIndex);
                lines.Add((pageIndex, line, title, size, bold));
            }
        }
        Dictionary<double, long> weights = regularWeight.Count > 0 ? regularWeight : anyWeight;
        double body = weights.Count == 0 ? 0 : weights.MaxBy(pair => pair.Value).Key;

        bool Running(string title)
        {
            if (pages.Length < 3 || !options.ExcludeRunningText) return false;
            (HashSet<int> seenPages, double low, double high) = repeats[Digits.Replace(title, "#")];
            return seenPages.Count >= 3 && seenPages.Count * 2 >= pages.Length
                && high - low <= 12;
        }

        var candidates = new List<VisualLine>();
        foreach ((int page, PdfExtractedLine line, string title, double size, bool bold) in lines)
        {
            if (options.ExcludeRunningText && PageNumber.IsMatch(title) || Running(title)) continue;
            Match numbering = Numbering.Match(title);
            int? depth = numbering.Success
                ? numbering.Groups[1].Success ? numbering.Groups[1].Value.Split('.').Length : 1
                : null;
            bool large = size >= options.MinimumPointSize;
            // Bold lines at body size are headings when they read like titles, not like sentences.
            bool styled = options.DetectStyledHeadings && bold && body > 0 && size >= body - 0.25
                && title.Length <= Math.Min(options.MaximumTitleLength, 100)
                && title.Any(char.IsLetter) && !".,;".Contains(title[^1]);
            if (!large && !styled) continue;
            candidates.Add(new VisualLine(page, line.BoundingBox, title, size, bold, depth));
        }

        // A long title wraps onto lines of the same style directly below it.
        var merged = new List<VisualLine>();
        foreach (VisualLine line in candidates)
        {
            if (merged.Count > 0 && merged[^1] is var previous && previous.Page == line.Page
                && previous.Size == line.Size && previous.Bold == line.Bold && line.NumberDepth is null
                && previous.Bounds.Bottom - line.Bounds.Top <= line.Size * 0.8
                && previous.Bounds.Bottom - line.Bounds.Top >= -line.Size * 0.2
                && line.Bounds.Left < previous.Bounds.Right && line.Bounds.Right > previous.Bounds.Left)
            {
                merged[^1] = previous with
                {
                    Title = previous.Title + " " + line.Title,
                    Bounds = PdfContentBounds.Union([previous.Bounds, line.Bounds])
                };
                continue;
            }
            merged.Add(line);
        }

        // Larger styles rank higher, and bold above regular at the same size.
        (double Size, bool Bold)[] styles = [.. merged.Select(line => (line.Size, line.Bold)).Distinct()
            .OrderByDescending(style => style.Size).ThenByDescending(style => style.Bold)
            .Take(options.MaximumDepth)];
        var proposals = new List<PdfBookmarkProposal>(merged.Count);
        int previousLevel = 0;
        foreach (VisualLine line in merged)
        {
            int ranked = Array.IndexOf(styles, (line.Size, line.Bold));
            int level = line.NumberDepth is int depth && options.DetectStyledHeadings ? depth - 1 : ranked;
            if (ranked < 0 || level >= options.MaximumDepth
                || !accepts(line.Page, line.Title, line.Bounds)) continue;
            level = proposals.Count == 0 ? 0 : Math.Min(level, previousLevel + 1);
            proposals.Add(new PdfBookmarkProposal
            {
                Title = line.Title,
                PageIndex = line.Page,
                Bounds = line.Bounds,
                PointSize = line.Size,
                Level = level,
                Source = PdfBookmarkProposalSource.TextAppearance
            });
            previousLevel = level;
        }
        return Array.AsReadOnly(proposals.ToArray());
    }

    private static bool IsBold(string fontName)
    {
        string name = fontName.Contains('+') ? fontName[(fontName.IndexOf('+') + 1)..] : fontName;
        return name.Contains("Bold", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Black", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Heavy", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Semibold", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Demi", StringComparison.OrdinalIgnoreCase);
    }

    private static string Clean(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32) throw new FormatException("Reference nesting limit exceeded.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static bool Intersects(PdfContentBounds first, PdfContentBounds second) =>
        first.Left < second.Right && first.Right > second.Left
        && first.Bottom < second.Top && first.Top > second.Bottom;

    /// <summary>Authors accepted proposals after every proposal has been reviewed.</summary>
    public static byte[] Apply(PdfDocument document,
        IEnumerable<PdfBookmarkProposal> proposals)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(proposals);
        PdfBookmarkProposal[] reviewed = [.. proposals];
        if (reviewed.Any(item => item.Decision == PdfBookmarkProposalDecision.Pending))
            throw new InvalidOperationException(
                "Every bookmark proposal must be accepted or rejected before authoring.");
        PdfBookmarkProposal[] accepted = [.. reviewed.Where(item =>
            item.Decision == PdfBookmarkProposalDecision.Accepted)];
        if (accepted.Length == 0)
            throw new InvalidOperationException("No bookmark proposals were accepted.");
        var editor = new PdfIncrementalPageEditor(document);
        int previousLevel = 0;
        for (int index = 0; index < accepted.Length; index++)
        {
            PdfBookmarkProposal item = accepted[index];
            if (string.IsNullOrWhiteSpace(item.Title))
                throw new ArgumentException("An accepted bookmark title cannot be empty.",
                    nameof(proposals));
            int level = index == 0 ? 0 : Math.Min(item.Level, previousLevel + 1);
            editor.AddBookmark(item.Title, item.PageIndex, level,
                new PdfBookmarkOptions
                {
                    Destination = PdfDestination.FitWidth(item.Bounds.Top)
                });
            previousLevel = level;
        }
        return editor.Build();
    }

    private static void Validate(PdfBookmarkDetectionOptions options)
    {
        if (!double.IsFinite(options.MinimumPointSize)
            || options.MinimumPointSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaximumTitleLength < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaximumDepth is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.PageRegions?.Values.Any(region => region.Width <= 0 || region.Height <= 0) == true)
            throw new ArgumentException(
                "Heading regions must have positive dimensions.", nameof(options));
        if (options.TitlePattern is not null)
            try
            {
                _ = new Regex(options.TitlePattern, RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException error)
            {
                throw new ArgumentException(
                    "The heading title pattern is invalid.", nameof(options), error);
            }
    }
}

/// <summary>Controls heading detection for bookmark proposals.</summary>
public sealed record PdfBookmarkDetectionOptions
{
    /// <summary>Gets the smallest effective text size considered a heading.</summary>
    public double MinimumPointSize { get; init; } = 14;
    /// <summary>Gets the longest heading text accepted.</summary>
    public int MaximumTitleLength { get; init; } = 160;
    /// <summary>Gets the maximum number of font-size hierarchy levels.</summary>
    public int MaximumDepth { get; init; } = 6;
    /// <summary>Gets an optional regular expression that heading titles must match.</summary>
    public string? TitlePattern { get; init; }
    /// <summary>Gets optional page regions that bound heading detection.</summary>
    public IReadOnlyDictionary<int, PdfContentBounds>? PageRegions { get; init; }
    /// <summary>Gets whether a tagged document's heading elements are used when it has them.</summary>
    public bool UseDocumentTags { get; init; } = true;
    /// <summary>Gets whether bold body-size lines count as headings and numbers set the hierarchy.</summary>
    public bool DetectStyledHeadings { get; init; } = true;
    /// <summary>Gets whether text repeated across pages, such as running headers and page numbers, is left out.</summary>
    public bool ExcludeRunningText { get; init; } = true;
}

/// <summary>A reviewable heading-derived bookmark.</summary>
public sealed record PdfBookmarkProposal
{
    /// <summary>Gets the proposed bookmark title.</summary>
    public required string Title { get; init; }
    /// <summary>Gets the zero-based target page.</summary>
    public int PageIndex { get; init; }
    /// <summary>Gets the detected heading bounds.</summary>
    public PdfContentBounds Bounds { get; init; }
    /// <summary>Gets the detected effective text size.</summary>
    public double PointSize { get; init; }
    /// <summary>Gets the proposed zero-based hierarchy level.</summary>
    public int Level { get; init; }
    /// <summary>Gets the explicit review decision.</summary>
    public PdfBookmarkProposalDecision Decision { get; init; }
    /// <summary>Gets where the heading was found.</summary>
    public PdfBookmarkProposalSource Source { get; init; }
}

/// <summary>Where a bookmark proposal's heading was found.</summary>
public enum PdfBookmarkProposalSource
{
    /// <summary>The heading was recognized from how its text looks.</summary>
    TextAppearance,
    /// <summary>The heading is a heading element in the document's tag structure.</summary>
    DocumentTags
}

/// <summary>The review state of a generated bookmark proposal.</summary>
public enum PdfBookmarkProposalDecision
{
    /// <summary>The proposal has not been reviewed.</summary>
    Pending,
    /// <summary>The proposal should be authored.</summary>
    Accepted,
    /// <summary>The proposal should not be authored.</summary>
    Rejected
}
