using System;
using System.IO;

namespace KillerPDF.Services
{
    /// <summary>
    /// A cheap identity for the version of a file a document was loaded from or last saved to, so a
    /// save can tell when something else (another tab, another pane, another program) replaced it.
    /// </summary>
    internal readonly record struct FileRevision(long Length, DateTime LastWriteUtc)
    {
        internal static FileRevision? Capture(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? new FileRevision(info.Length, info.LastWriteTimeUtc) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException)
            {
                return null;
            }
        }

        /// <summary>
        /// True when saving to <paramref name="path"/> would replace a version this document never
        /// saw. A missing destination is not a conflict; an unknown baseline over an existing file is.
        /// </summary>
        internal static bool ConflictsWith(FileRevision? baseline, string path)
        {
            FileRevision? current = Capture(path);
            return current is not null && current != baseline;
        }
    }
}
