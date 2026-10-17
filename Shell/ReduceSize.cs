using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Writing;
using KillerPDF.Services;

namespace KillerPDF
{
    // Reduce File Size (#433): saves a smaller copy and leaves the open document as it is.
    public partial class MainWindow
    {
        private async void ReduceFileSize_Click(object sender, RoutedEventArgs e)
        {
            if (_doc is null || _currentFile is null) { KillerDialog.Show(this, Loc("Str_Msg_OpenFirst")); return; }
            if (!EnsureEditable()) return;
            CommitActiveTextBox();
            var options = new ReduceSizeDialog(this);
            options.ShowDialog();
            if (!options.Confirmed) return;

            string seed = _originalFile ?? _currentFile;
            var dlg = new Controls.FileDialog(Controls.FileDialogMode.Save)
            {
                Filter = Loc("Str_Filter_Pdf") + "|*.pdf",
                Title = Loc("Str_Reduce_Title"),
                CheckFileExists = false,
                CheckPathExists = true,
                RequireFilterExtension = true,
                FileName = System.IO.Path.GetFileNameWithoutExtension(seed) + "-reduced.pdf"
            };
            if (dlg.ShowDialog(this) != true) return;
            if (!ConfirmSignatureRemoval(dlg.FileName)) return;
            CloseEngineDocumentSession();
            OfferRescaleOutOfRangePages();

            Border? busy = null;
            bool started = false;
            try
            {
                string source = WriteSavedCopyToTemp();
                var ct = BeginCancellableOp(Loc("Str_Reduce_Title"));
                started = true;
                busy = ShowBusyOverlay(Loc("Str_Reduce_Working"));
                PdfImageDownsampleOptions? images = options.Downsample
                    ? new PdfImageDownsampleOptions
                    {
                        TargetDpi = options.Dpi,
                        ThresholdDpi = options.Dpi * 1.5,
                        JpegQuality = options.Quality,
                        JpegEncoder = PdfImageJpeg.Encode
                    }
                    : null;
                string output = dlg.FileName;
                var (before, after, resampled) = await Task.Run(
                    () => PdfEngineIntegration.ReduceFileSize(source, output, images, ct), ct);
                SetStatus(string.Format(Loc("Str_Reduce_Done"), System.IO.Path.GetFileName(output),
                    Megabytes(before), Megabytes(after), resampled));
            }
            catch (OperationCanceledException)
            {
                SetStatus(Loc("Str_St_Canceled"));
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, Loc("Str_Reduce_Failed") + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                if (busy is not null) HideBusyOverlay(busy);
                if (started) EndCancellableOp();
            }
        }

        private static string Megabytes(long bytes) =>
            (bytes / 1048576.0).ToString(bytes < 10 * 1048576 ? "0.00" : "0.0",
                System.Globalization.CultureInfo.CurrentCulture) + " MB";

        // The bytes Save As would write, in a temp file, without touching the open document's
        // state: annotations and stamps burned in, form values and page rotations applied.
        private string WriteSavedCopyToTemp()
        {
            Dictionary<int, int> rotations = SnapshotPageRotations();
            string clean = App.MakeTempFile("reduceclean");
            _doc!.Save(clean);
            PdfEngineIntegration.RepairHarmlessSaveArtifacts(clean);
            PdfEngineIntegration.StripLinkAppearances(clean);
            string copy = App.MakeTempFile("reduce");
            System.IO.File.Copy(clean, copy, true);
            PdfEngineIntegration.ClearInvalidatedSignatures(copy);
            if (_annotations.Values.Any(list => list.Count > 0) || HasActiveStamps)
            {
                PdfEngineBurn.Burn(copy, _annotations, _renderDims, _docStampSpec, null, _pageRotations);
                PdfEngineIntegration.StripLinkAppearances(copy);
            }
            WriteFormValuesToDocument(copy);
            WriteRedactionMarksToDocument(copy);
            PdfEngineIntegration.ApplyPageRotations(copy, rotations);
            return copy;
        }
    }
}
