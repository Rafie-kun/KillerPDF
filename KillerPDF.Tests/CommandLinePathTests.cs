using System;
using System.Diagnostics;
using System.IO;
using KillerPDF.Services;
using Microsoft.Win32;
using Xunit;

namespace KillerPDF.Tests;

public sealed class CommandLinePathTests
{
    [Fact]
    public void AddsTheInstallFolderOnceWhateverItsSpelling()
    {
        string folder = Path.Combine(Path.GetTempPath(), "KillerPDF Test");
        string path = @"C:\Windows;C:\Tools";

        string added = CommandLinePath.WithEntry(path, folder);

        Assert.Equal(path + ";" + folder, added);
        Assert.Equal(added, CommandLinePath.WithEntry(added, folder + "\\"));
        Assert.Equal(added, CommandLinePath.WithEntry(added, folder.ToUpperInvariant()));
        string quoted = path + ";\"" + folder + "\"";
        Assert.Equal(quoted, CommandLinePath.WithEntry(quoted, folder));
    }

    [Fact]
    public void RemovesEverySpellingOfTheInstallFolderAndKeepsTheRest()
    {
        string folder = Path.Combine(Path.GetTempPath(), "KillerPDF Test");
        string path = @"%SystemRoot%\system32;" + folder + ";C:\\Tools;\"" + folder.ToUpperInvariant() + "\\\"";

        Assert.Equal(@"%SystemRoot%\system32;C:\Tools", CommandLinePath.WithoutEntry(path, folder));
        Assert.Equal(@"C:\Windows;;C:\Tools", CommandLinePath.WithoutEntry(@"C:\Windows;;C:\Tools", folder));
    }

    [Fact]
    public void RelativeEntriesNeverNameTheInstallFolder()
    {
        string folder = Environment.CurrentDirectory;
        string drive = folder[..2];

        Assert.False(CommandLinePath.SameDirectory(".", folder));
        Assert.False(CommandLinePath.SameDirectory(drive + Path.GetFileName(folder), folder));
        Assert.Equal(@".;C:\Tools", CommandLinePath.WithoutEntry(@".;C:\Tools;" + folder, folder));
    }

    [Fact]
    public void KeepsUnexpandedEntriesAndTheRegistryTypeOfPath()
    {
        string name = @"Software\KillerPDF-PathTest-" + Guid.NewGuid().ToString("N");
        string folder = Path.Combine(Path.GetTempPath(), "KillerPDF Test");
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(name, writable: true);
            key.SetValue("Path", @"%USERPROFILE%\bin", RegistryValueKind.ExpandString);

            Assert.True(CommandLinePath.AddTo(key, folder));
            Assert.False(CommandLinePath.AddTo(key, folder));
            Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("Path"));
            Assert.Equal(@"%USERPROFILE%\bin;" + folder,
                key.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames));

            Assert.True(CommandLinePath.RemoveFrom(key, folder));
            Assert.False(CommandLinePath.RemoveFrom(key, folder));
            Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("Path"));
            Assert.Equal(@"%USERPROFILE%\bin",
                key.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames));

            key.DeleteValue("Path");
            Assert.False(CommandLinePath.RemoveFrom(key, folder));
            Assert.Null(key.GetValue("Path"));
            Assert.True(CommandLinePath.AddTo(key, folder));
            Assert.Equal(RegistryValueKind.ExpandString, key.GetValueKind("Path"));
            Assert.Equal(folder, key.GetValue("Path"));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void CommandScriptForwardsArgumentsAndReturnsTheExitCode()
    {
        string folder = Path.Combine(Path.GetTempPath(), "KillerPDF cmd " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // A copy of cmd.exe stands in for the application: it sees the forwarded arguments.
            File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(folder, "Stand In.exe"));
            string script = Path.Combine(folder, CommandLinePath.CommandFileName);
            File.WriteAllText(script, CommandLinePath.CommandScript("Stand In.exe"));

            (int exitCode, _) = Run(script, "/d /c exit 7");
            Assert.Equal(7, exitCode);
            (exitCode, string output) = Run(script, "/d /c echo \"two words\"");
            Assert.Equal(0, exitCode);
            Assert.Contains("\"two words\"", output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static (int ExitCode, string Output) Run(string script, string arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            "/d /c \"\"" + script + "\" " + arguments + "\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(30_000));
        return (process.ExitCode, output);
    }
}
