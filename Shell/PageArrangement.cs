using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF
{
    // One-click page arrangement (#436): reverse the page order, turn every page upright, and
    // straighten tilted scans. Each acts on the selected pages when two or more are selected,
    // otherwise on the whole document, and each is a single undo step.
    public partial class MainWindow
    {
        private int[] PagesForDocumentCommand()
        {
            int[] selected = [.. PageList.SelectedItems.Cast<PageThumbnailVm>()
                .Select(page => page.PageIndex).Distinct().OrderBy(index => index)];
            return selected.Length >= 2 ? selected : [.. Enumerable.Range(0, PageList.Items.Count)];
        }

        // ---- Reverse Page Order ----------------------------------------------------------------

        private void ReversePageOrder()
        {
            if (_doc is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            CommitActiveTextBox();
            int[] pages = PagesForDocumentCommand();
            if (pages.Length < 2) return;
            try
            {
                IReadOnlyList<int> order = PdfEngineIntegration.PageOrderAfterReverse(PageList.Items.Count, pages);
                int selected = Math.Max(0, PageList.SelectedIndex);
                ReloadWithRemappedAnnotations(
                    annotations => PageAnnotationInsertion.Reorder(annotations, order),
                    documentUndo => SaveTempAndReload(
                        keepAnnotations: true,
                        finalizeSavedFile: path => PdfEngineIntegration.ReorderPages(path, order),
                        remapRotations: rotations => PdfEngineIntegration.RemapRotationsAfterReorder(rotations, order),
                        selectedPageAfterReload: Math.Max(0, order.ToList().IndexOf(selected)),
                        documentUndo: documentUndo));
                SetStatus(string.Format(Loc("Str_Pages_Reversed"), pages.Length));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Pages_Failed"), ex.Message), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---- Fix Page Orientation ------------------------------------------------------------

        // Pages with text are judged by the direction their text runs. Scans without a text layer
        // are read with OCR in the two directions their lines allow, and the reading that makes
        // sense wins. The fix is a page rotation, so nothing on the page is redrawn.
        private async void FixPageOrientation()
        {
            if (_doc is null || _currentFile is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            CommitActiveTextBox();
            int[] pages = PagesForDocumentCommand();
            string label = Loc("Str_Pages_FixOrientation");
            var turns = new Dictionary<int, int>();
            int unread = 0;
            Border? busy = null;
            try
            {
                var ct = BeginCancellableOp(label);
                busy = ShowBusyOverlay(label);
                await Dispatcher.Yield(DispatcherPriority.Background);

                string file = _currentFile;
                Dictionary<int, int?> fromText = await Task.Run(() => UprightFromText(file, pages, ct), ct);
                var scans = new List<int>();
                foreach (int page in pages)
                {
                    if (!fromText.TryGetValue(page, out int? upright)) { scans.Add(page); continue; }
                    if (upright is not int target) { unread++; continue; }
                    int turn = PageGeometry.TurnBetween(CurrentPageRotation(page), target);
                    if (turn != 0) turns[page] = turn;
                }

                if (scans.Count > 0)
                {
                    // The model download prompt must not sit under the busy overlay.
                    HideBusyOverlay(busy);
                    busy = null;
                    if (!await EnsureOcrModelsReadyAsync())
                        unread += scans.Count;
                    else
                    {
                        busy = ShowBusyOverlay(label);
                        using var ocr = new OcrService(language: CurrentOcrLanguageString());
                        for (int i = 0; i < scans.Count; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            SetBusyMessage(busy, $"{label}: {i + 1}/{scans.Count}");
                            await Dispatcher.Yield(DispatcherPriority.Background);
                            var bitmap = RenderPageBitmap(scans[i], 1600);
                            if (bitmap is null) { unread++; continue; }
                            int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
                            var bgra = new byte[width * height * 4];
                            bitmap.CopyPixels(bgra, width * 4, 0);
                            int? turn = await Task.Run(() => ScanUprightTurn(ocr, bgra, width, height, ct), ct);
                            if (turn is null) unread++;
                            else if (turn != 0) turns[scans[i]] = turn.Value;
                        }
                    }
                }

                string skipped = unread > 0 ? " " + string.Format(Loc("Str_Pages_Unread"), unread) : "";
                if (turns.Count == 0)
                {
                    SetStatus(Loc("Str_Pages_OrientNone") + skipped);
                    return;
                }

                UndoEntry? documentUndo = CaptureDocumentUndo();
                foreach (var (page, turn) in turns)
                    if (_annotations.TryGetValue(page, out var annotations) && _renderDims.TryGetValue(page, out var dims))
                        AnnotationRotate.Remap(annotations, turn, dims.w, dims.h);
                int restore = PageList.SelectedIndex;
                SaveTempAndReload(
                    keepAnnotations: true,
                    remapRotations: rotations =>
                    {
                        foreach (var group in turns.GroupBy(pair => pair.Value))
                            PdfEngineIntegration.RemapRotationsAfterPageTurns(
                                rotations, [.. group.Select(pair => pair.Key)], group.Key);
                    },
                    documentUndo: documentUndo);
                PageList.SelectedIndex = Math.Min(restore, PageList.Items.Count - 1);
                FitToPage();
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() => FitToPage()));
                SetStatus(string.Format(Loc("Str_Pages_Oriented"), turns.Count) + skipped);
            }
            catch (OperationCanceledException)
            {
                SetStatus(Loc("Str_St_Canceled"));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Pages_Failed"), ex.Message), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (busy is not null) HideBusyOverlay(busy);
                EndCancellableOp();
            }
        }

        private int CurrentPageRotation(int page) => _pageRotations.TryGetValue(page, out int rotation) ? rotation : 0;

        // The rotation that shows each page's text upright, for every page that has text. A page
        // whose text runs several ways maps to null; a page without text is left out.
        private static Dictionary<int, int?> UprightFromText(string file, int[] pages, CancellationToken ct)
        {
            var result = new Dictionary<int, int?>();
            using var content = PdfContentDocument.Open(file);
            foreach (int page in pages)
            {
                ct.ThrowIfCancellationRequested();
                if (page >= content.NumberOfPages) continue;
                List<(double, double, int)> baselines;
                try
                {
                    baselines = [.. content.GetPage(page + 1).GetWords()
                        .Where(word => word.Letters.Count > 0)
                        .Select(word =>
                        {
                            var first = word.Letters[0];
                            var last = word.Letters[^1];
                            return (last.EndBaseLine.X - first.StartBaseLine.X,
                                last.EndBaseLine.Y - first.StartBaseLine.Y, word.Letters.Count);
                        })];
                }
                catch (Exception error) when (error is FormatException or NotSupportedException or InvalidOperationException)
                {
                    continue;
                }
                if (baselines.Count > 0) result[page] = PageGeometry.UprightRotation(baselines);
            }
            return result;
        }

        // How far a scan must turn clockwise to read upright, or null when no reading is clearly
        // better. The ink's line direction picks the two candidates, so a page costs two OCR
        // passes at most, and one when it already reads cleanly as it is.
        private static int? ScanUprightTurn(OcrService ocr, byte[] bgra, int width, int height, CancellationToken ct)
        {
            byte[] gray = PageGeometry.DownscaleGray(bgra, width, height, 800, out int grayWidth, out int grayHeight);
            double ratio = PageGeometry.LineAxisRatio(gray, grayWidth, grayHeight);
            int[] candidates = ratio >= 1.25 ? [0, 180] : ratio <= 0.8 ? [90, 270] : [0, 180, 90, 270];
            int best = -1;
            double bestScore = 0, runnerUp = 0;
            foreach (int turn in candidates)
            {
                ct.ThrowIfCancellationRequested();
                PdfOcrResult result;
                if (turn == 0)
                    result = ocr.RecognizeBgra(bgra, width, height, cancellationToken: ct);
                else
                {
                    PdfOcrBgraImage turned = PdfOcrImagePreprocessor.RotateBgra(bgra, width, height, turn, ct);
                    result = ocr.RecognizeBgra(turned.Pixels, turned.Width, turned.Height, cancellationToken: ct);
                }
                double score = ReadingScore(result);
                if (score > bestScore) { runnerUp = bestScore; bestScore = score; best = turn; }
                else runnerUp = Math.Max(runnerUp, score);
                if (turn == 0 && score >= 0.85) return 0;
            }
            return best >= 0 && bestScore - runnerUp >= 0.08 ? best : null;
        }

        // Mean word confidence, scaled down for pages with only a few words so a stray reading of
        // noise cannot outvote real text.
        private static double ReadingScore(PdfOcrResult result)
        {
            var words = result.Words.Where(word => word.Text.Any(char.IsLetterOrDigit)).ToList();
            if (words.Count < 3) return 0;
            return words.Average(word => word.Confidence) * Math.Min(1.0, words.Count / 20.0);
        }

        // ---- Straighten Scanned Pages --------------------------------------------------------

        // Measures each scan's tilt from its lines of text and turns the page image back level,
        // keeping the page size. Only scanned pages without a text layer are changed: redrawing
        // a scan drops an OCR layer, so scans should be straightened before OCR.
        private async void StraightenScannedPages()
        {
            if (_doc is null || _currentFile is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
            if (!EnsureEditable(continueAfterUnlock: false)) return;
            CommitActiveTextBox();
            int[] pages = PagesForDocumentCommand();
            string label = Loc("Str_Pages_Straighten");
            Border? busy = null;
            try
            {
                var ct = BeginCancellableOp(label);
                busy = ShowBusyOverlay(label);
                await Dispatcher.Yield(DispatcherPriority.Background);

                string file = _currentFile;
                var (scans, withText) = await Task.Run(() => ScansWithoutText(file, pages, ct), ct);
                string skipped = withText > 0 ? " " + string.Format(Loc("Str_Pages_TextLayerSkipped"), withText) : "";

                var tilts = new Dictionary<int, double>();
                for (int i = 0; i < scans.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    SetBusyMessage(busy, $"{label}: {i + 1}/{scans.Count}");
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    var preview = RenderPageBitmap(scans[i], 1600);
                    if (preview is null) continue;
                    int width = preview.PixelWidth, height = preview.PixelHeight;
                    var bgra = new byte[width * height * 4];
                    preview.CopyPixels(bgra, width * 4, 0);
                    double tilt = await Task.Run(() =>
                    {
                        byte[] gray = PageGeometry.DownscaleGray(bgra, width, height, 1600, out int gw, out int gh);
                        return PageGeometry.EstimateSkewDegrees(gray, gw, gh);
                    }, ct);
                    if (Math.Abs(tilt) >= 0.15) tilts[scans[i]] = tilt;
                }
                if (tilts.Count == 0)
                {
                    SetStatus(Loc("Str_Pages_StraightenNone") + skipped);
                    return;
                }

                UndoEntry? documentUndo = CaptureDocumentUndo();
                PdfEngineDocumentSession engineSession = EnsureEngineDocumentSession();
                var replacements = new Dictionary<int, string>();
                var bakedAnnotationPages = new List<int>();
                int done = 0;
                foreach (var (page, tilt) in tilts.OrderBy(pair => pair.Key))
                {
                    ct.ThrowIfCancellationRequested();
                    SetBusyMessage(busy, $"{label}: {++done}/{tilts.Count}");
                    await Dispatcher.Yield(DispatcherPriority.Background);

                    // Annotations drawn on the page turn with it, the way the Transform tool
                    // bakes them, so they stay where they were placed on the scan.
                    string? burned = BurnPageAnnotationsToTemp(page);
                    if (burned is not null) bakedAnnotationPages.Add(page);
                    var (pageWidth, pageHeight) = engineSession.VisualPageSize(page, _pageRotations);
                    int budget = Math.Clamp((int)Math.Ceiling(Math.Max(pageWidth, pageHeight) * 300 / 72.0), 1000, 6000);
                    var source = RenderPageBitmap(page, budget, burned)
                        ?? throw new InvalidOperationException(Loc("Str_Tf_NoRender"));
                    var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
                    source.CopyPixels(pixels, source.PixelWidth * 4, 0);
                    ScanTone tone = PageGeometry.ClassifyTone(pixels);

                    BitmapSource level = RotateWithinPage(source, -tilt);
                    if (tone == ScanTone.BlackAndWhite)
                        level = PageQualityConverter.ApplyColorMode(level, PageColorMode.BlackAndWhite, 160);
                    var levelPixels = new byte[level.PixelWidth * level.PixelHeight * 4];
                    level.CopyPixels(levelPixels, level.PixelWidth * 4, 0);
                    ReadOnlyMemory<byte> jpeg = tone == ScanTone.BlackAndWhite
                        ? default : EncodeJpeg(level, 90, tone == ScanTone.Grayscale);

                    string tmp = App.MakeTempFile("straighten");
                    File.WriteAllBytes(tmp, PdfEngineIntegration.CreateRasterDocument([
                        new PdfEngineIntegration.RasterPage(level.PixelWidth, level.PixelHeight,
                            pageWidth, pageHeight, levelPixels, jpeg,
                            Bitonal: tone == ScanTone.BlackAndWhite,
                            Grayscale: tone == ScanTone.Grayscale)]));
                    replacements[page] = tmp;
                }

                foreach (int page in bakedAnnotationPages)
                    if (_annotations.TryGetValue(page, out var pageAnnotations)) pageAnnotations.Clear();
                int[] changed = [.. replacements.Keys.OrderBy(index => index)];
                int restore = Math.Max(0, PageList.SelectedIndex);
                SaveTempAndReload(
                    keepAnnotations: true,
                    finalizeSavedFile: path =>
                    {
                        if (changed.Length == engineSession.PageCount)
                            PdfEngineIntegration.ReplaceAllPagesAndCompact(path,
                                [.. changed.Select(page => replacements[page])]);
                        else
                            PdfEngineIntegration.ReplacePagesAndCompact(path, replacements);
                    },
                    remapRotations: rotations =>
                        PdfEngineIntegration.RemapRotationsAfterPageReplacements(rotations, changed),
                    selectedPageAfterReload: restore,
                    documentUndo: documentUndo);
                SetStatus(string.Format(Loc("Str_Pages_Straightened"), changed.Length) + skipped);
            }
            catch (OperationCanceledException)
            {
                SetStatus(Loc("Str_St_Canceled"));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Pages_Failed"), ex.Message), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (busy is not null) HideBusyOverlay(busy);
                EndCancellableOp();
            }
        }

        // Scanned pages among the targets that have no text layer, and how many scans do have one.
        private static (List<int> Scans, int WithText) ScansWithoutText(string file, int[] pages, CancellationToken ct)
        {
            byte[] bytes = File.ReadAllBytes(file);
            IReadOnlyList<bool> scanned = PdfScanTextAnalyzer.ReadScannedPages(
                KillerPdf.Engine.Documents.PdfDocument.Open(bytes, string.Empty), ct);
            using var content = PdfContentDocument.Open(bytes);
            var scans = new List<int>();
            int withText = 0;
            foreach (int page in pages)
            {
                ct.ThrowIfCancellationRequested();
                if (page >= scanned.Count || !scanned[page]) continue;
                bool hasText;
                try { hasText = content.GetPage(page + 1).GetWords().Any(); }
                catch (Exception error) when (error is FormatException or NotSupportedException or InvalidOperationException)
                {
                    hasText = false;
                }
                if (hasText) withText++;
                else scans.Add(page);
            }
            return (scans, withText);
        }

        // Turns the image about its center inside the same canvas, on white, so the page keeps
        // its size and only the corners the turn uncovers are filled.
        private static BitmapSource RotateWithinPage(BitmapSource source, double degrees)
        {
            int width = source.PixelWidth, height = source.PixelHeight;
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                dc.PushTransform(new RotateTransform(degrees, width / 2.0, height / 2.0));
                dc.DrawImage(source, new Rect(0, 0, width, height));
                dc.Pop();
            }
            var result = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            result.Render(visual);
            result.Freeze();
            return result;
        }
    }
}
