using PdfDocument = KillerPDF.Services.PdfContentDocument;

namespace KillerPDF.Services
{
    internal sealed class SearchResult
    {
        public Dictionary<int, List<(double Left, double Bottom, double Right, double Top)>> PageRects { get; } = [];
        public List<int> ResultPages { get; } = [];
        public int TotalHits { get; set; }

        /// <summary>
        /// Zero-based pages whose text could not be read and were left out of the result. A search that
        /// found nothing on the other pages has not shown the term is absent from these.
        /// </summary>
        public List<int> UnreadablePages { get; } = [];

        /// <summary>Why each page in <see cref="UnreadablePages"/> was skipped, by page.</summary>
        public Dictionary<int, string> UnreadableMessages { get; } = [];
    }

    internal sealed class SearchService
    {
        /// <summary>
        /// Scans every page of <paramref name="filePath"/> for <paramref name="query"/>, ignoring case
        /// unless <paramref name="matchCase"/> is set; <paramref name="wholeWord"/> skips matches inside
        /// longer words. Returns an empty result when query is blank. A file that cannot be opened, or whose
        /// pages all fail to read, throws, because an empty result would tell the user the term is absent. A
        /// page the text reader refuses is skipped and listed in <see cref="SearchResult.UnreadablePages"/>, so
        /// one bad page does not cost the matches on every other page.
        /// </summary>
        public static SearchResult Search(string filePath, string query, bool matchCase = false, bool wholeWord = false)
        {
            var result = new SearchResult();
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(filePath))
                return result;

            using var doc = PdfDocument.Open(filePath);
            Exception? firstFailure = null;
            for (int pi = 0; pi < doc.NumberOfPages; pi++)
            {
                List<(double Left, double Bottom, double Right, double Top)> hits;
                try { hits = FindMatchesOnPage(doc.GetPage(pi + 1), query, matchCase, wholeWord); }
                catch (Exception ex) when (CanSkipPage(ex))
                {
                    result.UnreadablePages.Add(pi);
                    result.UnreadableMessages[pi] = ex.Message;
                    firstFailure ??= ex;
                    continue;
                }
                if (hits.Count > 0)
                {
                    result.PageRects[pi] = hits;
                    result.ResultPages.Add(pi);
                    result.TotalHits += hits.Count;
                }
            }

            RequireAReadablePage(doc.NumberOfPages, result.UnreadablePages.Count, firstFailure);
            return result;
        }

        // A page the reader refuses is skipped; running out of memory or being cancelled is not that.
        private static bool CanSkipPage(Exception ex) => ex is not (OutOfMemoryException or OperationCanceledException);

        // Every page failing is a file that cannot be searched, not a file without the term.
        private static void RequireAReadablePage(int pageCount, int unreadableCount, Exception? firstFailure)
        {
            if (firstFailure is not null && unreadableCount == pageCount)
                throw new System.IO.InvalidDataException("No page of the document could be read: " + firstFailure.Message, firstFailure);
        }

        /// <summary>
        /// The one-based page numbers of <paramref name="zeroBasedPages"/> as a comma-separated list for a
        /// message, cut after ten so a document that fails everywhere does not fill the status bar.
        /// </summary>
        internal static string SkippedPagesText(IEnumerable<int> zeroBasedPages)
        {
            const int Shown = 10;
            List<int> pages = [.. zeroBasedPages.Select(page => page + 1)];
            string text = string.Join(", ", pages.Take(Shown).Select(page => page.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return pages.Count > Shown ? text + ", ..." : text;
        }

        internal static List<(double Left, double Bottom, double Right, double Top)> FindMatchesOnPage(
            KillerPdf.Engine.Documents.PdfPageContent page, string query, bool matchCase = false, bool wholeWord = false)
        {
            var result = new List<(double, double, double, double)>();
            var words = page.GetWords().ToList();
            var comparison = matchCase ? System.StringComparison.Ordinal : System.StringComparison.OrdinalIgnoreCase;
            bool Matches(string text) => wholeWord
                ? TextReplacePlanner.FindAll(text, query, matchCase, wholeWord: true).Count > 0
                : text.Contains(query, comparison);

            // A multi-word phrase can span adjacent words; a single token never does, so for single-token
            // queries we only do the per-word substring match (the cross-word union box below would
            // otherwise highlight whole runs of words leading up to the matching one).
            bool isPhrase = query.Trim().Contains(' ');

            for (int i = 0; i < words.Count; i++)
            {
                if (Matches(words[i].Text))
                {
                    var bb = words[i].BoundingBox;
                    result.Add((bb.Left, bb.Bottom, bb.Right, bb.Top));
                    continue;
                }

                if (!isPhrase) continue;

                // Multi-word match
                string combined = words[i].Text;
                for (int j = i + 1; j < words.Count && combined.Length < query.Length + 20; j++)
                {
                    combined += " " + words[j].Text;
                    if (Matches(combined))
                    {
                        double minX = double.MaxValue, minY = double.MaxValue;
                        double maxX = double.MinValue, maxY = double.MinValue;
                        for (int k = i; k <= j; k++)
                        {
                            var wbb = words[k].BoundingBox;
                            minX = Math.Min(minX, wbb.Left);
                            minY = Math.Min(minY, wbb.Bottom);
                            maxX = Math.Max(maxX, wbb.Right);
                            maxY = Math.Max(maxY, wbb.Top);
                        }
                        result.Add((minX, minY, maxX, maxY));
                        break;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Finds every case-insensitive occurrence of <paramref name="query"/> on every page and
        /// returns the bounds of only the matched characters, one rectangle per word a match touches. A page
        /// that cannot be read throws, so the caller never proceeds as if that page had been searched.
        /// </summary>
        internal static Dictionary<int, List<(double Left, double Bottom, double Right, double Top)>> FindExactMatches(
            string filePath, string query) => FindExactMatches(filePath, query, null);

        /// <summary>
        /// The same search, except a page the reader refuses is left out and its zero-based number added to
        /// <paramref name="unreadablePages"/>, which the caller must show the user. Still throws when the
        /// file cannot be opened or no page can be read.
        /// </summary>
        internal static Dictionary<int, List<(double Left, double Bottom, double Right, double Top)>> FindExactMatches(
            string filePath, string query, out List<int> unreadablePages)
        {
            unreadablePages = [];
            return FindExactMatches(filePath, query, unreadablePages);
        }

        private static Dictionary<int, List<(double Left, double Bottom, double Right, double Top)>> FindExactMatches(
            string filePath, string query, List<int>? unreadablePages)
        {
            var result = new Dictionary<int, List<(double, double, double, double)>>();
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(filePath)) return result;
            using var doc = PdfDocument.Open(filePath);
            Exception? firstFailure = null;
            for (int pi = 0; pi < doc.NumberOfPages; pi++)
            {
                List<(double Left, double Bottom, double Right, double Top)> hits;
                try { hits = FindExactMatchesOnPage(doc.GetPage(pi + 1), query); }
                catch (Exception ex) when (unreadablePages is not null && CanSkipPage(ex))
                {
                    unreadablePages.Add(pi);
                    firstFailure ??= ex;
                    continue;
                }
                if (hits.Count > 0) result[pi] = hits;
            }
            RequireAReadablePage(doc.NumberOfPages, unreadablePages?.Count ?? 0, firstFailure);
            return result;
        }

        /// <summary>
        /// The areas a redaction of <paramref name="term"/> covers: its page-text matches, plus every form
        /// field and annotation whose value or text contains it, which the page content does not include.
        /// Case-insensitive, like <see cref="FindExactMatches"/>.
        /// </summary>
        internal static Dictionary<int, List<(double Left, double Bottom, double Right, double Top)>> FindRedactionTermAreas(
            string filePath, string term) => FindRedactionTermAreas(filePath, [term]);

        /// <summary>
        /// The areas for several terms at once, merged per page. The file is read and parsed once for all
        /// of them; a macro with many terms on a large scan otherwise parsed it twice per term.
        /// </summary>
        internal static Dictionary<int, List<(double Left, double Bottom, double Right, double Top)>> FindRedactionTermAreas(
            string filePath, IEnumerable<string> terms)
        {
            var result = new Dictionary<int, List<(double, double, double, double)>>();
            if (string.IsNullOrWhiteSpace(filePath)) return result;
            List<string> queries = [.. terms.Where(term => !string.IsNullOrWhiteSpace(term))];
            if (queries.Count == 0) return result;
            byte[] bytes = System.IO.File.ReadAllBytes(filePath);
            using var pages = PdfDocument.Open(bytes);
            var document = PdfEngineIntegration.OpenForReading(bytes);
            foreach (string term in queries)
            {
                for (int pi = 0; pi < pages.NumberOfPages; pi++)
                {
                    var hits = FindExactMatchesOnPage(pages.GetPage(pi + 1), term);
                    if (hits.Count > 0) Areas(pi).AddRange(hits);
                }
                var options = new KillerPdf.Engine.Documents.PdfRedactionSearchOptions
                {
                    Kind = KillerPdf.Engine.Documents.PdfRedactionSearchKind.ExactText,
                    Query = term.Trim(),
                    Timeout = System.TimeSpan.FromSeconds(2)
                };
                foreach (var match in KillerPdf.Engine.Documents.PdfRedactionSearch.FindInAnnotations(document, options).Matches)
                {
                    var areas = Areas(match.PageIndex);
                    var area = (match.Bounds.Left, match.Bounds.Bottom, match.Bounds.Right, match.Bounds.Top);
                    if (!areas.Contains(area)) areas.Add(area);
                }
            }
            return result;

            List<(double, double, double, double)> Areas(int page)
            {
                if (!result.TryGetValue(page, out var areas)) result[page] = areas = [];
                return areas;
            }
        }

        internal static List<(double Left, double Bottom, double Right, double Top)> FindExactMatchesOnPage(
            KillerPdf.Engine.Documents.PdfPageContent page, string query)
        {
            var result = new List<(double, double, double, double)>();
            string needle = query.Trim();
            if (needle.Length == 0) return result;
            var words = page.GetWords().ToList();

            // Page text with one space between words, and each character mapped to its word and letter.
            var text = new System.Text.StringBuilder();
            var map = new List<(int Word, int Letter)>();
            for (int w = 0; w < words.Count; w++)
            {
                if (w > 0) { text.Append(' '); map.Add((-1, -1)); }
                var letters = words[w].Letters;
                for (int l = 0; l < letters.Count; l++)
                    foreach (char c in letters[l].Value) { text.Append(c); map.Add((w, l)); }
            }

            string haystack = text.ToString();
            for (int start = haystack.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase); start >= 0;
                 start = haystack.IndexOf(needle, start + needle.Length, System.StringComparison.OrdinalIgnoreCase))
            {
                foreach (var word in map.Skip(start).Take(needle.Length).Where(m => m.Word >= 0).GroupBy(m => m.Word))
                {
                    var boxes = word.Select(m => m.Letter).Distinct()
                        .Select(letter => words[word.Key].Letters[letter].BoundingBox).ToList();
                    result.Add((boxes.Min(b => b.Left), boxes.Min(b => b.Bottom),
                        boxes.Max(b => b.Right), boxes.Max(b => b.Top)));
                }
            }
            return result;
        }
    }
}
