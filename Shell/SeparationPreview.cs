using System.Windows;
using KillerPDF.Services;

namespace KillerPDF
{
    // Separations in the Document tools (#264): the page plate by plate, with the earlier text
    // report one click away.
    public partial class MainWindow
    {
        private void ShowSeparationPreview()
        {
            if (_doc is null || string.IsNullOrWhiteSpace(_currentFile))
            {
                KillerDialog.Show(this, Loc("Str_Msg_OpenFirst"));
                return;
            }
            CommitActiveTextBox();
            string file = _currentFile;
            void Report() => ShowDocumentReport(Loc("Str_DocumentTools_Separations"), path =>
            {
                var production = PdfEngineIntegration.InspectPrintProduction(path);
                return PdfEngineIntegration.CreateSeparationPreview(path,
                    production.Colorants.Select(colorant => colorant.Name)).ToText();
            });
            try
            {
                var production = PdfEngineIntegration.InspectPrintProduction(file);
                var selection = PdfEngineIntegration.CreateSeparationPreview(file,
                    production.Colorants.Select(colorant => colorant.Name));
                SeparationPreviewDialog.Plate[] plates = [.. selection.Plates.Select(plate =>
                    new SeparationPreviewDialog.Plate(plate.Name, plate.PageIndexes.ToHashSet()))];
                new SeparationPreviewDialog(this, file, _doc.PageCount, _currentPage, plates, Report).ShowDialog();
            }
            catch (Exception ex)
            {
                KillerDialog.Show(this, string.Format(Loc("Str_Sep_Failed"), ex.Message), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
