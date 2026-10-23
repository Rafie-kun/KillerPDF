using System.IO;
using Xunit;

namespace KillerPDF.Tests;

public sealed class InstallerRegistrationTests
{
    private static string AppSource() => File.ReadAllText(Path.Combine(
        Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!, "App.xaml.cs"));

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " was not found.");
        int end = source.IndexOf("\n        private static ", start + signature.Length, StringComparison.Ordinal);
        return source[start..end];
    }

    // A desktop shortcut that could not be written used to abort setup after the payload was copied
    // and before the uninstall entry existed, leaving a copy nothing could remove.
    [Fact]
    public void InstalledCopyIsRegisteredForUninstallBeforeOptionalSteps()
    {
        string register = Method(AppSource(), "private static string? RegisterInstalledCopy(");
        int uninstall = register.IndexOf("key.SetValue(\"UninstallString\"", StringComparison.Ordinal);
        Assert.True(uninstall >= 0);
        string[] steps = ["CreateShortcut(", "RegisterFileHandler(", "ProtocolRegistrar.Register(",
            "CommandLinePath.Register(", "PreviewHandlerRegistration."];
        foreach (string step in steps)
            Assert.True(uninstall < register.IndexOf(step, StringComparison.Ordinal), step + " runs before the uninstall entry.");
        // A failed upgrade is rolled back by setup, so the advertised version is written only after
        // every required step, and the entry keeps naming the version that is really installed.
        foreach (string version in new[] { "key.SetValue(\"DisplayVersion\"", "key.SetValue(\"Version\"", "key.SetValue(\"EstimatedSize\"" })
            foreach (string step in steps)
                Assert.True(register.IndexOf(version, StringComparison.Ordinal) > register.LastIndexOf(step, StringComparison.Ordinal),
                    version + " is written before " + step);
    }

    // Owner decision, 2026-10-23: an optional desktop shortcut that Windows refuses no longer fails
    // setup. The install finishes and reports a warning; a Start menu shortcut failure stays fatal.
    [Fact]
    public void DesktopShortcutFailureIsAWarningButStartMenuFailureIsNot()
    {
        string source = AppSource();
        string register = Method(source, "private static string? RegisterInstalledCopy(");
        int startMenu = register.IndexOf("CreateShortcut(startMenuShortcut, exePath);", StringComparison.Ordinal);
        Assert.True(startMenu >= 0);
        Assert.DoesNotContain("try", register[(register.LastIndexOf('\n', startMenu) + 1)..startMenu]);
        Assert.Contains("try { CreateShortcut(InstalledShortcuts.DesktopPath(machine), exePath); }", register);
        Assert.Contains("desktopProblem = ex.Message;", register);
        Assert.Contains("return desktopProblem;", register);

        Assert.Equal(2, source.Split("Console.Error.WriteLine(\"Installed without the desktop shortcut: \" + desktopProblem);").Length - 1);
        Assert.Contains("SetupText(\"Str_Dlg_InstallNoDesktopShortcut\"", source);

        string wizard = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!,
            "Packaging", "KillerLauncher", "InstallerWizard.xaml.cs"));
        Assert.Contains("if (desktop && !Program.DesktopShortcutOpensInstalledCopy(machine, installDirectory))", wizard);
        Assert.Contains("LauncherStrings.Get(\"ShortcutSkipped\")", wizard);
    }

    // A declined or failed elevated uninstall used to exit 0.
    [Fact]
    public void ElevatedUninstallReportsItsOwnResult()
    {
        string source = AppSource();
        string relaunch = Method(source, "private static int? RelaunchMachineUninstallElevatedIfNeeded(");
        Assert.Contains("elevated.WaitForExit();", relaunch);
        Assert.Contains("return elevated.ExitCode;", relaunch);
        Assert.Contains("return 1223;", relaunch);
        Assert.Contains("return 1;", relaunch);
        Assert.Contains("is int elevatedExit) return elevatedExit;", source);
    }

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
