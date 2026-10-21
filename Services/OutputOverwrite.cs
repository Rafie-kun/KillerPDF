using System.IO;
using System.Windows;
using KillerPDF.Controls;

namespace KillerPDF.Services
{
    /// <summary>
    /// The final-command check for dialogs that write a new file (Sign, Protect): ask before an
    /// existing file is replaced, and refuse when the file changes between that answer and the write.
    /// </summary>
    internal static class OutputOverwrite
    {
        /// <summary>False when the user declined to replace an existing file.</summary>
        internal static bool Confirm(Window owner, string path, out FileRevision? approved)
        {
            approved = FileRevision.Capture(path);
            if (approved is null) return true;
            return KillerDialog.Show(owner,
                string.Format(Loc("Str_Dlg_OverwriteMsg"), Path.GetFileName(path)), Loc("Str_Dlg_AppTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, defaultResult: MessageBoxResult.No)
                == MessageBoxResult.Yes;
        }

        /// <summary>
        /// True when the destination is still what the user approved. When a file appeared or
        /// changed there meanwhile, the user is asked again about the file now in place.
        /// </summary>
        internal static bool StillAsApproved(Window owner, string path, FileRevision? approved) =>
            FileRevision.Capture(path) == approved || Confirm(owner, path, out _);

        private static string Loc(string key) => Application.Current.TryFindResource(key) as string ?? key;
    }
}
