namespace KillerPDF.Shell
{
    /// <summary>
    /// APP-OWNED: what the footer's Install button runs for a portable KillerPDF (Shell/Install.cs,
    /// Shell/Install.Extras.cs). Pure, so the argument and target choice is testable without
    /// installing anything. The commands are the ones 1.8.x used, so install paths, registry keys,
    /// uninstall names and the exe name do not change.
    /// </summary>
    internal sealed record PortableInstallPlan(
        PortableInstallKind Kind, string? Program, string? Arguments, bool Elevate, string? TargetExe)
    {
        /// <summary>
        /// Chooses the install for the portable copy at <paramref name="currentExe"/>.
        /// A packaged portable asks its launcher for the in-app install
        /// (KillerLauncher.PortableLauncherPolicy.InAppInstallArgument), elevated for all users.
        /// The launcher refuses Setup's /install-user and /silent, so those are never sent to it.
        /// Without a launcher (old woven builds and developer runs), all users re-runs this exe
        /// elevated with /silent and a per-user install copies this exe in process. A per-user
        /// install while an all-users copy exists is refused, so there are never two installed copies.
        /// </summary>
        /// <summary>
        /// The document the installed copy reopens: the user's real file, or the working copy only when
        /// it outlives this process. A browser download that was never saved lives in a session temp
        /// file that cleanup deletes on exit, before the installed copy could open it, so that tab hands
        /// over nothing rather than a path about to vanish.
        /// </summary>
        internal static string? ReopenPath(string? originalFile, string? currentFile, Func<string, bool> isCleanupOwned)
        {
            if (!string.IsNullOrEmpty(originalFile)) return originalFile;
            if (string.IsNullOrEmpty(currentFile) || isCleanupOwned(currentFile)) return null;
            return currentFile;
        }

        internal static PortableInstallPlan Choose(bool allUsers, bool desktop, bool machineInstallExists,
            string? launcher, string? currentExe, string userInstallExe, string machineInstallExe,
            bool currentExeIsSelfContained)
        {
            if (!allUsers && machineInstallExists)
                return new(PortableInstallKind.RefusedSecondCopy, null, null, false, null);
            if (launcher != null)
            {
                string arguments = "/install-from-portable" + (allUsers ? " /all-users" : "") + (desktop ? " /desktop" : "");
                return new(PortableInstallKind.Process, launcher, arguments, Elevate: allUsers,
                    allUsers ? machineInstallExe : userInstallExe);
            }
            // The installs below copy or re-run this exe on its own, which needs a self-contained exe.
            if (!currentExeIsSelfContained)
                return new(PortableInstallKind.Unavailable, null, null, false, null);
            if (allUsers)
            {
                string? program = currentExe;
                if (string.IsNullOrEmpty(program))
                    return new(PortableInstallKind.Unavailable, null, null, false, null);
                return new(PortableInstallKind.Process, program, desktop ? "/silent /desktop" : "/silent",
                    Elevate: true, machineInstallExe);
            }
            return new(PortableInstallKind.InProcess, null, null, false, userInstallExe);
        }

        /// <summary>
        /// How the installed copy is started once the install finishes. The portable launcher's
        /// variables would make the installed copy's first session use the portable data folder, and
        /// the launcher's extraction folder as working directory would stop the launcher removing it.
        /// </summary>
        internal static System.Diagnostics.ProcessStartInfo InstalledRelaunch(string targetExe, string? fileToOpen)
        {
            var start = new System.Diagnostics.ProcessStartInfo(targetExe)
            {
                UseShellExecute = false,
                WorkingDirectory = System.IO.Path.GetDirectoryName(targetExe) ?? string.Empty,
            };
            foreach (string name in new[] { "KILLERPDF_LAUNCHER_PATH", "KILLERPDF_LAUNCHER_PID" })
                start.Environment.Remove(name);
            if (fileToOpen != null) start.ArgumentList.Add(fileToOpen);
            return start;
        }
    }

    internal enum PortableInstallKind
    {
        /// <summary>Run <see cref="PortableInstallPlan.Program"/> with its arguments and wait.</summary>
        Process,
        /// <summary>Copy this exe into the per-user location in process.</summary>
        InProcess,
        /// <summary>A per-user install was asked for while an all-users copy exists.</summary>
        RefusedSecondCopy,
        /// <summary>The current executable path is unavailable.</summary>
        Unavailable,
    }
}
