using System;
using System.IO;
using System.Security.Cryptography;

namespace KillerPDF.Services
{
    /// <summary>
    /// Identifies the bytes of the file a document was loaded from or last saved to, so a
    /// save can tell when something else (another tab, another pane, another program) replaced it.
    /// </summary>
    internal readonly record struct FileRevision(long Length, DateTime LastWriteUtc, string? Sha256 = null)
    {
        internal static FileRevision? Capture(string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            FileRevision? metadata = null;
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                metadata = new FileRevision(info.Length, info.LastWriteTimeUtc);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                string hash = Convert.ToHexString(SHA256.HashData(stream));
                return new FileRevision(stream.Length, File.GetLastWriteTimeUtc(path), hash);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                or NotSupportedException)
            {
                return metadata;
            }
        }

        /// <summary>
        /// True when saving to <paramref name="path"/> would replace a version this document never
        /// saw. A missing destination is not a conflict; an unknown baseline over an existing file is.
        /// </summary>
        internal static bool ConflictsWith(FileRevision? baseline, string path)
        {
            FileRevision? current = Capture(path);
            return current is not null
                ? current.Value.Sha256 is null || current != baseline
                : File.Exists(path);
        }
    }
}
