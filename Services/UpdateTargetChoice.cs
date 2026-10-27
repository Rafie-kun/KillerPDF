using System;
using System.IO;

namespace KillerPDF.Services
{
    /// <summary>How one KillerPDF copy updates, as UpdateService.Extras.cs hands it to the kit.</summary>
    internal readonly record struct UpdateTargetChoice(bool Unsupported, string Asset, string? Replace,
        int WaitForProcess, bool Machine, string? InstallerArguments)
    {
        internal const string InstallerAsset = "KillerPDF.exe";
        internal const string PortableAsset = "KillerPDF-Portable.exe";

        private static readonly UpdateTargetChoice None = new(true, InstallerAsset, null, 0, false, null);

        /// <summary>
        /// An installed copy runs the signed installer (KillerPDF.exe) with the same /install-user or
        /// /silent command a manual upgrade uses, into the folder it is installed in, and asks it to
        /// start the new copy afterward. A machine-wide copy is one registered under HKLM, or one in a
        /// folder this process cannot write; an administrator running the Program Files copy can write
        /// there and must still update it machine-wide. A packaged portable copy replaces its launcher
        /// (KillerPDF-Portable.exe) once the launcher and this process have exited, and only when the
        /// launcher's folder is writable, because the portable update never elevates. A portable copy
        /// without a launcher (a development build) is not updated.
        /// </summary>
        internal static UpdateTargetChoice Choose(string? currentExe, bool portable, string? launcher,
            string? launcherPid, Func<string, bool> canWrite, Func<string, bool> machineRegistered,
            Func<string, bool> fileExists)
        {
            if (string.IsNullOrWhiteSpace(currentExe)) return None;
            if (portable)
            {
                if (string.IsNullOrWhiteSpace(launcher) || !fileExists(launcher!)
                    || !canWrite(Path.GetDirectoryName(Path.GetFullPath(launcher!))!))
                    return None;
                int pid = int.TryParse(launcherPid, out int parsed) && parsed > 0 ? parsed : 0;
                return new(false, PortableAsset, launcher, pid, false, null);
            }
            bool machine = !canWrite(Path.GetDirectoryName(Path.GetFullPath(currentExe!))!)
                || machineRegistered(currentExe!);
            return new(false, InstallerAsset, null, 0, machine,
                Features.InstalledUpdateCommand.Arguments(currentExe!, machine, null));
        }
    }
}
