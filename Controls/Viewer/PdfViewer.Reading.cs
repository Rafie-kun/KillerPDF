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
            private Dictionary<int, List<int>>? _structureOrder;
            private bool _structureRead;

            // A tagged PDF's reading order: for each page, its marked-content IDs in the order the
            // tag tree reaches them. Null for an untagged or unreadable tree, which reads in the
            // viewer's visual order instead.
            private Dictionary<int, List<int>>? StructureOrder(string file)
            {
                ReadDocumentFacts(file);
                return _structureOrder;
            }

            // Each page's comments as readable lines: the PDF's own review comments, read after the
            // page body so a screen reader reaches them in page order.
            private readonly Dictionary<int, List<string>> _comments = [];

            private List<string> PageComments(string file, int page)
            {
                ReadDocumentFacts(file);
                var lines = new List<string>(_comments.TryGetValue(page, out List<string>? saved) ? saved : []);
                // Text boxes added in KillerPDF and not yet saved are on screen too.
                if (viewer._annotations.TryGetValue(page, out var placed))
                    foreach (var text in placed.OfType<TextAnnotation>())
                        if (!string.IsNullOrWhiteSpace(text.Content)) lines.Add(text.Content.Trim());
                return lines;
            }

            private void ReadDocumentFacts(string file)
            {
                if (_structureRead) return;
                _structureRead = true;
                _comments.Clear();
                KillerPdf.Engine.Documents.PdfDocument document;
                try { document = KillerPdf.Engine.Documents.PdfDocument.Open(System.IO.File.ReadAllBytes(file)); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _structureOrder = null; return; }
                try
                {
                    foreach (var comment in KillerPdf.Engine.Documents.PdfCommentReader.Read(document))
                    {
                        if (string.IsNullOrWhiteSpace(comment.Contents)) continue;
                        if (!_comments.TryGetValue(comment.PageIndex, out List<string>? lines)) _comments[comment.PageIndex] = lines = [];
                        lines.Add(string.IsNullOrWhiteSpace(comment.Author)
                            ? string.Format(viewer.Loc("Str_Read_Comment"), comment.Contents.Trim())
                            : string.Format(viewer.Loc("Str_Read_CommentBy"), comment.Author.Trim(), comment.Contents.Trim()));
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { _comments.Clear(); }
                try
                {
                    var order = new Dictionary<int, List<int>>();
                    foreach (var item in KillerPdf.Engine.Diagnostics.PdfAccessibilityReadingOrder.Read(document)
                                 .Items.OrderBy(item => item.Sequence))
                    {
                        if (item.MarkedContentId is not int mcid) continue;
                        if (!order.TryGetValue(item.PageIndex, out List<int>? list)) order[item.PageIndex] = list = [];
                        list.Add(mcid);
                    }
                    _structureOrder = order.Count == 0 ? null : order;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _structureOrder = null;   // no structure tree, or one that cannot be read
                }
            }

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
                    // Placed text boxes are read too, so adding or removing one rebuilds the text.
                    return (file, ticks, PageCount, viewer._annotations.Values.Sum(list => list.Count));
                }
            }

            public PageReadingText PageText(int page)
            {
                object version = Version;
                if (!Equals(version, _pagesVersion))
                {
                    _pages.Clear();
                    _structureOrder = null;
                    _structureRead = false;
                    _pagesVersion = version;
                    TextChanged?.Invoke();
                }
                if (_pages.TryGetValue(page, out PageReadingText? cached)) return cached;
                PageReadingText text = PageReadingText.Empty;
                if (viewer._currentFile is { } file && page >= 0 && page < PageCount)
                {
                    try
                    {
                        PageTextRuns? runs = viewer._textRuns.GetPage(file, page);
                        if (runs is not null)
                            text = StructureOrder(file) is { } order && order.TryGetValue(page, out List<int>? mcids)
                                ? PageReadingText.BuildTagged([.. runs.Chars.Select(c => (c.Value, c.Word, c.Line, c.MarkedContentId))], mcids)
                                : PageReadingText.Build([.. runs.Chars.Select(c => (c.Value, c.Word, c.Line))]);
                        text = text.WithAppendix(PageComments(file, page));
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
