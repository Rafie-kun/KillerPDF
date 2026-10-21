using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    // The screen-reader view of this pane's document (BF-U001): page text in the same reading
    // order selection and search use, mapped back to characters so a reader's range can be
    // outlined on the page, selected and scrolled to.
    public partial class PdfViewer
    {
        private ReadingSource? _readingSource;

        private void InitializeReadingSurface()
        {
            _readingSource = new ReadingSource(this);
            DocSurfacePad.Source = _readingSource;
        }

        /// <summary>Tells a screen reader the selection moved. Called when selection is repainted or cleared.</summary>
        private void NotifyReadingSelectionChanged() => _readingSource?.RaiseSelectionChanged();

        private sealed class ReadingSource(PdfViewer viewer) : IDocumentReadingSource
        {
            private readonly Dictionary<int, PageReadingText> _pages = [];
            private object? _pagesVersion;

            public event Action? TextChanged;
            public event Action? SelectionChanged;

            internal void RaiseSelectionChanged() => SelectionChanged?.Invoke();

            public int PageCount => viewer._doc?.PageCount ?? 0;
            public int CurrentPage => viewer._currentPage;

            public object Version
            {
                get
                {
                    string? file = viewer._currentFile;
                    long ticks = 0;
                    try { if (file is not null && System.IO.File.Exists(file)) ticks = System.IO.File.GetLastWriteTimeUtc(file).Ticks; }
                    catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
                    return (file, ticks, PageCount);
                }
            }

            public PageReadingText PageText(int page)
            {
                object version = Version;
                if (!Equals(version, _pagesVersion)) { _pages.Clear(); _pagesVersion = version; TextChanged?.Invoke(); }
                if (_pages.TryGetValue(page, out PageReadingText? cached)) return cached;
                PageReadingText text = PageReadingText.Empty;
                if (viewer._currentFile is { } file && page >= 0 && page < PageCount)
                {
                    try
                    {
                        PageTextRuns? runs = viewer._textRuns.GetPage(file, page);
                        if (runs is not null)
                            text = PageReadingText.Build([.. runs.Chars.Select(c => (c.Value, c.Word, c.Line))]);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        text = PageReadingText.Empty;   // an unreadable page reads as empty, never breaks the reader
                    }
                }
                return _pages[page] = text;
            }

            public IReadOnlyList<Rect> ScreenRects(int page, int startCaret, int endCaret)
            {
                var result = new List<Rect>();
                if (viewer._currentFile is not { } file || !viewer._renderDims.TryGetValue(page, out var rd)) return result;
                if (viewer.VisibleCanvasForPage(page) is not { } canvas || !canvas.IsVisible
                    || PresentationSource.FromVisual(canvas) is null) return result;
                PageTextRuns? runs = viewer._textRuns.GetPage(file, page);
                if (runs is null) return result;
                foreach (Rect rect in viewer.CaretRangeRects(page, runs, startCaret, endCaret, rd))
                {
                    Point a = canvas.PointToScreen(rect.TopLeft), b = canvas.PointToScreen(rect.BottomRight);
                    result.Add(new Rect(a, b));
                }
                return result;
            }

            public void Select(int startPage, int startCaret, int endPage, int endCaret)
            {
                if (viewer._currentFile is null) return;
                viewer.ClearTextSelection();
                viewer._txtSelAnchor = (startPage, startCaret);
                viewer._txtSelFocus = (endPage, endCaret);
                viewer._txtSelHasRange = (startPage, startCaret) != (endPage, endCaret);
                viewer.RepaintTextSelection();
                // The selection is shown and available to Copy; the clipboard is left alone.
                viewer._selectedText = viewer._txtSelHasRange ? viewer.BuildSelectedText(out _) : null;
                RaiseSelectionChanged();
            }

            public (int StartPage, int StartCaret, int EndPage, int EndCaret)? Selection
            {
                get
                {
                    if (!viewer._txtSelHasRange) return null;
                    var (s, e) = viewer.OrderedSelection();
                    return (s.Page, s.Caret, e.Page, e.Caret);
                }
            }

            public void ScrollToPage(int page)
            {
                if (page >= 0 && page < PageCount && page != viewer._currentPage) viewer.NavigateToPageExt(page);
            }
        }
    }
}
