using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    // Redact tool: drag areas on the page (or mark the current search results), then remove
    // everything under them for good. Marks are kept in unrotated, crop-relative page points so they
    // survive zoom and view changes; they belong to the document they were drawn on and are dropped
    // when the tool closes.
    public partial class PdfViewer
    {
        private const string RedactMarkTag = "RedactMark";
        private readonly List<RedactionMark> _redactMarks = [];
        private DocumentSession? _redactSession;
        private Border? _redactBar;
        private TextBlock? _redactCountText;
        private Rectangle? _redactDrawRect;
        private Point _redactDrawStart;
        private bool _redactDrawing;

        private sealed record RedactionMark(int PageIndex, double Left, double Bottom, double Right, double Top);

        /// <summary>Shows the Redact bar for the active document, keeping marks already drawn on it.</summary>
        internal void ShowRedactionBar()
        {
            if (_doc is null) return;
            if (!ReferenceEquals(_redactSession, _active))
            {
                _redactMarks.Clear();
                _redactSession = _active;
            }
            if (_redactBar is not null)
            {
                UpdateRedactionCount();
                return;
            }

            _annotBarDragInners.Clear();
            var outer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 8, 2), Background = Brushes.Transparent };
            var grip = MakeBarGrip();
            outer.Children.Add(grip);

            _redactCountText = new TextBlock
            {
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            _redactCountText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            outer.Children.Add(_redactCountText);

            Button BarButton(string key, string tipKey, bool accent, RoutedEventHandler click)
            {
                var button = UiKit.Make(Loc(key), accent);
                button.SetResourceReference(ContentControl.ContentProperty, key);
                button.SetResourceReference(FrameworkElement.ToolTipProperty, tipKey);
                button.Padding = new Thickness(10, 4, 10, 4);
                button.Margin = new Thickness(0, 0, 5, 0);
                button.Click += click;
                outer.Children.Add(button);
                return button;
            }

            BarButton("Str_Redact_MarkSearch", "Str_Redact_MarkSearchTip", false, (_, _) => MarkSearchResultsForRedaction());
            BarButton("Str_Redact_Clear", "Str_Redact_ClearTip", false, (_, _) => ClearRedactionMarks());
            var divider = new Border { Width = 1, Margin = new Thickness(2, 0, 8, 0), VerticalAlignment = VerticalAlignment.Stretch };
            divider.SetResourceReference(Border.BackgroundProperty, "CardBorderBrush");
            outer.Children.Add(divider);
            BarButton("Str_Redact_Apply", "Str_Redact_ApplyTip", true, (_, _) => ApplyRedaction());

            var bar = new Border
            {
                BorderThickness = new Thickness(1, 0, 1, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Effect = AnnotBarShadow(),
                Child = BuildBarHost(outer)
            };
            bar.SetResourceReference(Border.BackgroundProperty, "AnnotationBarBrush");
            bar.SetResourceReference(Border.BorderBrushProperty, "AnnotationBarBorderBrush");
            _redactBar = bar;

            if (PagePreviewPanel.Parent is Grid previewArea)
            {
                Panel.SetZIndex(bar, 100);
                previewArea.Children.Add(bar);
                PlaceAnnotationBar(bar, grip, fadeIn: false);
            }
            _annotBarTool = EditTool.Redact;
            _annotBarMinimized = false;
            UpdateRedactionCount();
            RepaintRedactionMarks();
        }

        /// <summary>Closes the Redact bar and discards its marks.</summary>
        internal void HideRedactionBar()
        {
            if (_redactBar is not null)
            {
                (_redactBar.Parent as Panel)?.Children.Remove(_redactBar);
                _redactBar = null;
            }
            _redactCountText = null;
            CancelRedactionDrag();
            _redactMarks.Clear();
            _redactSession = null;
            RemoveRedactionMarkVisuals();
            if (_annotBarTool == EditTool.Redact) _annotBarTool = null;
        }

        /// <summary>True when the event came from the Redact bar, which the page canvas must not swallow.</summary>
        internal bool IsInRedactionBar(object? source) =>
            _redactBar is not null && source is DependencyObject element && IsDescendantOf(element, _redactBar);

        private void UpdateRedactionCount()
        {
            if (_redactCountText is null) return;
            int pages = _redactMarks.Select(mark => mark.PageIndex).Distinct().Count();
            _redactCountText.Text = _redactMarks.Count == 0
                ? Loc("Str_Redact_NoMarks")
                : string.Format(Loc("Str_Redact_Count"), _redactMarks.Count, pages);
        }

        private bool TryPageGeometry(int pageIndex, out double pageWidth, out double pageHeight,
            out double canvasWidth, out double canvasHeight, out int rotation)
        {
            pageWidth = pageHeight = canvasWidth = canvasHeight = 0;
            rotation = 0;
            if (_doc is null || !_renderDims.TryGetValue(pageIndex, out var dims) || dims.w <= 0 || dims.h <= 0)
                return false;
            PdfEngineDocumentSession session = EnsureEngineDocumentSession();
            if (pageIndex < 0 || pageIndex >= session.PageCount) return false;
            pageWidth = session.Pages[pageIndex].Width;
            pageHeight = session.Pages[pageIndex].Height;
            canvasWidth = dims.w;
            canvasHeight = dims.h;
            rotation = _pageRotations.TryGetValue(pageIndex, out int stored) ? stored : session.Pages[pageIndex].Rotation;
            return pageWidth > 0 && pageHeight > 0;
        }

        private Rect? MarkCanvasRect(RedactionMark mark)
        {
            if (!TryPageGeometry(mark.PageIndex, out double pw, out double ph, out double cw, out double ch, out int rot))
                return null;
            return RedactionGeometry.PageToCanvas(mark.Left, mark.Bottom, mark.Right, mark.Top, pw, ph, cw, ch, rot);
        }

        /// <summary>Repaints this page's marks on its overlay. Called at the end of every annotation re-render.</summary>
        internal void ApplyRedactionMarks(int pageIndex, Canvas? canvas)
        {
            if (canvas is null) return;
            foreach (var old in canvas.Children.OfType<Rectangle>().Where(r => r.Tag is RedactionMark).ToList())
                canvas.Children.Remove(old);
            if (_currentTool != EditTool.Redact || !ReferenceEquals(_redactSession, _active)) return;
            foreach (RedactionMark mark in _redactMarks.Where(mark => mark.PageIndex == pageIndex))
            {
                if (MarkCanvasRect(mark) is not Rect rect) continue;
                var visual = new Rectangle
                {
                    Width = Math.Max(1, rect.Width),
                    Height = Math.Max(1, rect.Height),
                    Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                    Stroke = new SolidColorBrush(Color.FromRgb(0xE0, 0x20, 0x20)),
                    StrokeThickness = 1.5,
                    IsHitTestVisible = false,
                    Tag = mark
                };
                Canvas.SetLeft(visual, rect.X);
                Canvas.SetTop(visual, rect.Y);
                Panel.SetZIndex(visual, 5);
                canvas.Children.Add(visual);
            }
        }

        private void RepaintRedactionMarks()
        {
            if (_doc is null) return;
            int count = EnsureEngineDocumentSession().PageCount;
            for (int page = 0; page < count; page++)
            {
                var canvas = VisibleCanvasForPage(page);
                if (canvas is not null) ApplyRedactionMarks(page, canvas);
            }
        }

        private void RemoveRedactionMarkVisuals()
        {
            foreach (var canvas in AllPageCanvases())
                foreach (var old in canvas.Children.OfType<Rectangle>()
                             .Where(r => r.Tag is RedactionMark || r.Tag as string == RedactMarkTag).ToList())
                    canvas.Children.Remove(old);
        }

        internal void RedactionMouseDown(int pageIndex, Point position)
        {
            if (_activeCanvas is null) return;
            ClearSelection();
            _redactDrawing = true;
            _redactDrawStart = position;
            _redactDrawRect = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromRgb(0xE0, 0x20, 0x20)),
                StrokeThickness = 1.5,
                StrokeDashArray = [4, 2],
                Fill = new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)),
                Width = 0,
                Height = 0,
                IsHitTestVisible = false,
                Tag = RedactMarkTag
            };
            Canvas.SetLeft(_redactDrawRect, position.X);
            Canvas.SetTop(_redactDrawRect, position.Y);
            Panel.SetZIndex(_redactDrawRect, 6);
            _activeCanvas.Children.Add(_redactDrawRect);
            _activeCanvas.CaptureMouse();
        }

        internal bool RedactionMouseMove(MouseEventArgs e)
        {
            if (!_redactDrawing || _redactDrawRect is null || _gestureCanvas is null) return false;
            Point position = e.GetPosition(_gestureCanvas);
            Canvas.SetLeft(_redactDrawRect, Math.Min(position.X, _redactDrawStart.X));
            Canvas.SetTop(_redactDrawRect, Math.Min(position.Y, _redactDrawStart.Y));
            _redactDrawRect.Width = Math.Abs(position.X - _redactDrawStart.X);
            _redactDrawRect.Height = Math.Abs(position.Y - _redactDrawStart.Y);
            return true;
        }

        internal bool RedactionMouseUp(int pageIndex, MouseButtonEventArgs e)
        {
            if (!_redactDrawing) return false;
            Canvas? canvas = _gestureCanvas ?? _activeCanvas;
            Point end = canvas is null ? _redactDrawStart : e.GetPosition(canvas);
            canvas?.ReleaseMouseCapture();
            var drawn = new Rect(_redactDrawStart, end);
            CancelRedactionDrag();
            if (!ReferenceEquals(_redactSession, _active))
            {
                _redactMarks.Clear();
                _redactSession = _active;
            }

            if (drawn.Width < 4 || drawn.Height < 4)
            {
                // A click removes the mark under it.
                RedactionMark? hit = _redactMarks.LastOrDefault(mark => mark.PageIndex == pageIndex
                    && MarkCanvasRect(mark) is Rect rect && rect.Contains(end));
                if (hit is not null) _redactMarks.Remove(hit);
            }
            else if (TryPageGeometry(pageIndex, out double pw, out double ph, out double cw, out double ch, out int rot))
            {
                var (left, bottom, right, top) = RedactionGeometry.CanvasToPage(drawn, pw, ph, cw, ch, rot);
                if (right - left > 0.5 && top - bottom > 0.5)
                    _redactMarks.Add(new RedactionMark(pageIndex, left, bottom, right, top));
            }
            UpdateRedactionCount();
            ApplyRedactionMarks(pageIndex, canvas);
            return true;
        }

        private void CancelRedactionDrag()
        {
            _redactDrawing = false;
            if (_redactDrawRect is not null)
                (_redactDrawRect.Parent as Panel)?.Children.Remove(_redactDrawRect);
            _redactDrawRect = null;
        }

        private void MarkSearchResultsForRedaction()
        {
            if (!Search.HasResults)
            {
                SetStatus(Loc("Str_Redact_NoSearch"));
                return;
            }
            _redactSession = _active;
            int added = 0;
            foreach (int page in Search.PagesWithResults.ToList())
            {
                if (!Search.TryGetPageRects(page, out var rects)) continue;
                foreach (var (left, bottom, right, top) in rects)
                {
                    if (_redactMarks.Any(mark => mark.PageIndex == page && mark.Left == left
                        && mark.Bottom == bottom && mark.Right == right && mark.Top == top)) continue;
                    _redactMarks.Add(new RedactionMark(page, left, bottom, right, top));
                    added++;
                }
            }
            UpdateRedactionCount();
            RepaintRedactionMarks();
            SetStatus(string.Format(Loc("Str_Redact_MarkedSearch"), added));
        }

        private void ClearRedactionMarks()
        {
            _redactMarks.Clear();
            UpdateRedactionCount();
            RepaintRedactionMarks();
        }

        private void ApplyRedaction()
        {
            if (_doc is null || _currentFile is null) return;
            if (_redactMarks.Count == 0)
            {
                SetStatus(Loc("Str_Redact_NoMarks"));
                return;
            }
            PdfEngineDocumentSession session = EnsureEngineDocumentSession();
            List<PdfRedactionRegion> regions = [.. _redactMarks
                .Where(mark => mark.PageIndex >= 0 && mark.PageIndex < session.PageCount)
                .Select(mark => new PdfRedactionRegion(mark.PageIndex,
                    new PdfContentBounds(mark.Left, mark.Bottom, mark.Right, mark.Top)))];

            PdfContentRedactionPlan plan;
            try { plan = PdfEngineIntegration.AnalyzeRedaction(session.Document, regions); }
            catch (Exception ex)
            {
                SetStatus(string.Format(Loc("Str_Redact_Failed"), ex.Message));
                return;
            }

            // Pages whose marked content can't be removed by itself are only turned into an image
            // with the user's say-so, one page at a time.
            var approved = new List<int>();
            var skipped = new HashSet<int>();
            bool approveRest = false;
            Window? owner = Window.GetWindow(this);
            IReadOnlyList<int> rasterPages = plan.RasterPages;
            for (int index = 0; index < rasterPages.Count; index++)
            {
                int page = rasterPages[index];
                if (approveRest) { approved.Add(page); continue; }
                bool more = index < rasterPages.Count - 1;
                string[] labels = more
                    ? [Loc("Str_Redact_ConvertPage"), Loc("Str_Redact_ConvertAll"), Loc("Str_Redact_SkipPage"), Loc("Str_Btn_Cancel")]
                    : [Loc("Str_Redact_ConvertPage"), Loc("Str_Redact_SkipPage"), Loc("Str_Btn_Cancel")];
                int choice = KillerDialog.ShowChoices(owner,
                    string.Format(Loc("Str_Redact_RasterPrompt"), page + 1), labels, 0, Loc("Str_Lbl_Redact"));
                if (!more && choice >= 1) choice++;   // align the two-button layout with the four-button one
                switch (choice)
                {
                    case 0: approved.Add(page); break;
                    case 1: approved.Add(page); approveRest = true; break;
                    case 2: skipped.Add(page); break;
                    default: return;
                }
            }
            if (skipped.Count > 0) regions.RemoveAll(region => skipped.Contains(region.PageIndex));
            if (regions.Count == 0)
            {
                SetStatus(Loc("Str_Redact_NothingApplied"));
                return;
            }

            List<(int Page, PageAnnotation Annotation)> covered = CoveredAnnotations(regions);

            Exception? failure = null;
            PdfContentRedactionResult? result = null;
            HideRedactionBar();
            SetTool(EditTool.Select);
            try
            {
                SaveTempAndReload(keepAnnotations: true, preserveZoom: true, finalizeSavedFile: path =>
                {
                    try { result = PdfEngineIntegration.ApplyRedaction(path, regions, approved); }
                    catch (Exception ex) { failure = ex; }
                });
            }
            catch (Exception ex) { failure ??= ex; }

            if (failure is not null || result is null)
            {
                string reason = failure is PdfRedactionVerificationException
                    ? Loc("Str_Redact_VerifyFailed") : failure?.Message ?? string.Empty;
                SetStatus(string.Format(Loc("Str_Redact_Failed"), reason));
                return;
            }
            foreach (var (page, annotation) in covered)
                if (_annotations.TryGetValue(page, out var list)) list.Remove(annotation);
            int pages = regions.Select(region => region.PageIndex).Distinct().Count();
            string status = approved.Count == 0
                ? string.Format(Loc("Str_Redacted"), regions.Count, pages)
                : string.Format(Loc("Str_RedactedRaster"), regions.Count, pages, approved.Count);
            if (skipped.Count > 0)
                status += " " + string.Format(Loc("Str_Redact_SkippedPages"), string.Join(", ", skipped.Order().Select(page => page + 1)));
            SetStatus(status);
        }

        // Unsaved overlay annotations drawn over a redacted area would be burned back in on save,
        // so they go with the content underneath once the redaction succeeds.
        private List<(int Page, PageAnnotation Annotation)> CoveredAnnotations(IReadOnlyList<PdfRedactionRegion> regions)
        {
            var covered = new List<(int, PageAnnotation)>();
            foreach (var group in regions.GroupBy(region => region.PageIndex))
            {
                if (!_annotations.TryGetValue(group.Key, out var annotations) || annotations.Count == 0) continue;
                if (!TryPageGeometry(group.Key, out double pw, out double ph, out double cw, out double ch, out int rot))
                    continue;
                Rect[] areas = [.. group.Select(region => RedactionGeometry.PageToCanvas(region.Bounds.Left,
                    region.Bounds.Bottom, region.Bounds.Right, region.Bounds.Top, pw, ph, cw, ch, rot))];
                foreach (PageAnnotation annotation in annotations)
                    if (areas.Any(rect => RedactionGeometry.Overlaps(AnnotBounds(annotation), rect)))
                        covered.Add((group.Key, annotation));
            }
            return covered;
        }
    }
}
