using System.IO;
using System.Text.RegularExpressions;
using KillerPDF.Features;
using Xunit;

namespace KillerPDF.Tests;

// A command-line run promises never to show a window. A crash during one used to open the modal
// crash dialog, which stalled the script that started it until someone clicked it away.
public sealed class CommandLineCrashTests
{
    [Fact]
    public void CrashDuringACommandLineRunIsWrittenToItsConsole()
    {
        var console = new StringWriter();
        CommandLineCrash.Console = console;
        try
        {
            Assert.True(CommandLineCrash.TryReport(new InvalidOperationException("Render\r\nfailed"),
                @"C:\Logs\crash.log"));
            string written = console.ToString();
            Assert.Contains("Render failed", written);
            Assert.Contains(@"C:\Logs\crash.log", written);
            Assert.Single(written.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        }
        finally { CommandLineCrash.Console = null; }
    }

    [Fact]
    public void DesktopSessionKeepsTheCrashDialog()
    {
        CommandLineCrash.Console = null;
        Assert.False(CommandLineCrash.TryReport(new InvalidOperationException("failed"), "crash.log"));
    }

    [Fact]
    public void EveryCommandLineEntryRegistersItsConsoleBeforeRunning()
    {
        foreach (string file in new[] { "CliRunner.cs", "BatchRunner.cs", "BatchRenderRunner.cs" })
        {
            string source = File.ReadAllText(Path.Combine(Root(), "Features", "Cli", file));
            Assert.Matches(new Regex(@"var con = (?:BatchRunner\.)?OpenBatchConsole\(\);\s*CommandLineCrash\.Console = con;"), source);
        }
    }

    [Theory]
    [InlineData("OnDispatcherException", "RestartAfterRenderFailure")]
    [InlineData("OnDispatcherException", "QueueRecoverableCrashDialog")]
    [InlineData("OnDomainException", "ShowCrashDialog")]
    public void AppCrashHandlersReportCommandLineRunsBeforeAnyWindow(string handler, string window)
    {
        string source = File.ReadAllText(Path.Combine(Root(), "App.xaml.cs"));
        int start = source.IndexOf("private void " + handler + "(", StringComparison.Ordinal);
        Assert.True(start >= 0, handler + " was not found.");
        string rest = source[start..];
        string body = rest[..Regex.Match(rest, @"\r?\n        \}\r?\n").Index];
        int report = body.IndexOf("CommandLineCrash.TryReport(", StringComparison.Ordinal);
        Assert.True(report >= 0, handler + " does not report command-line crashes.");
        Assert.True(report < body.IndexOf(window, StringComparison.Ordinal),
            handler + " can reach " + window + " before reporting a command-line crash.");
        // A reported crash always ends the run with exit code 1, the command line's failure code.
        int exit = body.IndexOf("Environment.Exit(1);", report, StringComparison.Ordinal);
        Assert.True(exit > report && exit < body.IndexOf(window, StringComparison.Ordinal),
            handler + " does not exit with code 1 after reporting.");
    }

    private static string Root() => Directory.GetParent(Path.GetDirectoryName(SourcePath())!)!.FullName;

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
