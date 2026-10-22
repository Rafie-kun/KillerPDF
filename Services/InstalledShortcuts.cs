using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace KillerPDF.Services;

internal static class InstalledShortcuts
{
    internal static string DesktopPath(bool machine)
    {
        string directory = Environment.GetFolderPath(machine ? Environment.SpecialFolder.CommonDesktopDirectory
            : Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.DoNotVerify);
        if (!Path.IsPathFullyQualified(directory))
            throw new DirectoryNotFoundException("The Windows desktop folder is unavailable.");
        return Path.Combine(directory, "KillerPDF.lnk");
    }

    internal static void Create(string linkPath, string targetPath)
    {
        if (!Path.IsPathFullyQualified(linkPath) || !Path.IsPathFullyQualified(targetPath))
            throw new ArgumentException("Shortcut and application paths must be absolute.");
        linkPath = Path.GetFullPath(linkPath);
        targetPath = Path.GetFullPath(targetPath);
        if (!File.Exists(targetPath)) throw new FileNotFoundException("The shortcut target is missing.", targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        string temporary = Path.Combine(Path.GetDirectoryName(linkPath)!, Guid.NewGuid().ToString("N") + ".lnk");
        try
        {
            object instance = new ShellLink();
            try
            {
                var shortcut = (IShellLink)instance;
                shortcut.SetPath(targetPath);
                shortcut.SetWorkingDirectory(Path.GetDirectoryName(targetPath)!);
                shortcut.SetDescription("KillerPDF");
                shortcut.SetIconLocation(targetPath, 0);
                ((IPersistFile)instance).Save(temporary, true);
            }
            finally { Marshal.FinalReleaseComObject(instance); }
            Verify(temporary, targetPath);
            File.Move(temporary, linkPath, overwrite: true);
            Verify(linkPath, targetPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static (string Target, string WorkingDirectory, string Icon, int IconIndex) Read(string linkPath)
    {
        object instance = new ShellLink();
        try
        {
            ((IPersistFile)instance).Load(Path.GetFullPath(linkPath), 0);
            var shortcut = (IShellLink)instance;
            var target = new StringBuilder(32768);
            var directory = new StringBuilder(32768);
            var icon = new StringBuilder(32768);
            shortcut.GetPath(target, target.Capacity, IntPtr.Zero, 4);
            shortcut.GetWorkingDirectory(directory, directory.Capacity);
            shortcut.GetIconLocation(icon, icon.Capacity, out int index);
            return (target.ToString(), directory.ToString(), icon.ToString(), index);
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    private static void Verify(string linkPath, string targetPath)
    {
        var saved = Read(linkPath);
        if (!string.Equals(saved.Target, targetPath, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(saved.WorkingDirectory, Path.GetDirectoryName(targetPath), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(saved.Icon, targetPath, StringComparison.OrdinalIgnoreCase) || saved.IconIndex != 0)
            throw new IOException("The installed shortcut could not be verified: " + linkPath);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLink
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int capacity);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int capacity, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }
}
