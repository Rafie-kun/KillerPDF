using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KillerPdf.Engine.Documents;

/// <summary>The supported search expression for a redaction preview.</summary>
public enum PdfRedactionSearchKind
{
    /// <summary>Literal text or phrase.</summary>
    ExactText,
    /// <summary>A caller-supplied regular expression.</summary>
    RegularExpression,
    /// <summary>A common email-address pattern.</summary>
    EmailAddress,
    /// <summary>A common North American phone-number pattern.</summary>
    PhoneNumber,
    /// <summary>A U.S. Social Security number written with standard separators.</summary>
    SocialSecurityNumber,
    /// <summary>A payment card account number with a valid checksum.</summary>
    PaymentCardNumber,
    /// <summary>An international bank account number with a valid checksum.</summary>
    InternationalBankAccountNumber
}

/// <summary>Options for locating reviewable redaction candidates.</summary>
public sealed record PdfRedactionSearchOptions
{
    /// <summary>Gets the search kind.</summary>
    public PdfRedactionSearchKind Kind { get; init; }
    /// <summary>Gets literal text or a regular expression. Common-pattern searches ignore this value.</summary>
    public string Query { get; init; } = string.Empty;
    /// <summary>Gets whether matching distinguishes letter case.</summary>
    public bool MatchCase { get; init; }
    /// <summary>Gets the bounded regular-expression evaluation time.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Gets the optional reason code carried by every proposed match.</summary>
    public string? ReasonCode { get; init; }
    /// <summary>Gets the optional replacement text carried by every proposed match.</summary>
    public string? OverlayText { get; init; }
}

/// <summary>The kind of page content selected for later permanent removal.</summary>
public enum PdfRedactionTargetKind
{
    /// <summary>Text found by a search expression.</summary>
    Text,
    /// <summary>A caller-selected rectangular page region.</summary>
    PageRegion,
    /// <summary>An extracted image placement.</summary>
    Image,
    /// <summary>A review annotation containing comment text.</summary>
    Comment,
    /// <summary>A document-level embedded file.</summary>
    Attachment,
    /// <summary>A form field widget whose value contains the searched text.</summary>
    FormField
}

/// <summary>A rectangular page region proposed for redaction.</summary>
public sealed record PdfRedactionRegion(int PageIndex, PdfContentBounds Bounds,
    string? ReasonCode = null, string? OverlayText = null)
{
    /// <summary>Gets the color that fills the region once it is redacted, black when not set.</summary>
    public KillerPdf.Engine.Authoring.PdfRgbColor? FillColor { get; init; }
}

/// <summary>One text match proposed for redaction.</summary>
public sealed record PdfRedactionMatch(string Id, int PageIndex, string Text,
    PdfContentBounds Bounds, int FirstWordIndex, int WordCount,
    string? ReasonCode = null, string? OverlayText = null,
    PdfRedactionTargetKind TargetKind = PdfRedactionTargetKind.Text)
{
    /// <summary>
    /// Gets the matched characters as one area per text line, so a match that wraps or covers
    /// only part of a word marks exactly that text. Empty for matches not built from text.
    /// </summary>
    public IReadOnlyList<PdfContentBounds> Areas { get; init; } = [];
}

/// <summary>One named document prepared for isolated redaction search.</summary>
public sealed record PdfRedactionSearchBatchInput(
    string Name, IReadOnlyList<PdfPageContent> Pages, PdfRedactionSearchOptions Options);

/// <summary>The review or isolated failure produced for one batch document.</summary>
public sealed record PdfRedactionSearchBatchResult(
    int Index, string Name, PdfRedactionReview? Review, string? Error)
{
    /// <summary>Gets whether the document produced a review successfully.</summary>
    public bool Succeeded => Review is not null && Error is null;
}

/// <summary>An immutable preview whose matches must be explicitly excluded when retained.</summary>
public sealed partial class PdfRedactionReview
{
    private static readonly PdfRedactionReviewJsonContext CompactJson = new(JsonOptions(false));
    private static readonly PdfRedactionReviewJsonContext IndentedJson = new(JsonOptions(true));
    private readonly PdfRedactionMatch[] _matches;
    private readonly HashSet<string> _excluded;

    internal PdfRedactionReview(IEnumerable<PdfRedactionMatch> matches, IEnumerable<string>? excluded = null)
    {
        _matches = [.. matches];
        _excluded = new HashSet<string>(excluded ?? [], StringComparer.Ordinal);
        Matches = Array.AsReadOnly(_matches);
        Included = Array.AsReadOnly(_matches.Where(match => !_excluded.Contains(match.Id)).ToArray());
    }

    /// <summary>Gets every proposed match.</summary>
    public IReadOnlyList<PdfRedactionMatch> Matches { get; }
    /// <summary>Gets matches still selected for later permanent removal.</summary>
    public IReadOnlyList<PdfRedactionMatch> Included { get; }
    /// <summary>
    /// Gets the zero-based pages with an annotation that <see cref="PdfRedactionSearch.FindInAnnotations"/>
    /// could not read and therefore could not search. Empty for reviews built another way.
    /// </summary>
    public IReadOnlyList<int> UnreadableAnnotationPages { get; init; } = [];

    /// <summary>Returns a new review with one match excluded.</summary>
    public PdfRedactionReview Exclude(string id)
    {
        if (!_matches.Any(match => string.Equals(match.Id, id, StringComparison.Ordinal)))
            throw new KeyNotFoundException($"Redaction match '{id}' was not found.");
        return new PdfRedactionReview(_matches, _excluded.Append(id)) { UnreadableAnnotationPages = UnreadableAnnotationPages };
    }

    /// <summary>Returns a new review with an excluded match restored.</summary>
    public PdfRedactionReview Include(string id)
    {
        if (!_matches.Any(match => string.Equals(match.Id, id, StringComparison.Ordinal)))
            throw new KeyNotFoundException($"Redaction match '{id}' was not found.");
        return new PdfRedactionReview(_matches, _excluded.Where(value => !string.Equals(value, id, StringComparison.Ordinal)))
        {
            UnreadableAnnotationPages = UnreadableAnnotationPages
        };
    }

    /// <summary>Returns a new review with every match excluded.</summary>
    public PdfRedactionReview ExcludeAll() =>
        new(_matches, _matches.Select(match => match.Id)) { UnreadableAnnotationPages = UnreadableAnnotationPages };

    /// <summary>Returns a new review with every match included.</summary>
    public PdfRedactionReview IncludeAll() => new(_matches) { UnreadableAnnotationPages = UnreadableAnnotationPages };

    /// <summary>Builds a review from caller-selected rectangular page regions.</summary>
    public static PdfRedactionReview FromRegions(IEnumerable<PdfRedactionRegion> regions)
    {
        ArgumentNullException.ThrowIfNull(regions);
        PdfRedactionRegion[] values = [.. regions.Select(region => region
            ?? throw new ArgumentException("A redaction region cannot be null.", nameof(regions)))];
        var matches = new PdfRedactionMatch[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            PdfRedactionRegion region = values[index];
            if (region.PageIndex < 0)
                throw new ArgumentOutOfRangeException(nameof(regions), "A redaction page index cannot be negative.");
            PdfContentBounds bounds = region.Bounds;
            if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Bottom)
                || !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Top)
                || bounds.Right <= bounds.Left || bounds.Top <= bounds.Bottom)
                throw new ArgumentException("A redaction region must have finite positive dimensions.", nameof(regions));
            PdfRedactionSearch.ValidatePresentation(
                region.ReasonCode, region.OverlayText, nameof(regions));
            matches[index] = new PdfRedactionMatch($"region:{index}", region.PageIndex, string.Empty,
                bounds, -1, 0, region.ReasonCode, region.OverlayText,
                PdfRedactionTargetKind.PageRegion);
        }
        return new PdfRedactionReview(matches);
    }

    /// <summary>Builds a review from every extracted image placement on selected pages.</summary>
    public static PdfRedactionReview FromImages(
        IEnumerable<PdfPageContent> pages,
        string? reasonCode = null, string? overlayText = null)
    {
        ArgumentNullException.ThrowIfNull(pages);
        PdfRedactionSearch.ValidatePresentation(reasonCode, overlayText, nameof(pages));
        var matches = new List<PdfRedactionMatch>();
        foreach ((PdfPageContent page, int pageIndex) in pages.Select(
                     (page, index) => (page ?? throw new ArgumentException(
                         "A redaction page cannot be null.", nameof(pages)), index)))
        {
            foreach ((PdfExtractedImage image, int imageIndex) in page.Images.Select(
                         (image, index) => (image, index)))
                matches.Add(new PdfRedactionMatch(
                    $"image:{pageIndex}:{imageIndex}", pageIndex, string.Empty,
                    image.BoundingBox, -1, 0, reasonCode, overlayText,
                    PdfRedactionTargetKind.Image));
        }
        return new PdfRedactionReview(matches);
    }

    /// <summary>Builds a review from every bounded comment annotation in a document.</summary>
    public static PdfRedactionReview FromComments(
        PdfDocument document, string? reasonCode = null, string? overlayText = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfRedactionSearch.ValidatePresentation(reasonCode, overlayText, nameof(document));
        var matches = new List<PdfRedactionMatch>();
        foreach (PdfCommentInfo comment in PdfCommentReader.Read(document))
        {
            PdfContentBounds bounds = comment.Bounds
                ?? throw new NotSupportedException(
                    $"Comment annotation {comment.AnnotationIndex + 1} on page {comment.PageIndex + 1} has no rectangular bounds.");
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new NotSupportedException(
                    $"Comment annotation {comment.AnnotationIndex + 1} on page {comment.PageIndex + 1} has empty bounds.");
            matches.Add(new PdfRedactionMatch(
                $"comment:{comment.PageIndex}:{comment.AnnotationIndex}",
                comment.PageIndex, comment.Contents, bounds, -1, 0,
                reasonCode, overlayText, PdfRedactionTargetKind.Comment));
        }
        return new PdfRedactionReview(matches);
    }

    /// <summary>Builds a review from every document-level embedded file.</summary>
    public static PdfRedactionReview FromAttachments(
        PdfDocument document, string? reasonCode = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfRedactionSearch.ValidatePresentation(reasonCode, null, nameof(document));
        PdfRedactionMatch[] matches = [.. PdfAttachmentReader.Read(document)
            .Select((attachment, index) => new PdfRedactionMatch(
                $"attachment:{index}", -1, attachment.FileName,
                new PdfContentBounds(0, 0, 0, 0), -1, 0,
                reasonCode, null, PdfRedactionTargetKind.Attachment))];
        return new PdfRedactionReview(matches);
    }

    /// <summary>Exports a stable review report without matched text unless explicitly requested.</summary>
    public string ToJson(bool includeMatchedText = false, bool indented = false)
    {
        return JsonSerializer.Serialize(new ReportFile(
            1,
            _matches.Length,
            Included.Count,
            [.. _matches.Select(match => new ReportMatch(
                match.Id,
                match.PageIndex,
                includeMatchedText ? match.Text : null,
                match.Bounds,
                match.FirstWordIndex,
                match.WordCount,
                match.ReasonCode,
                match.OverlayText,
                match.TargetKind,
                !_excluded.Contains(match.Id)))]),
            indented ? IndentedJson.ReportFile : CompactJson.ReportFile);
    }

    private sealed record ReportFile(
        int Version, int MatchCount, int IncludedCount, ReportMatch[] Matches);

    private sealed record ReportMatch(
        string Id,
        int PageIndex,
        string? Text,
        PdfContentBounds Bounds,
        int FirstWordIndex,
        int WordCount,
        string? ReasonCode,
        string? OverlayText,
        PdfRedactionTargetKind TargetKind,
        bool Included);

    private static JsonSerializerOptions JsonOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented
    };

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ReportFile))]
    private sealed partial class PdfRedactionReviewJsonContext : JsonSerializerContext;

    /// <summary>Exports a readable review without matched text unless explicitly requested.</summary>
    public string ToText(bool includeMatchedText = false)
    {
        var output = new System.Text.StringBuilder()
            .Append("Redaction candidates: ").AppendLine(_matches.Length.ToString())
            .Append("Selected: ").AppendLine(Included.Count.ToString());
        foreach (PdfRedactionMatch match in _matches)
        {
            output.Append(_excluded.Contains(match.Id) ? "[Excluded] " : "[Selected] ")
                .Append(match.Id).Append(" | ").Append(match.TargetKind);
            if (match.PageIndex >= 0)
                output.Append(" | Page ").Append(match.PageIndex + 1);
            if (!string.IsNullOrWhiteSpace(match.ReasonCode))
                output.Append(" | Reason ").Append(match.ReasonCode);
            if (!string.IsNullOrWhiteSpace(match.OverlayText))
                output.Append(" | Overlay ").Append(match.OverlayText);
            if (includeMatchedText && !string.IsNullOrEmpty(match.Text))
                output.Append(" | Text ").Append(match.Text);
            output.AppendLine();
        }
        return output.ToString();
    }
}

/// <summary>Finds text redaction candidates without modifying the source document.</summary>
public static class PdfRedactionSearch
{
    private const string EmailPattern = @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b";
    private const string PhonePattern = @"(?<!\d)(?:\+?1[ .-]?)?(?:\(\d{3}\)|\d{3})[ .-]?\d{3}[ .-]?\d{4}(?!\d)";
    private const string SocialSecurityNumberPattern = @"(?<!\d)\d{3}[ -]\d{2}[ -]\d{4}(?!\d)";
    private const string PaymentCardNumberPattern = @"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)";
    private const string InternationalBankAccountNumberPattern =
        @"(?<![A-Z0-9])(?:[A-Z]{2}\d{2}[A-Z0-9]{11,30}|[A-Z]{2}\d{2}(?: [A-Z0-9]{4}){2,7}(?: [A-Z0-9]{1,4})?)(?![A-Z0-9])";

    /// <summary>Searches ordered documents while isolating document-specific failures.</summary>
    public static IReadOnlyList<PdfRedactionSearchBatchResult> FindBatch(
        IEnumerable<PdfRedactionSearchBatchInput> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var results = new List<PdfRedactionSearchBatchResult>();
        foreach ((PdfRedactionSearchBatchInput input, int index) in inputs.Select(
                     (item, index) => (item ?? throw new ArgumentException(
                         "A redaction search batch input is null.", nameof(inputs)), index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(input.Name))
                    throw new ArgumentException(
                        "A redaction search batch input name is required.");
                ArgumentNullException.ThrowIfNull(input.Pages);
                ArgumentNullException.ThrowIfNull(input.Options);
                results.Add(new PdfRedactionSearchBatchResult(index, input.Name,
                    Find(input.Pages, input.Options, cancellationToken), null));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is ArgumentException or FormatException
                or InvalidOperationException or NotSupportedException or OverflowException)
            {
                results.Add(new PdfRedactionSearchBatchResult(
                    index, input.Name, null, error.Message));
            }
        }
        return Array.AsReadOnly(results.ToArray());
    }

    /// <summary>Builds a reviewable match list from extracted pages.</summary>
    public static PdfRedactionReview Find(IEnumerable<PdfPageContent> pages,
        PdfRedactionSearchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pages);
        Regex expression = Expression(options);
        var results = new List<PdfRedactionMatch>();
        int pageIndex = 0;
        foreach (PdfPageContent page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var spans = new List<(PdfExtractedWord Word, int Start, int End)>();
            string text = string.Join(' ', page.Words.Select(word => word.Text));
            int offset = 0;
            foreach (PdfExtractedWord word in page.Words)
            {
                spans.Add((word, offset, offset + word.Text.Length));
                offset += word.Text.Length + 1;
            }
            foreach (Match match in expression.Matches(text))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Accepted(options.Kind, match.Value)) continue;
                var words = spans.Select((span, index) => (span, index))
                    .Where(item => item.span.End > match.Index && item.span.Start < match.Index + match.Length)
                    .ToArray();
                if (words.Length == 0) continue;
                results.Add(new PdfRedactionMatch($"{pageIndex}:{match.Index}:{match.Length}", pageIndex,
                    match.Value, PdfContentBounds.Union(words.Select(item => item.span.Word.BoundingBox)),
                    words[0].index, words.Length, options.ReasonCode, options.OverlayText)
                {
                    Areas = MatchAreas(words.Select(item => item.span), match.Index, match.Index + match.Length)
                });
            }
            pageIndex++;
        }
        return new PdfRedactionReview(results);
    }

    /// <summary>
    /// Searches text that a page shows through its annotations instead of its content: form field
    /// values and annotation contents such as FreeText notes. Each match proposes the whole annotation,
    /// which redaction then removes. An annotation without a usable rectangle is proposed through a
    /// one-point area at the page corner, because redaction removes any unplaceable annotation on a
    /// page it redacts, and a widget with an empty rectangle, such as a hidden field, through a one-point
    /// area at its position. A malformed annotation is skipped and its page listed in
    /// <see cref="PdfRedactionReview.UnreadableAnnotationPages"/> rather than failing the search.
    /// </summary>
    public static PdfRedactionReview FindInAnnotations(PdfDocument document,
        PdfRedactionSearchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        Regex expression = Expression(options);
        IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(document);
        var results = new List<PdfRedactionMatch>();
        var unreadablePages = new SortedSet<int>();
        for (int pageIndex = 0; pageIndex < pages.Count; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<PdfFormWidgetInfo> widgets;
            try { widgets = PdfFormWidgetReader.ReadPage(document, pageIndex); }
            catch (Exception error) when (error is InvalidOperationException or FormatException)
            {
                unreadablePages.Add(pageIndex);
                continue;
            }
            foreach (PdfFormWidgetInfo widget in widgets)
            {
                var bounds = new PdfContentBounds(widget.Left - widget.PageBoxLeft, widget.Bottom - widget.PageBoxBottom,
                    widget.Right - widget.PageBoxLeft, widget.Top - widget.PageBoxBottom);
                if (!double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Bottom) || !double.IsFinite(bounds.Right)
                    || !double.IsFinite(bounds.Top) || bounds.Width <= 0 || bounds.Height <= 0)
                {
                    // Redaction removes every widget that touches a region, so one point at the widget's
                    // position, kept inside the page, is enough to take an empty rectangle with it.
                    double x = double.IsFinite(bounds.Left) ? Math.Clamp(bounds.Left, 0, Math.Max(0, widget.PageBoxWidth - 1)) : 0;
                    double y = double.IsFinite(bounds.Bottom) ? Math.Clamp(bounds.Bottom, 0, Math.Max(0, widget.PageBoxHeight - 1)) : 0;
                    bounds = new PdfContentBounds(x, y, x + 1, y + 1);
                }
                string[] texts = [widget.Value, widget.DefaultValue, .. widget.Values, .. widget.DefaultValues];
                AddMatches($"field:{pageIndex}:{widget.AnnotationIndex}", pageIndex, texts.Distinct(),
                    bounds, PdfRedactionTargetKind.FormField);
            }
        }
        foreach (PdfCommentInfo comment in PdfCommentReader.Read(document, includeMarkupWithoutText: false, unreadablePages))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfPageInformation page = pages[comment.PageIndex];
            PdfContentBounds bounds = comment.Bounds is { } rectangle
                ? new PdfContentBounds(rectangle.Left - page.Left, rectangle.Bottom - page.Bottom,
                    rectangle.Right - page.Left, rectangle.Top - page.Bottom)
                : new PdfContentBounds(0, 0, 1, 1);
            AddMatches($"annotation:{comment.PageIndex}:{comment.AnnotationIndex}", comment.PageIndex,
                [comment.Contents], bounds, PdfRedactionTargetKind.Comment);
        }
        return new PdfRedactionReview(results) { UnreadableAnnotationPages = Array.AsReadOnly(unreadablePages.ToArray()) };

        void AddMatches(string id, int pageIndex, IEnumerable<string> texts, PdfContentBounds bounds,
            PdfRedactionTargetKind kind)
        {
            foreach (string text in texts)
            {
                if (string.IsNullOrEmpty(text)) continue;
                foreach (Match match in expression.Matches(text))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Accepted(options.Kind, match.Value)) continue;
                    results.Add(new PdfRedactionMatch(id, pageIndex, match.Value, bounds, -1, 0,
                        options.ReasonCode, options.OverlayText, kind));
                    return;
                }
            }
        }
    }

    private static bool Accepted(PdfRedactionSearchKind kind, string value) => kind switch
    {
        PdfRedactionSearchKind.PaymentCardNumber => HasValidPaymentCardChecksum(value),
        PdfRedactionSearchKind.SocialSecurityNumber => HasValidSocialSecurityNumber(value),
        PdfRedactionSearchKind.InternationalBankAccountNumber => HasValidInternationalBankAccountNumber(value),
        _ => true
    };

    private static Regex Expression(PdfRedactionSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(options.Kind)) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Timeout <= TimeSpan.Zero || options.Timeout > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(options), "The search timeout must be from zero through ten seconds.");
        ValidatePresentation(options.ReasonCode, options.OverlayText, nameof(options));
        string pattern = options.Kind switch
        {
            PdfRedactionSearchKind.ExactText when !string.IsNullOrEmpty(options.Query) => Regex.Escape(options.Query),
            PdfRedactionSearchKind.RegularExpression when !string.IsNullOrEmpty(options.Query) => options.Query,
            PdfRedactionSearchKind.EmailAddress => EmailPattern,
            PdfRedactionSearchKind.PhoneNumber => PhonePattern,
            PdfRedactionSearchKind.SocialSecurityNumber => SocialSecurityNumberPattern,
            PdfRedactionSearchKind.PaymentCardNumber => PaymentCardNumberPattern,
            PdfRedactionSearchKind.InternationalBankAccountNumber =>
                InternationalBankAccountNumberPattern,
            _ => throw new ArgumentException("Search text is required.", nameof(options))
        };
        RegexOptions flags = RegexOptions.CultureInvariant;
        if (!options.MatchCase) flags |= RegexOptions.IgnoreCase;
        return new Regex(pattern, flags, options.Timeout);
    }

    // The matched letters of each word, merged into one box per text line. A word whose letters
    // do not map one-to-one onto its text falls back to the whole word.
    private static IReadOnlyList<PdfContentBounds> MatchAreas(
        IEnumerable<(PdfExtractedWord Word, int Start, int End)> spans, int matchStart, int matchEnd)
    {
        var pieces = new List<PdfContentBounds>();
        foreach (var (word, start, _) in spans)
        {
            int localStart = Math.Max(0, matchStart - start);
            int localEnd = Math.Min(word.Text.Length, matchEnd - start);
            if (localEnd <= localStart) continue;
            var letters = new List<PdfContentBounds>();
            int offset = 0;
            foreach (PdfExtractedLetter letter in word.Letters)
            {
                int letterEnd = offset + letter.Value.Length;
                if (letterEnd > localStart && offset < localEnd) letters.Add(letter.BoundingBox);
                offset = letterEnd;
            }
            pieces.Add(letters.Count > 0 && offset == word.Text.Length
                ? PdfContentBounds.Union(letters) : word.BoundingBox);
        }
        var lines = new List<PdfContentBounds>();
        foreach (PdfContentBounds piece in pieces)
        {
            if (lines.Count > 0 && SameLine(lines[^1], piece))
                lines[^1] = PdfContentBounds.Union([lines[^1], piece]);
            else
                lines.Add(piece);
        }
        return Array.AsReadOnly(lines.ToArray());

        static bool SameLine(PdfContentBounds first, PdfContentBounds second)
        {
            double overlap = Math.Min(first.Top, second.Top) - Math.Max(first.Bottom, second.Bottom);
            return overlap > 0.5 * Math.Min(first.Height, second.Height);
        }
    }

    internal static void ValidatePresentation(string? reasonCode, string? overlayText, string parameterName)
    {
        if (reasonCode is not null && string.IsNullOrWhiteSpace(reasonCode))
            throw new ArgumentException("A redaction reason code cannot be blank.", parameterName);
        if (overlayText is not null && string.IsNullOrWhiteSpace(overlayText))
            throw new ArgumentException("Redaction overlay text cannot be blank.", parameterName);
    }

    private static bool HasValidPaymentCardChecksum(string value)
    {
        int digitCount = value.Count(char.IsAsciiDigit);
        if (digitCount is < 13 or > 19) return false;
        int sum = 0;
        bool doubleDigit = false;
        for (int index = value.Length - 1; index >= 0; index--)
        {
            if (!char.IsAsciiDigit(value[index])) continue;
            int digit = value[index] - '0';
            if (doubleDigit)
            {
                digit *= 2;
                if (digit > 9) digit -= 9;
            }
            sum += digit;
            doubleDigit = !doubleDigit;
        }
        return sum % 10 == 0;
    }

    private static bool HasValidSocialSecurityNumber(string value)
    {
        string digits = new([.. value.Where(char.IsAsciiDigit)]);
        if (digits.Length != 9) return false;
        int area = int.Parse(digits.AsSpan(0, 3), System.Globalization.CultureInfo.InvariantCulture);
        int group = int.Parse(digits.AsSpan(3, 2), System.Globalization.CultureInfo.InvariantCulture);
        int serial = int.Parse(digits.AsSpan(5, 4), System.Globalization.CultureInfo.InvariantCulture);
        return area is > 0 and < 900 and not 666 && group > 0 && serial > 0;
    }

    private static bool HasValidInternationalBankAccountNumber(string value)
    {
        string compact = new([.. value.Where(character => character != ' ').Select(char.ToUpperInvariant)]);
        if (compact.Length is < 15 or > 34
            || !char.IsAsciiLetter(compact[0]) || !char.IsAsciiLetter(compact[1])
            || !char.IsAsciiDigit(compact[2]) || !char.IsAsciiDigit(compact[3])
            || compact.Any(character => !char.IsAsciiLetterOrDigit(character))) return false;
        int remainder = 0;
        foreach (char character in compact[4..].Concat(compact[..4]))
        {
            if (char.IsAsciiDigit(character))
                remainder = (remainder * 10 + character - '0') % 97;
            else
                remainder = (remainder * 100 + character - 'A' + 10) % 97;
        }
        return remainder == 1;
    }
}
