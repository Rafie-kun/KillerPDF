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
        /// An all-users install re-runs the packaged launcher (or this exe, for a direct run)
        /// elevated with /silent, the same machine-wide path winget and choco use. A per-user
        /// install runs the packaged launcher with /install-user, or copies this exe in process
        /// when there is no launcher (old woven builds and developer runs). A per-user install
        /// while an all-users copy exists is refused, so there are never two installed copies.
        /// </summary>
        internal static PortableInstallPlan Choose(bool allUsers, bool desktop, bool machineInstallExists,
            string? launcher, string? currentExe, string userInstallExe, string machineInstallExe)
        {
            if (!allUsers && machineInstallExists)
                return new(PortableInstallKind.RefusedSecondCopy, null, null, false, null);
            if (allUsers)
            {
                string? program = launcher ?? currentExe;
                if (string.IsNullOrEmpty(program))
                    return new(PortableInstallKind.Unavailable, null, null, false, null);
                return new(PortableInstallKind.Process, program, desktop ? "/silent /desktop" : "/silent",
                    Elevate: true, machineInstallExe);
            }
            if (launcher != null)
                return new(PortableInstallKind.Process, launcher, desktop ? "/install-user /desktop" : "/install-user",
                    Elevate: false, userInstallExe);
            return new(PortableInstallKind.InProcess, null, null, false, userInstallExe);
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
