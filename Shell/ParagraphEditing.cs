using System.IO;
using System.Security.Cryptography;
using System.Windows;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF;

public partial class MainWindow
{
    private void EditParagraph(int pageIndex, Point? canvasPoint = null)
    {
        if (_doc is null || _currentFile is null) { SetStatus(Loc("Str_Msg_OpenFirst")); return; }
        if (pageIndex < 0 || pageIndex >= _doc.PageCount || !EnsureEditable(continueAfterUnlock: false)) return;
        string? serialized = null, snapshot = null;
        try
        {
            CommitActiveTextBox();
            Func<bool> sourceCurrent = CaptureDocumentOrigin();
            string sourcePath = _currentFile;
            byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sourcePath));
            var session = EnsureEngineDocumentSession();
            PdfPageInformation page = session.Pages[pageIndex];
            int rotation = ((_pageRotations.TryGetValue(pageIndex, out int turn) ? turn : page.Rotation) % 360 + 360) % 360;
            if (rotation != 0)
            {
                KillerDialog.Show(this, ParagraphEditDialog.RefusalText(PdfParagraphRefusalCode.RotatedPage)); return;
            }
            PdfParagraphCandidateInspection inspection = PdfParagraphEditor.InspectCandidates(session.Document, pageIndex);
            if (inspection.Candidates.Count == 0)
            {
                KillerDialog.Show(this, string.Join("\n", inspection.Refusals.Select(item =>
                    ParagraphEditDialog.RefusalText(item.RefusalCode)).Distinct())); return;
            }
            // Capture the working model before opening the modal editor. Editable overlays remain
            // in their existing side state and survive the normal reload path.
            serialized = App.MakeTempFile("paragraphsource");
            snapshot = App.MakeTempFile("paragraph");
            _doc.Save(serialized);
            PdfEngineIntegration.RepairHarmlessSaveArtifacts(serialized);
            PdfEngineIntegration.CreateZeroRotationCopy(serialized, snapshot);
            PdfDocument document = PdfDocument.Open(File.ReadAllBytes(snapshot));
            Point? point = null;
            if (canvasPoint is Point position && _renderDims.TryGetValue(pageIndex, out var dimensions))
            {
                var bounds = RedactionGeometry.CanvasToPage(new Rect(position, new Size(0, 0)),
                    page.Width, page.Height, dimensions.w, dimensions.h, rotation);
                point = new Point(bounds.Left, bounds.Bottom);
            }
            var dialog = new ParagraphEditDialog(this, document, pageIndex, _appScale, point);
            dialog.ShowDialog();
            if (dialog.Result is not { } plan) return;
            if (!sourceCurrent() || !File.Exists(sourcePath)
                || !SHA256.HashData(File.ReadAllBytes(sourcePath)).AsSpan().SequenceEqual(sourceHash))
            {
                SetStatus(Loc("Str_Paragraph_Changed")); return;
            }
            byte[] edited = PdfParagraphEditor.Apply(document, plan);
            SaveTempAndReload(keepAnnotations: true, preserveZoom: true,
                finalizeSavedFile: path => AtomicFileOutput.Write(path, edited), selectedPageAfterReload: pageIndex);
            SetStatus(Loc("Str_St_TextEditedInPage"));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            KillerDialog.Show(this, Loc("Str_Paragraph_Unsupported") + "\n" + error.Message,
                "KillerPDF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            foreach (string? file in new[] { serialized, snapshot })
                if (file is not null)
                    try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
