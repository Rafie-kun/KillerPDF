using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KillerLauncher
{
    /// <summary>
    /// The arguments an updating application adds to the installer command so the installer
    /// starts the new copy when it is done: <c>/relaunch</c>, optionally followed by
    /// <c>/reopen:</c> and the base64 UTF-8 path of the document to open again.
    /// </summary>
    internal static class UpdateRelaunchArguments
    {
        internal const string Relaunch = "/relaunch";
        private const string ReopenPrefix = "/reopen:";

        internal static bool Requested(IEnumerable<string> args) =>
            args.Any(a => string.Equals(a, Relaunch, StringComparison.OrdinalIgnoreCase));

        internal static string? ReopenFile(IEnumerable<string> args)
        {
            string? value = args.FirstOrDefault(a => a.StartsWith(ReopenPrefix, StringComparison.OrdinalIgnoreCase));
            if (value == null) return null;
            try
            {
                string path = Encoding.UTF8.GetString(Convert.FromBase64String(value.Substring(ReopenPrefix.Length)));
                return path.Length == 0 || path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0 ? null : path;
            }
            catch (FormatException) { return null; }
        }

        internal static string Encode(string? reopenFile) =>
            string.IsNullOrEmpty(reopenFile)
                ? Relaunch
                : Relaunch + " " + ReopenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(reopenFile));
    }
}
