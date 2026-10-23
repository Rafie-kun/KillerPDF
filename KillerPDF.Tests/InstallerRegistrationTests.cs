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
        string register = Method(AppSource(), "private static void RegisterInstalledCopy(");
        int uninstall = register.IndexOf(@"CurrentVersion\Uninstall\KillerPDF", StringComparison.Ordinal);
        Assert.True(uninstall >= 0);
        foreach (string step in new[] { "CreateShortcut(", "RegisterFileHandler(", "ProtocolRegistrar.Register(",
                     "CommandLinePath.Register(", "PreviewHandlerRegistration." })
            Assert.True(uninstall < register.IndexOf(step, StringComparison.Ordinal), step + " runs before the uninstall entry.");
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
