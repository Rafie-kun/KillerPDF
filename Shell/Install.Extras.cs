using System.Threading.Tasks;
using System.Windows;
using KillerPDF.Controls;
using KillerPDF.Features;

namespace KillerPDF.Shell
{
    /// <summary>
    /// APP-OWNED: KillerPDF's side of the family install badge (Shell/Install.cs). The confirm
    /// carries the all-users checkbox, locked when a machine-wide copy already exists. Unsaved
    /// documents hold the install, because the installed copy replaces this process. The install
    /// itself is App.InstallAndRelaunch with the command PortableInstallPlan chooses: the packaged
    /// launcher's in-app install (elevated for all users), the same install paths and registry
    /// keys as 1.8.x, and the installed copy reopens the active document.
    /// </summary>
    public partial class MainWindow
    {
        partial void ConfigureInstall(InstallOptions options)
        {
            options.OfferAllUsers = true;
            // Already installed for all users (by an admin, winget, choco or an RMM)? Then this
            // updates that copy in place, so the all-users box is ticked and locked with a note.
            if (App.MachineInstallExists())
                options.AllUsersLockNote = Loc("Str_Install_AlreadyAllUsers");
            options.CanInstallAsync = () =>
            {
                if (!((IAboutUpdateRestart)this).IsDirty) return Task.FromResult(true);
                KillerDialog.Show(this, Loc("Str_Dlg_SaveBeforeInstall"), "KillerPDF",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return Task.FromResult(false);
            };
            // The installed copy reopens the user's file, not this tab's temporary working copy.
            options.InstallAsync = async (desktop, allUsers) =>
                await App.InstallAndRelaunchAsync(wantDesktop: desktop, allUsers: allUsers,
                    fileToOpen: _originalFile ?? _currentFile)
                    ? InstallOutcome.Installed : InstallOutcome.Failed;
            // SetStatus honors the status hold, so the failure line is not stomped by the next fit.
            options.ShowStatus = SetStatus;
        }
    }
}
