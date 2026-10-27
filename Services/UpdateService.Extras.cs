using System;
using System.IO;
using Microsoft.Win32;

namespace KillerPDF.Services
{
    /// <summary>
    /// APP-OWNED: how KillerPDF updates on the shared update stack (Services/UpdateService.cs). The
    /// choice itself is in UpdateTargetChoice.cs. An installed copy uses the kit's direct installer
    /// mode, so elevation is requested on the signed installer and the UAC prompt names the
    /// publisher; the launcher is named by KILLERPDF_LAUNCHER_PATH and KILLERPDF_LAUNCHER_PID.
    /// </summary>
    internal static partial class UpdateService
    {
        static partial void ChooseUpdateTarget(UpdateTarget target)
        {
            var choice = UpdateTargetChoice.Choose(Environment.ProcessPath, App.IsPortable(),
                Environment.GetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH"),
                Environment.GetEnvironmentVariable("KILLERPDF_LAUNCHER_PID"),
                CanCreateFileIn, exe => App.IsRegisteredCopy(Registry.LocalMachine, exe), File.Exists);
            target.Unsupported = choice.Unsupported;
            target.Asset = choice.Asset;
            target.Replace = choice.Replace;
            target.WaitForProcess = choice.WaitForProcess;
            target.Machine = choice.Machine;
            target.InstallerArguments = choice.InstallerArguments;
            target.DirectInstaller = choice.InstallerArguments is not null;
        }

        /// <summary>True if this process can create a file in <paramref name="directory"/>.</summary>
        private static bool CanCreateFileIn(string directory)
        {
            try
            {
                var probe = Path.Combine(directory, $".kp_write_{Guid.NewGuid():N}.tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                      1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch { return false; }
        }
    }
}
