using System.IO;
using System.Windows;
using System.Windows.Interop;
using KillerPDF.Services;

namespace KillerPDF
{
    // Scan to PDF (#73): scan through TWAIN and open the pages as a new, unsaved document,
    // the same way Import opens images.
    public partial class MainWindow
    {
        private const string ScanSourceSetting = "ScanSource";
        private const string ScanSettingsWindowSetting = "ScanShowSettings";

        private async void ScanToPdf()
        {
            if (!TwainScan.ManagerInstalled)
            {
                KillerDialog.Show(this, Loc("Str_Scan_NoManager"), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            IntPtr window = new WindowInteropHelper(this).Handle;
            string folder = Path.Combine(Path.GetTempPath(), $"KillerPDF-scan-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
            IReadOnlyList<string> pages;
            try
            {
                using TwainScan scan = TwainScan.Open(window);
                IReadOnlyList<NTwain.DataSource> sources = scan.Sources;
                if (sources.Count == 0)
                {
                    KillerDialog.Show(this, Loc("Str_Scan_NoScanners"), "KillerPDF",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                var dialog = new ScanDialog(this, [.. sources.Select(source => source.Name)],
                    App.GetSetting(ScanSourceSetting), App.GetSetting(ScanSettingsWindowSetting) != "0");
                dialog.ShowDialog();
                if (!dialog.Confirmed || dialog.SelectedIndex < 0) return;
                NTwain.DataSource chosen = sources[dialog.SelectedIndex];
                App.SetSetting(ScanSourceSetting, chosen.Name);
                App.SetSetting(ScanSettingsWindowSetting, dialog.ShowSettings ? "1" : "0");
                SetStatus(Loc("Str_Scan_Scanning"));
                pages = await scan.ScanAsync(chosen, dialog.ShowSettings, folder);
            }
            catch (Exception ex)
            {
                SetStatus("");
                KillerDialog.Show(this, Loc("Str_Scan_Failed") + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                DeleteScanFolder(folder);
                return;
            }

            if (pages.Count == 0)
            {
                SetStatus(Loc("Str_Scan_NoPages"));
                DeleteScanFolder(folder);
                return;
            }

            var target = BeginTabLoad(out var prev, out bool createdNew);
            try
            {
                string tempPath = Path.Combine(Path.GetTempPath(), $"Scanned-{DateTime.Now:yyyyMMdd-HHmmss-fff}.pdf");
                File.WriteAllBytes(tempPath, PdfEngineIntegration.MergeFiles(pages));
                _doc = PdfWorkingDocument.Open(tempPath);
                FinishOpenFile("Scanned.pdf", tempPath);
                _originalFile = null;   // no saved location yet, so Save becomes Save As
                MarkDirty(true);
                SetStatus(string.Format(Loc("Str_Scan_Done"), pages.Count));
                CaptureSessionState(_active!);
                SetTool(_currentTool);
                RebuildTabStrip();
            }
            catch (Exception ex)
            {
                AbortTabLoad(target, prev, createdNew);
                KillerDialog.Show(this, Loc("Str_Scan_Failed") + "\n" + ex.Message, "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                DeleteScanFolder(folder);
            }
        }

        private static void DeleteScanFolder(string folder)
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
