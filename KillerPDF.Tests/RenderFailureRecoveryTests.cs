using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class RenderFailureRecoveryTests
    {
        // The last fallback used to sit under KillerPDF's own temp root, where a restarted session
        // treats files as imports and so reported a complete recovery as incomplete.
        [Fact]
        public void RecoveryFolderFallsBackToRootsOutsideTheSessionTemp()
        {
            Assert.Equal([@"D:\Portable\KillerPDF-Data", @"C:\Local\KillerPDF", @"C:\Temp\KillerPDF"],
                RenderFailureRecovery.FolderRoots(@"D:\Portable\KillerPDF-Data", @"C:\Local\KillerPDF", @"C:\Temp\"));
            Assert.Equal([@"C:\Local\KillerPDF", @"C:\Temp\KillerPDF"],
                RenderFailureRecovery.FolderRoots(@"C:\Local\KillerPDF", @"C:\Local\KillerPDF\", @"C:\Temp"));

            string root = Path.Combine(Path.GetTempPath(), "kp-recovery-roots-" + Path.GetRandomFileName());
            try
            {
                Directory.CreateDirectory(root);
                string blocked = Path.Combine(root, "blocked");
                File.WriteAllText(blocked, "a file where the folder should be");
                string fallback = Path.Combine(root, "fallback");
                string? folder = RenderFailureRecovery.CreateFolder([blocked, fallback]);
                Assert.NotNull(folder);
                Assert.StartsWith(Path.Combine(fallback, "Recovery") + Path.DirectorySeparatorChar, folder);
                Assert.Null(RenderFailureRecovery.CreateFolder([blocked]));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void RecoveryManifestPreservesTheLoadedRevisionBeforeAnExternalSave()
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-recovery-conflict-" + Path.GetRandomFileName());
            try
            {
                string folder = RenderFailureRecovery.CreateFolder(root);
                string original = Path.Combine(root, "original.pdf");
                File.WriteAllText(original, "%PDF-1.7 version A");
                FileRevision loaded = FileRevision.Capture(original)!.Value;
                string copy = RenderFailureRecovery.CopyPath(folder, "original.pdf", 0);
                File.Copy(original, copy);
                string manifest = Path.Combine(folder, "recovery.json");
                File.WriteAllText(manifest, JsonSerializer.Serialize(new[]
                {
                    new { OriginalPath = original, RecoveredPath = copy,
                        SuggestedName = (string?)null, LoadedRevision = loaded }
                }));
                File.WriteAllText(original, "%PDF-1.7 externally saved version B with different bytes");

                var recovered = RenderFailureRecovery.ReadManifest(manifest);
                RenderFailureRecovery.WriteManifest(folder, recovered);
                using JsonDocument saved = JsonDocument.Parse(File.ReadAllBytes(manifest));
                Assert.True(saved.RootElement[0].TryGetProperty("LoadedRevision", out JsonElement revision),
                    "Recovery discarded the revision loaded before the external save.");
                FileRevision restored = revision.Deserialize<FileRevision>();
                Assert.Equal(loaded, restored);
                Assert.True(FileRevision.ConflictsWith(restored, original));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void RecoveryManifestRetainsIncompleteDocumentDetailsBesideSuccessfulCopies()
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-recovery-partial-" + Path.GetRandomFileName());
            try
            {
                string folder = RenderFailureRecovery.CreateFolder(root);
                string complete = RenderFailureRecovery.CopyPath(folder, "complete.pdf", 0);
                string working = RenderFailureRecovery.CopyPath(folder, "incomplete.pdf", 1);
                string state = Path.ChangeExtension(working, ".json");
                File.WriteAllText(complete, "complete saved PDF bytes");
                File.WriteAllText(working, "raw working PDF bytes");
                File.WriteAllText(state, "{\"PendingText\":\"typed before failure\"}");
                string manifest = Path.Combine(folder, "recovery.json");
                File.WriteAllText(manifest, JsonSerializer.Serialize(new[]
                {
                    new { OriginalPath = "complete.pdf", RecoveredPath = complete,
                        SuggestedName = (string?)null, RecoveryError = (string?)null, StatePath = (string?)null },
                    new { OriginalPath = "incomplete.pdf", RecoveredPath = working,
                        SuggestedName = (string?)null, RecoveryError = (string?)"Form values could not be saved.", StatePath = (string?)state }
                }));

                var recovered = RenderFailureRecovery.ReadManifest(manifest);
                RenderFailureRecovery.WriteManifest(folder, recovered);
                using JsonDocument saved = JsonDocument.Parse(File.ReadAllBytes(manifest));
                Assert.Equal(2, saved.RootElement.GetArrayLength());
                Assert.True(saved.RootElement[1].TryGetProperty("RecoveryError", out JsonElement error),
                    "Recovery discarded the incomplete document's failure instead of reporting it.");
                Assert.Equal("Form values could not be saved.", error.GetString());
                Assert.Equal(state, saved.RootElement[1].GetProperty("StatePath").GetString());
                Assert.Equal("raw working PDF bytes", File.ReadAllText(working));
                Assert.Contains("typed before failure", File.ReadAllText(state));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void IncompleteRecoveryKeepsItsFailureWhenOnlyTheStateSurvived()
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-recovery-state-only-" + Path.GetRandomFileName());
            try
            {
                string folder = RenderFailureRecovery.CreateFolder(root);
                string copy = RenderFailureRecovery.CopyPath(folder, "unavailable.pdf", 0);
                string state = Path.ChangeExtension(copy, ".state.json");
                File.WriteAllText(state, "{\"PendingText\":\"available edit\"}");
                string manifest = RenderFailureRecovery.WriteManifest(folder,
                    [new RecoveredDocument("original.pdf", copy, null, RecoveryError: "Working bytes unavailable.", StatePath: state)]);

                var read = RenderFailureRecovery.ReadManifest(manifest);
                Assert.Single(read);
                Assert.Equal("Working bytes unavailable.", read[0].RecoveryError);
                Assert.Equal(state, read[0].StatePath);
                Assert.False(File.Exists(read[0].RecoveredPath));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public void RecognizesTheRenderThreadFailureWhereverItIsWrapped()
        {
            var failure = new COMException("UCEERR_RENDERTHREADFAILURE", RenderFailureRecovery.RenderThreadFailureHResult);

            Assert.True(RenderFailureRecovery.IsRenderThreadFailure(failure));
            Assert.True(RenderFailureRecovery.IsRenderThreadFailure(new InvalidOperationException("outer", failure)));
            Assert.True(RenderFailureRecovery.IsRenderThreadFailure(new AggregateException(new Exception(), failure)));
            Assert.False(RenderFailureRecovery.IsRenderThreadFailure(new COMException("other", unchecked((int)0x80004005))));
            Assert.False(RenderFailureRecovery.IsRenderThreadFailure(null));
        }

        [Fact]
        public void ManifestRoundTripsAndRefusesCopiesOutsideItsFolder()
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-recovery-" + Path.GetRandomFileName());
            try
            {
                string folder = RenderFailureRecovery.CreateFolder(root);
                string copy = RenderFailureRecovery.CopyPath(folder, @"Q3 report: final?.pdf", 0);
                File.WriteAllText(copy, "%PDF-1.7");
                string outside = Path.Combine(root, "outside.pdf");
                File.WriteAllText(outside, "%PDF-1.7");

                string manifest = RenderFailureRecovery.WriteManifest(folder,
                [
                    new RecoveredDocument(@"C:\Docs\Q3 report.pdf", copy, null),
                    new RecoveredDocument(null, outside, "Combined.pdf"),
                    new RecoveredDocument(null, Path.Combine(folder, "gone.pdf"), null)
                ]);

                var read = RenderFailureRecovery.ReadManifest(manifest);
                Assert.Single(read);
                Assert.Equal(@"C:\Docs\Q3 report.pdf", read[0].OriginalPath);
                Assert.Equal(folder, Path.GetDirectoryName(copy));
                Assert.DoesNotContain(':', Path.GetFileName(copy));
                Assert.Empty(RenderFailureRecovery.ReadManifest(Path.Combine(folder, "missing.json")));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }

        // A pane whose capture threw used to abort recovery for every tab, and a restart with only
        // clean tabs reopened the tab set from the last normal close.
        [Fact]
        public void RecoveryIsolatesEachPaneAndRemembersTabsBeforeAnyEarlyReturn()
        {
            string source = File.ReadAllText(Path.Combine(
                Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!, "Shell", "RenderRecovery.cs"));
            int start = source.IndexOf("internal string? PrepareRenderFailureRecovery()", StringComparison.Ordinal);
            Assert.True(start >= 0, "Recovery is no longer internal.");
            string body = source[start..source.IndexOf("private void WriteRecoveryState(", start, StringComparison.Ordinal)];

            Assert.Contains("try { pending = pane.CapturePendingTextForRecovery(); }", body);
            Assert.Contains("try { pane.CaptureActiveIfAny(); }", body);
            Assert.True(body.IndexOf("try { SaveWindowSettings(); }", StringComparison.Ordinal)
                < body.IndexOf("if (folder is null || recovered.Count == 0) return null;", StringComparison.Ordinal),
                "Tabs are remembered only after an early return.");
            Assert.True(body.IndexOf("RenderFailureRecovery.WriteManifest(", StringComparison.Ordinal)
                < body.IndexOf("foreach (string key in new[] { \"OpenTabs\", \"OpenTabsB\" })", StringComparison.Ordinal),
                "Recovered originals are dropped from the restore before the manifest exists.");
            Assert.Contains("RenderFailureRecovery.FolderRoots(", body);
            Assert.DoesNotContain("CreateFolder(App.TempDir)", body);
            Assert.Contains("foreach (string backup in new[] { working, state })", body);
            // With no folder anywhere, the restart must name the lost changes instead of reporting that
            // the documents reopened: a manifest in the retained session folder lists them.
            int unavailable = body.IndexOf("RenderFailureRecovery.WriteManifest(App.TempDir, lost);", StringComparison.Ordinal);
            Assert.True(unavailable >= 0 && unavailable < body.IndexOf("if (folder is null || recovered.Count == 0) return null;", StringComparison.Ordinal),
                "Unrecovered documents can still end in a silent null manifest.");
            Assert.Contains("Loc(\"Str_Dlg_RenderRecoveryNoFolder\")", body);

            // A recovered editor lands where committing it would have: turned boxes re-anchored and
            // the box kept on the page.
            string editing = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!,
                "Controls", "Viewer", "PdfViewer.TextEditing.cs"));
            Assert.Equal(3, editing.Split("BuildCommittedTextAnnotation(").Length - 1);
        }

        // With no recovery folder, the restart named an untitled document by a placeholder file that
        // was never written, asked the user to review recovered copies that did not exist, and named
        // nothing when the session folder refused the manifest too.
        [Fact]
        public void LostDocumentsAreNamedEvenWhenTheirManifestCannotBeWritten()
        {
            string root = Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!;
            string recovery = File.ReadAllText(Path.Combine(root, "Shell", "RenderRecovery.cs"));
            Assert.Contains("LostRecoveryNames = [.. unrecovered.Select(RecoveryName)];", recovery);
            Assert.Contains("LostRecoveryNames[index], session.LoadedRevision, Loc(\"Str_Dlg_RenderRecoveryNoFolder\")", recovery);
            Assert.Contains("Loc(\"Str_Outline_Untitled\")", recovery);
            Assert.Contains("CrashReporter.Capture(ex, \"Render recovery manifest\");", recovery);
            Assert.Contains("App.RenderRecoveryLostNames", recovery);
            Assert.Contains("reopened > 0 ? \"Str_Dlg_RenderRecoveryIncomplete\" : \"Str_Dlg_RenderRecoveryNotReopened\"", recovery);

            // The names travel on the restart's command line, which needs no disk.
            string app = File.ReadAllText(Path.Combine(root, "App.xaml.cs"));
            Assert.Contains("foreach (string name in window?.LostRecoveryNames ?? []) start.ArgumentList.Add(name);", app);
            Assert.Contains("RenderRecoveryLostNames = e.Args[(recoveryAt + 3)..];", app);

            // With nothing reopened, each locale keeps its message up to the folder it names.
            foreach (string file in Directory.GetFiles(Path.Combine(root, "Strings"), "*.xaml"))
            {
                string strings = File.ReadAllText(file);
                string incomplete = Value(strings, "Str_Dlg_RenderRecoveryIncomplete");
                string notReopened = Value(strings, "Str_Dlg_RenderRecoveryNotReopened");
                Assert.StartsWith(notReopened, incomplete);
                Assert.Contains("{1}", notReopened);
                Assert.True(notReopened.Length < incomplete.Length, Path.GetFileName(file));
            }
        }

        private static string Value(string strings, string key)
        {
            string open = "x:Key=\"" + key + "\">";
            int start = strings.IndexOf(open, StringComparison.Ordinal);
            Assert.True(start >= 0, key + " is missing.");
            start += open.Length;
            return strings[start..strings.IndexOf("</sys:String>", start, StringComparison.Ordinal)];
        }

        private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
    }
}
