using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace KillerPDF.Services
{
    /// <summary>One document saved aside when the window could no longer draw.</summary>
    internal sealed record RecoveredDocument(string? OriginalPath, string RecoveredPath, string? SuggestedName);

    /// <summary>
    /// WPF's render thread can fail for good (UCEERR_RENDERTHREADFAILURE, often after sleep, a
    /// display change or a graphics driver reset). The window then stays black and cannot recover,
    /// so KillerPDF saves unsaved documents aside and restarts instead of offering Continue (#456).
    /// </summary>
    internal static class RenderFailureRecovery
    {
        internal const int RenderThreadFailureHResult = unchecked((int)0x88980406);
        internal const string RecoverySwitch = "--render-recovery";

        internal static bool IsRenderThreadFailure(Exception? exception)
        {
            for (Exception? e = exception; e is not null; e = e.InnerException)
            {
                if (e is COMException { HResult: RenderThreadFailureHResult }) return true;
                if (e is AggregateException aggregate)
                    foreach (Exception inner in aggregate.InnerExceptions)
                        if (IsRenderThreadFailure(inner)) return true;
            }
            return false;
        }

        /// <summary>A fresh, dated folder under the user's data root for this recovery.</summary>
        internal static string CreateFolder(string root)
        {
            string folder = Path.Combine(root, "Recovery", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>A file name for a recovered copy that cannot collide or escape the folder.</summary>
        internal static string CopyPath(string folder, string? name, int index)
        {
            string stem = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(name) ? "document" : name);
            foreach (char invalid in Path.GetInvalidFileNameChars()) stem = stem.Replace(invalid, '_');
            if (stem.Length == 0) stem = "document";
            return Path.Combine(folder, $"{index + 1:00}-{stem}.pdf");
        }

        internal static string WriteManifest(string folder, IReadOnlyList<RecoveredDocument> documents)
        {
            string path = Path.Combine(folder, "recovery.json");
            AtomicFileOutput.Write(path, JsonSerializer.SerializeToUtf8Bytes(documents));
            return path;
        }

        /// <summary>The recovered documents, keeping only copies that are still inside the recovery folder.</summary>
        internal static IReadOnlyList<RecoveredDocument> ReadManifest(string path)
        {
            try
            {
                string folder = Path.GetFullPath(Path.GetDirectoryName(path)!) + Path.DirectorySeparatorChar;
                var documents = JsonSerializer.Deserialize<List<RecoveredDocument>>(File.ReadAllBytes(path)) ?? [];
                return documents.FindAll(d => !string.IsNullOrEmpty(d.RecoveredPath)
                    && Path.GetFullPath(d.RecoveredPath).StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(d.RecoveredPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                or ArgumentException or NotSupportedException)
            {
                return [];
            }
        }
    }
}
