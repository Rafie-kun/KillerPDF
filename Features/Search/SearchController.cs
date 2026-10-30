using KillerPDF.Services;

namespace KillerPDF.Features
{
    /// <summary>
    /// The document-search state machine: runs SearchService over the working file, keeps the
    /// per-page rect map and the flat reading-ordered match list, and steps the match cursor.
    /// Moved out of Search.cs (MainWindow) in the KillerUI refactor.
    ///
    /// Holds no controls. Talks to the window only through <see cref="ISearchHost"/>; the search
    /// bar UI, debounce, and the highlight painting stay in the shell half (Shell/Search.cs).
    /// </summary>
    internal sealed class SearchController
    {
        private readonly ISearchHost _host;
        private readonly SearchService _searchService = new();

        internal SearchController(ISearchHost host) => _host = host;

        // Whole-document search results (PDF-space rects per page). Settable references on
        // purpose: Tabs.cs parks these per tab and swaps them back on a tab switch, exactly as
        // it did when they were MainWindow fields.
        internal Dictionary<int, List<(double left, double bottom, double right, double top)>> AllSearchRects { get; set; } = [];
        internal List<int> ResultPages { get; set; } = [];
        internal int PageCursor { get; set; } = -1;

        /// <summary>Zero-based pages the last search could not read. A search that found nothing on the
        /// others has not shown the term is absent from these, so every result view names them.</summary>
        internal IReadOnlyList<int> UnreadablePages { get; set; } = [];

        // Flat, reading-ordered list of every match (page + rect) so Enter steps word-by-word rather
        // than page-by-page; _matchCursor indexes it and that match is drawn with extra emphasis.
        private readonly List<(int page, double left, double bottom, double right, double top)> _matches = [];
        private int _matchCursor = -1;
        private int _totalHits;

        /// <summary>True while any page has result rects - the F3 and repaint-on-page-change gate.</summary>
        internal bool HasResults => AllSearchRects.Count > 0;

        /// <summary>Match case and Whole word, the search bar toggles. Find and Replace uses the same
        /// options, so what it replaces is what the highlights showed.</summary>
        internal bool MatchCase { get; set; }
        internal bool WholeWord { get; set; }

        /// <summary>The text of the most recent search, for features that act on its exact matches.</summary>
        internal string LastQuery { get; private set; } = string.Empty;

        /// <summary>The emphasized match, or null when the cursor is not on one.</summary>
        internal (int page, double left, double bottom, double right, double top)? CurrentMatch =>
            _matchCursor >= 0 && _matchCursor < _matches.Count ? _matches[_matchCursor] : null;

        internal bool TryGetPageRects(int page,
            out List<(double left, double bottom, double right, double top)> rects) =>
            AllSearchRects.TryGetValue(page, out rects!);

        internal IEnumerable<int> PagesWithResults => AllSearchRects.Keys;

        /// <summary>The query-too-short reset (search box text dropped under 2 chars).</summary>
        internal void ClearMatches()
        {
            AllSearchRects.Clear();
            ResultPages.Clear();
            _matches.Clear();
            _matchCursor = -1;
            PageCursor = -1;
            UnreadablePages = [];
        }

        /// <summary>The new/closed-document reset - exactly the three things FileOperations
        /// cleared when these were fields (the match list is rebuilt by the next Run).</summary>
        internal void ClearPageResults()
        {
            AllSearchRects.Clear();
            ResultPages.Clear();
            PageCursor = -1;
            UnreadablePages = [];
        }

        internal void Run(string query)
        {
            LastQuery = query ?? string.Empty;
            _host.ClearHighlights();
            AllSearchRects.Clear();
            ResultPages.Clear();
            _matches.Clear();
            _matchCursor = -1;
            PageCursor = -1;
            UnreadablePages = [];

            if (string.IsNullOrWhiteSpace(query) || _host.CurrentFile is null)
            {
                _host.SetResultText("");
                return;
            }

            try
            {
                var sr = SearchService.Search(_host.CurrentFile, query, MatchCase, WholeWord);
                UnreadablePages = [.. sr.UnreadablePages];

                // Text hidden under an edit, such as a replaced line, no longer counts as a match.
                foreach (var kvp in sr.PageRects)
                {
                    var visible = kvp.Value.Where(r => !_host.IsCovered(kvp.Key, r.Left, r.Bottom, r.Right, r.Top)).ToList();
                    if (visible.Count == 0) continue;
                    AllSearchRects[kvp.Key] = visible;
                    ResultPages.Add(kvp.Key);
                }
                ResultPages.Sort();

                // Flatten every match into one reading-ordered list (page asc, then top-to-bottom,
                // then left-to-right) so navigation steps word-by-word across the whole document.
                foreach (var page in ResultPages)
                    foreach (var (left, bottom, right, top) in AllSearchRects[page].OrderByDescending(r => r.top).ThenBy(r => r.left))
                        _matches.Add((page, left, bottom, right, top));

                if (_matches.Count == 0)
                {
                    // Pages that could not be read were not searched, so this is not a plain "No matches".
                    if (UnreadablePages.Count == 0)
                    {
                        _host.SetResultText(_host.Loc("Str_Search_NoMatches"));
                        return;
                    }
                    _host.SetResultText(string.Format(_host.Loc("Str_Search_NoMatchesPartial"), UnreadablePages.Count));
                    _host.SetStatus(SkippedPagesText());
                    return;
                }

                _totalHits = _matches.Count;

                // Start at the first match on or after the current page.
                int startPage = _host.CurrentPageIndex;
                _matchCursor = _matches.FindIndex(m => m.page >= startPage);
                if (_matchCursor < 0) _matchCursor = 0;

                GoToCurrentMatch();
                if (UnreadablePages.Count > 0) _host.SetStatus(SkippedPagesText());
            }
            catch
            {
                _host.SetResultText(_host.Loc("Str_Search_Error"));
            }
        }

        internal void Next()
        {
            if (_matches.Count == 0) return;
            _matchCursor = (_matchCursor + 1) % _matches.Count;
            GoToCurrentMatch();
        }

        internal void Prev()
        {
            if (_matches.Count == 0) return;
            _matchCursor = (_matchCursor - 1 + _matches.Count) % _matches.Count;
            GoToCurrentMatch();
        }

        // Navigates to the current match's page (if needed), updates the counter, and repaints
        // highlights with the current match emphasized. Shared by Run and Next/Prev.
        private void GoToCurrentMatch()
        {
            if (_matchCursor < 0 || _matchCursor >= _matches.Count) return;
            int targetPage = _matches[_matchCursor].page;
            PageCursor = ResultPages.IndexOf(targetPage);   // keep the persisted page-cursor sane
            UpdateStatus();
            if (_host.CurrentPageIndex != targetPage)
                _host.GoToPage(targetPage);
            _host.RepaintHighlights();
        }

        // Compact count ("12 / 73" = current match / total matches); page breakdown in the tooltip.
        private void UpdateStatus()
        {
            if (_matches.Count == 0)
            {
                _host.SetResultCount(_host.Loc("Str_Search_NoMatches"), null);
                return;
            }
            int pages = ResultPages.Count;
            string summary = string.Format(_host.Loc("Str_Search_Summary"), _matches.Count, pages);
            if (UnreadablePages.Count > 0) summary += "\n" + SkippedPagesText();
            _host.SetResultCount($"{_matchCursor + 1} / {_matches.Count}", summary);
        }

        private string SkippedPagesText() => SkippedPagesMessage(UnreadablePages);

        /// <summary>Names the (zero-based) pages that were not searched because they could not be read,
        /// for the search bar, the status line and the Find-and-Redact warning.</summary>
        internal string SkippedPagesMessage(IEnumerable<int> pages) =>
            string.Format(_host.Loc("Str_Search_PagesSkipped"), SearchService.SkippedPagesText(pages));
    }
}
