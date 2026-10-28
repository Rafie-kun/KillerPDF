using System;

namespace KillerLauncher
{
    internal static class PortableLauncherPolicy
    {
        /// <summary>The one install the portable package performs: KillerPDF's own footer Install
        /// button asks for it (Shell/PortableInstallPlan.cs). Installer switches stay refused, so the
        /// portable file never acts as Setup for deployment tools.</summary>
        internal const string InAppInstallArgument = "/install-from-portable";
        internal const string AllUsersArgument = "/all-users";
        internal const string DesktopArgument = "/desktop";

        internal static bool IsInstallationArgument(string argument) =>
            string.Equals(argument, "/install-user", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, "/silent", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, "/register-user", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, "/register-machine", StringComparison.OrdinalIgnoreCase);

        /// <summary>Reads the app's install request: <see cref="InAppInstallArgument"/> first, then
        /// at most one each of <see cref="AllUsersArgument"/> and <see cref="DesktopArgument"/>.
        /// Anything else returns null.</summary>
        internal static (bool Machine, bool Desktop)? ReadInAppInstall(string[] args)
        {
            if (args.Length == 0 || !string.Equals(args[0], InAppInstallArgument, StringComparison.OrdinalIgnoreCase))
                return null;
            bool machine = false, desktop = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (!machine && string.Equals(args[i], AllUsersArgument, StringComparison.OrdinalIgnoreCase)) machine = true;
                else if (!desktop && string.Equals(args[i], DesktopArgument, StringComparison.OrdinalIgnoreCase)) desktop = true;
                else return null;
            }
            return (machine, desktop);
        }
    }
}
