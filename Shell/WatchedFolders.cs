using System.IO;
using System.Windows;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private void OpenWatchedFolders(Window owner)
    {
        try
        {
            PdfMacro[] macros = [.. new PdfMacroStore().LoadAll().Select(macro => new PdfMacro(macro.Name,
                macro.Steps.Select(step => step.Operation != PdfMacroOperation.Ocr ? step
                    : new PdfMacroStep(step.Operation, new Dictionary<string, string>(step.Settings ?? new Dictionary<string, string>())
                    { ["language"] = step.Settings?.GetValueOrDefault("language") ?? CurrentOcrLanguageString(),
                        ["formAware"] = step.Settings?.GetValueOrDefault("formAware") ?? (OcrFormAware ? "true" : "false") }))))];
            new WatchedFolderDialog(owner, App.GetFolderAutomation(), macros, _appScale).ShowDialog();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.Text.Json.JsonException)
        {
            KillerDialog.Show(owner, WatchedFolderDialog.ErrorText(error.Message), Loc("Str_Watch_Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
