using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Runs the real portable self-update helper, unelevated, through the same launch the app uses, with a copy of
/// cmd.exe standing in for the downloaded update. Relaunch and the releases page are recorded instead of opened.
/// </summary>
public sealed class UpdateHelperLaunchTests
{
    private static readonly byte[] StandIn = File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "cmd.exe"));

    [Fact]
    public void VerifiedStandInIsPublishedAndRelaunched()
    {
        using var run = new HelperRun(StandIn);
        Assert.True(run.Exit == 0, run.ToString());
        Assert.Equal(StandIn, File.ReadAllBytes(run.Target));
        Assert.True(run.Relaunched, run.ToString());
        Assert.False(run.ReleasesOpened, run.ToString());
        Assert.False(File.Exists(run.Download), run.ToString());
    }

    [Fact]
    public void DownloadSwappedAfterVerificationIsRefused()
    {
        using var run = new HelperRun([.. StandIn, 0]);
        Assert.True(run.Exit != 0, run.ToString());
        Assert.Equal(HelperRun.Original, File.ReadAllBytes(run.Target));
        Assert.False(run.Relaunched, run.ToString());
        Assert.True(run.ReleasesOpened, run.ToString());
        Assert.Empty(Directory.GetFiles(run.Root, "KillerPDF-Portable.exe.new-*"));
    }

    [Fact]
    public void HelperStartsPowerShellWithItsScriptInTheCommandLine()
    {
        var start = UpdateHelperLaunch.StartInfo(UpdateHelperLaunch.WaitFor(1234, 5678) + "exit 0", elevate: false);
        Assert.Equal("", start.Verb);
        Assert.True(start.UseShellExecute);
        Assert.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe", start.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-EncodedCommand ", start.Arguments);
        Assert.DoesNotContain(".bat", start.Arguments, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.System), start.WorkingDirectory);
    }

    private sealed class HelperRun : IDisposable
    {
        internal static readonly byte[] Original = Encoding.UTF8.GetBytes("original inert launcher image");

        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "KillerPDF-update-helper-" + Guid.NewGuid().ToString("N"));
        internal string Target => Path.Combine(Root, "KillerPDF-Portable.exe");
        internal string Download => Path.Combine(Root, "download.exe");
        internal int Exit { get; }
        internal bool Relaunched => File.Exists(Path.Combine(Root, "relaunched.txt"));
        internal bool ReleasesOpened => File.Exists(Path.Combine(Root, "releases.txt"));

        internal HelperRun(byte[] download)
        {
            Directory.CreateDirectory(Root);
            File.WriteAllBytes(Target, Original);
            File.WriteAllBytes(Download, download);
            UpdateImagePublication.RememberVerified(Download, Convert.ToHexString(SHA256.HashData(StandIn)));
            string overrides = "$ops.Relaunch = { param($plan) [IO.File]::WriteAllText('" + Path.Combine(Root, "relaunched.txt") + "', 'yes') }\n" +
                "$ops.Releases = { param($url) [IO.File]::WriteAllText('" + Path.Combine(Root, "releases.txt") + "', $url) }";
            string script = UpdateHelperLaunch.WaitFor(0, -1) + UpdateImagePublication.Script(Download, Target, false,
                "https://example.invalid/releases", "KillerPDF", overrides: overrides);
            using var process = Process.Start(UpdateHelperLaunch.StartInfo(script, elevate: false));
            Assert.NotNull(process);
            if (!process.WaitForExit(60_000))
            {
                process.Kill(true);
                Assert.Fail("The update helper did not finish. Scratch retained: " + Root);
            }
            Exit = process.ExitCode;
        }

        public override string ToString()
        {
            string report = Download + ".recovery.txt";
            return "Exit " + Exit + ", relaunched " + Relaunched + ", releases " + ReleasesOpened +
                (File.Exists(report) ? Environment.NewLine + File.ReadAllText(report) : "");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
