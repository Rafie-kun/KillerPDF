using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace KillerPDF.Services
{
    /// <summary>
    /// Puts an installed copy on PATH so "killerpdf" runs the command line from any new terminal.
    /// The application file is KillerPDF.App.exe, so a small KillerPDF.cmd next to it carries the
    /// command name. Running through the script also makes the terminal wait for the result and
    /// return its exit code, which a GUI-subsystem program started directly does not do.
    /// </summary>
    internal static partial class CommandLinePath
    {
        internal const string CommandFileName = "KillerPDF.cmd";
        private const string UserEnvironmentKey = "Environment";
        private const string MachineEnvironmentKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
        private const string PathValueName = "Path";

        [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
            uint flags, uint timeout, out IntPtr result);
        private static readonly IntPtr HwndBroadcast = new(0xffff);
        private const uint WmSettingChange = 0x001A;
        private const uint SmtoAbortIfHung = 0x0002;

        /// <summary>The script that forwards every argument to the application beside it.</summary>
        internal static string CommandScript(string executableName) =>
            "@\"%~dp0" + executableName + "\" %*\r\n@exit /b %ERRORLEVEL%\r\n";

        /// <summary>Writes the command script into the install folder and adds the folder to the
        /// current user's PATH, or to the machine PATH for an all-users install.</summary>
        internal static void Register(string installDirectory, string executableName, bool machine)
        {
            File.WriteAllText(Path.Combine(installDirectory, CommandFileName), CommandScript(executableName));
            using RegistryKey key = OpenEnvironment(machine);
            if (AddTo(key, installDirectory)) BroadcastEnvironmentChange();
        }

        /// <summary>Takes the install folder back out of PATH. Best effort: uninstall and the
        /// move to an all-users install go ahead even when the entry cannot be removed.</summary>
        internal static void Unregister(string installDirectory, bool machine)
        {
            try
            {
                using RegistryKey key = OpenEnvironment(machine);
                if (RemoveFrom(key, installDirectory)) BroadcastEnvironmentChange();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
                or IOException) { }
        }

        private static RegistryKey OpenEnvironment(bool machine) => machine
            ? Registry.LocalMachine.OpenSubKey(MachineEnvironmentKey, writable: true)
                ?? throw new InvalidOperationException("The machine environment is unavailable.")
            : Registry.CurrentUser.CreateSubKey(UserEnvironmentKey, writable: true);

        /// <summary>Adds the folder to the key's Path value once, keeping unexpanded entries such
        /// as %SystemRoot% and the value's registry type. Returns true when the value changed.</summary>
        internal static bool AddTo(RegistryKey environment, string directory)
        {
            (string current, RegistryValueKind kind) = ReadPath(environment);
            string updated = WithEntry(current, directory);
            if (string.Equals(updated, current, StringComparison.Ordinal)) return false;
            environment.SetValue(PathValueName, updated, kind);
            return true;
        }

        /// <summary>Removes every spelling of the folder from the key's Path value without touching
        /// other entries. Returns true when the value changed.</summary>
        internal static bool RemoveFrom(RegistryKey environment, string directory)
        {
            if (environment.GetValue(PathValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is null)
                return false;
            (string current, RegistryValueKind kind) = ReadPath(environment);
            string updated = WithoutEntry(current, directory);
            if (string.Equals(updated, current, StringComparison.Ordinal)) return false;
            environment.SetValue(PathValueName, updated, kind);
            return true;
        }

        private static (string Value, RegistryValueKind Kind) ReadPath(RegistryKey environment)
        {
            object? raw = environment.GetValue(PathValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (raw is null) return (string.Empty, RegistryValueKind.ExpandString);
            RegistryValueKind kind = environment.GetValueKind(PathValueName);
            return (raw as string ?? string.Empty,
                kind is RegistryValueKind.String or RegistryValueKind.ExpandString ? kind : RegistryValueKind.ExpandString);
        }

        /// <summary>The PATH text with the folder appended, unless some entry already names it.</summary>
        internal static string WithEntry(string path, string directory)
        {
            List<string> entries = Entries(path);
            if (entries.Any(entry => SameDirectory(entry, directory))) return path;
            entries.Add(Normalize(directory));
            return string.Join(";", entries);
        }

        /// <summary>The PATH text without any entry naming the folder; unchanged when none does.</summary>
        internal static string WithoutEntry(string path, string directory)
        {
            List<string> entries = Entries(path);
            List<string> kept = [.. entries.Where(entry => !SameDirectory(entry, directory))];
            return kept.Count == entries.Count ? path : string.Join(";", kept);
        }

        private static List<string> Entries(string path) =>
            [.. path.Split(';').Where(entry => entry.Trim().Length > 0)];

        private static string Normalize(string directory) =>
            Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        internal static bool SameDirectory(string entry, string directory)
        {
            try
            {
                // Only a fully qualified entry can name the install folder. A relative one such
                // as "." or "C:bin" would resolve against whatever folder setup runs from and
                // belongs to the user.
                string expanded = Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'));
                if (!IsFullyQualified(expanded)) return false;
                return string.Equals(Normalize(expanded), Normalize(directory), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        private static bool IsFullyQualified(string path) =>
            path.StartsWith(@"\\", StringComparison.Ordinal)
            || (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':'
                && (path[2] == Path.DirectorySeparatorChar || path[2] == Path.AltDirectorySeparatorChar));

        private static void BroadcastEnvironmentChange()
        {
            // Explorer rereads the environment on this broadcast, so terminals opened from the
            // desktop after setup see the new PATH without signing out.
            SendMessageTimeout(HwndBroadcast, WmSettingChange, IntPtr.Zero, "Environment",
                SmtoAbortIfHung, 2000, out _);
        }
    }
}
