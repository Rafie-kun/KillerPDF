using System.IO;
using System.Text;
using KillerLauncher;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>KillerPDF's choices on the kit update stack: which release asset, the machine rule,
/// the installer command, and the exact checksum line.</summary>
public sealed class UpdateTargetTests
{
    private const string Installed = @"C:\Users\pat\AppData\Local\Programs\KillerPDF\KillerPDF.exe";
    private const string Launcher = @"D:\Tools\KillerPDF-Portable.exe";

    private static UpdateTargetChoice Choose(string? exe = Installed, bool portable = false, string? launcher = null,
        string? pid = null, bool writable = true, bool hklm = false, bool launcherExists = true)
        => UpdateTargetChoice.Choose(exe, portable, launcher, pid, _ => writable, _ => hklm,
            path => path == Launcher && launcherExists);

    [Theory]
    [InlineData(false, "KillerPDF-Store.exe")]
    [InlineData(true, "KillerPDF-Portable.exe")]
    public void BundledRuntimePreservesItsInstalledChannelButPortableKeepsItsLauncher(bool portable, string asset)
    {
        string directory = Path.GetDirectoryName(Installed)!;
        var choice = UpdateTargetChoice.Choose(Installed, portable, Launcher, "42", _ => true, _ => false,
            path => path == Launcher || path == Path.Combine(directory, "coreclr.dll")
                || path == Path.Combine(directory, "System.Private.CoreLib.dll"));
        Assert.Equal(asset, choice.Asset);
        Assert.False(choice.Unsupported);
    }

    [Fact]
    public void InstalledCopyTakesTheInstallerAsset()
    {
        var choice = Choose();
        Assert.False(choice.Unsupported);
        Assert.Equal("KillerPDF.exe", choice.Asset);
        Assert.Null(choice.Replace);
        Assert.NotNull(choice.InstallerArguments);
    }

    [Fact]
    public void PackagedPortableCopyTakesThePortableAssetAndWaitsForItsLauncher()
    {
        var choice = Choose(portable: true, launcher: Launcher, pid: "4242");
        Assert.False(choice.Unsupported);
        Assert.Equal("KillerPDF-Portable.exe", choice.Asset);
        Assert.Equal(Launcher, choice.Replace);
        Assert.Equal(4242, choice.WaitForProcess);
        Assert.Null(choice.InstallerArguments);
        Assert.False(choice.Machine);
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData(Launcher, false, true)]
    [InlineData(Launcher, true, false)]
    public void PortableCopyWithoutAReplaceableLauncherIsNotUpdated(string? launcher, bool exists, bool writable)
        => Assert.True(Choose(portable: true, launcher: launcher, pid: "1", launcherExists: exists, writable: writable).Unsupported);

    [Fact]
    public void MissingProcessPathIsNotUpdated() => Assert.True(Choose(exe: null).Unsupported);

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    public void MachineMeansRegisteredUnderHklmOrAFolderThisUserCannotWrite(bool writable, bool hklm, bool machine)
        => Assert.Equal(machine, Choose(writable: writable, hklm: hklm).Machine);

    [Fact]
    public void InstallerArgumentsNameTheScopeTheFolderAndTheRelaunch()
    {
        string[] user = Choose().InstallerArguments!.Split(' ');
        string[] machine = Choose(hklm: true).InstallerArguments!.Split(' ');
        Assert.Equal("/install-user", user[0]);
        Assert.Equal("/silent", machine[0]);
        Assert.StartsWith("/install-dir64:", user[1]);
        Assert.Equal(@"C:\Users\pat\AppData\Local\Programs\KillerPDF",
            Encoding.UTF8.GetString(Convert.FromBase64String(user[1]["/install-dir64:".Length..])));
        Assert.True(UpdateRelaunchArguments.Requested(user));
        Assert.Null(UpdateRelaunchArguments.ReopenFile(user));
    }

    [Fact]
    public void ChecksumMatchesOnlyTheExactAssetLine()
    {
        const string installer = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        const string portable = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
        string sums = $"KillerPDF-Portable.exe  {portable}\r\nKillerPDF.exe           {installer}\r\n";
        Assert.Equal(installer, UpdateService.ChecksumFor(sums, "KillerPDF.exe"));
        Assert.Equal(portable, UpdateService.ChecksumFor(sums, "KillerPDF-Portable.exe"));
        Assert.Null(UpdateService.ChecksumFor($"KillerPDF.exe.bak {installer}\nKillerPDF.exe {installer} extra\n", "KillerPDF.exe"));
        Assert.Null(UpdateService.ChecksumFor($"  # KillerPDF.exe {installer}", "KillerPDF.exe"));
    }

    [Fact]
    public void PortableHelperFitsTheWindowsCommandLine()
    {
        // A long per-user temp path, like a real one, so the measured helper is not flattered.
        string root = Path.Combine(Path.GetTempPath(), "KillerPDF.UpdateTargetTests." + Guid.NewGuid().ToString("N"),
            new string('d', 80));
        Directory.CreateDirectory(root);
        try
        {
            string download = Path.Combine(root, "KillerPDF_update_" + Guid.NewGuid().ToString("N") + ".exe");
            UpdateImagePublication.RememberVerified(download, new string('A', 64), new string('B', 40));
            string script = "$ErrorActionPreference = 'Stop'\n" + TrustedUpdate.Wait(4242, DateTime.UtcNow.Ticks) + "\n"
                + UpdateService.LauncherWait(4243)
                + UpdateImagePublication.Script(download, Path.Combine(root, "KillerPDF-Portable.exe"), false,
                    UpdateService.ReleasesUrl, AppInfo.DisplayName, showReport: true);
            string arguments = ImmutablePowerShell.Arguments(script);
            Assert.True(arguments.Length <= 30000, $"{arguments.Length} characters");
        }
        finally { Directory.Delete(Path.GetDirectoryName(root)!, recursive: true); }
    }
}
