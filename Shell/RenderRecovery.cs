using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using KillerPDF.Services;

namespace KillerPDF
{
    // #456: when WPF's render thread fails the window goes black for good. App saves every
    // unsaved tab aside through here, restarts, and the new window reopens them unsaved.
    public partial class MainWindow
    {
        /// <summary>
        /// Writes each unsaved document, as it is on screen, to a recovery folder and remembers the
        /// open tabs. Returns the recovery manifest, or null when nothing needed saving.
        /// Nothing here draws, so it still works after the render thread has failed.
        /// </summary>
        internal string? PrepareRenderFailureRecovery()
        {
            var recovered = new List<RecoveredDocument>();
            string? folder = null;
            Controls.PdfViewer activeViewer = ActiveViewer;
            foreach (Controls.PdfViewer pane in new[] { Viewer, ViewerB })
            {
                try { pane.CaptureActiveIfAny(); } catch (Exception ex) when (ex is not OutOfMemoryException) { }
                foreach (var session in pane.SessionsRef.Where(s => s.IsDirty && s.Doc is not null).ToList())
                {
                    try
                    {
                        folder ??= RenderFailureRecovery.CreateFolder(AppDataPaths.UserRoot);
                        string name = session.SuggestedName ?? Path.GetFileName(session.OriginalFile ?? "document.pdf");
                        string copy = RenderFailureRecovery.CopyPath(folder, name, recovered.Count);
                        ActiveViewer = pane;
                        pane.WithSessionLive(session, () =>
                        {
                            string snapshot = WriteSavedCopyToTemp();
                            File.Copy(snapshot, copy, overwrite: false);
                        });
                        recovered.Add(new RecoveredDocument(session.OriginalFile, copy, session.SuggestedName));
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // One document that cannot be written must not stop the others.
                    }
                }
            }
            ActiveViewer = activeViewer;

            // Clean tabs reopen through the normal session restore; recovered ones come back from the
            // manifest instead, so their originals must not also reopen beside them.
            SaveWindowSettings();
            var originals = new HashSet<string>(recovered.Select(d => d.OriginalPath ?? string.Empty), StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[] { "OpenTabs", "OpenTabsB" })
                if (App.GetSetting(key) is { Length: > 0 } saved)
                {
                    string[] keep = [.. saved.Split('|').Where(path => !originals.Contains(path))];
                    if (keep.Length > 0) App.SetSetting(key, string.Join("|", keep)); else App.RemoveSetting(key);
                }
            return folder is null || recovered.Count == 0 ? null : RenderFailureRecovery.WriteManifest(folder, recovered);
        }

        /// <summary>
        /// Lab check for the recovery path, which a real failure (sleep, driver reset) cannot be
        /// summoned for: with KILLERPDF_SIMULATE_RENDER_FAILURE=1 the window raises the genuine
        /// render-thread error five seconds after it opens. Never set in normal use.
        /// </summary>
        private void ArmSimulatedRenderFailure()
        {
            if (App.RestartedAfterRenderFailure
                || Environment.GetEnvironmentVariable("KILLERPDF_SIMULATE_RENDER_FAILURE") != "1") return;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                throw new System.Runtime.InteropServices.COMException("UCEERR_RENDERTHREADFAILURE (simulated)",
                    RenderFailureRecovery.RenderThreadFailureHResult);
            };
            timer.Start();
        }

        /// <summary>Reopens documents recovered after a render failure as unsaved changes to their files.</summary>
        private void OpenRenderRecovery()
        {
            ArmSimulatedRenderFailure();
            if (!App.RestartedAfterRenderFailure) return;
            App.RestartedAfterRenderFailure = false;
            string? manifest = App.RenderRecoveryManifest;
            IReadOnlyList<RecoveredDocument> documents = manifest is null ? [] : RenderFailureRecovery.ReadManifest(manifest);
            foreach (RecoveredDocument document in documents)
            {
                try
                {
                    ActiveViewer.OpenInNewTabExt(document.RecoveredPath);
                    if (_doc is null) continue;
                    // The copy holds the edits; saving goes back to the file they were made to.
                    bool hasHome = document.OriginalPath is { Length: > 0 } original
                        && Directory.Exists(Path.GetDirectoryName(original));
                    _originalFile = hasHome ? document.OriginalPath : null;
                    _suggestedName = hasHome ? null : document.SuggestedName ?? Path.GetFileName(document.RecoveredPath);
                    _loadedRevision = hasHome ? FileRevision.Capture(document.OriginalPath) : null;
                    _publishedFile = null;
                    FileNameLabel.Text = Path.GetFileName(document.OriginalPath ?? _suggestedName ?? document.RecoveredPath);
                    MarkDirty(true);
                    ActiveViewer.CaptureActiveIfAny();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            }
            RebuildTabStrip();
            KillerDialog.Show(this, documents.Count > 0
                    ? string.Format(Loc("Str_Dlg_RenderRecovered"), Path.GetDirectoryName(manifest))
                    : Loc("Str_Dlg_RenderRestarted"),
                Loc("Str_Dlg_AppTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
