using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace KillerLauncher
{
    internal static class PortableDirectoryCleanup
    {
        internal static bool TryDelete(string directory, int attempts = 5, int baseDelayMilliseconds = 150)
        {
            if (!Directory.Exists(directory)) return true;
            if (attempts < 1) throw new ArgumentOutOfRangeException(nameof(attempts));
            if (baseDelayMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(baseDelayMilliseconds));

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                try
                {
                    foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                        try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                    Directory.Delete(directory, recursive: true);
                    return true;
                }
                catch when (attempt + 1 < attempts)
                {
                    Thread.Sleep(baseDelayMilliseconds * (attempt + 1));
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Staging and previous-copy folders an earlier install left beside
        /// <paramref name="destination"/>, matched by their exact names: the install folder's name,
        /// ".staging-" or ".previous-", and a 32-digit GUID.
        /// </summary>
        internal static IReadOnlyList<string> LeftoverSiblings(string destination)
        {
            var found = new List<string>();
            string? parent = Path.GetDirectoryName(destination);
            if (parent is null || !Directory.Exists(parent)) return found;
            string name = Path.GetFileName(destination);
            foreach (string directory in Directory.GetDirectories(parent, name + ".*"))
            {
                string leaf = Path.GetFileName(directory);
                if (!leaf.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)) continue;
                string suffix = leaf.Substring(name.Length);
                foreach (string kind in new[] { ".staging-", ".previous-" })
                    if (suffix.StartsWith(kind, StringComparison.OrdinalIgnoreCase)
                        && Guid.TryParseExact(suffix.Substring(kind.Length), "N", out _)
                        && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                        found.Add(directory);
            }
            return found;
        }
    }
}
