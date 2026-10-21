using System;
using System.IO;
using System.Security.Cryptography;

namespace KillerPDF.Services
{
    /// <summary>
    /// Identifies the exact bytes a save published, so later work (signing) can use what was saved
    /// rather than the editable working copy, and can tell when the file has changed since.
    /// </summary>
    internal sealed record PublishedFileStamp(string Path, string Sha256)
    {
        internal static PublishedFileStamp Capture(string path) => new(path, Hash(path));

        /// <summary>True when the file still holds exactly the bytes that were published.</summary>
        internal bool Matches()
        {
            try { return File.Exists(Path) && Hash(Path) == Sha256; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static string Hash(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
    }
}
