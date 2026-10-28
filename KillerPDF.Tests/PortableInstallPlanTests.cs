using KillerPDF.Shell;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>The footer Install button's command choice for a portable copy. The launcher installs
/// through its own Install, so install paths, registry keys and uninstall names do not change.</summary>
public sealed class PortableInstallPlanTests
{
    private const string Current = @"D:\Tools\KillerPDF.exe";
    private const string Launcher = @"D:\Tools\KillerPDF-Portable.exe";
    private const string UserExe = @"C:\Users\pat\AppData\Local\Programs\KillerPDF\KillerPDF.exe";
    private const string MachineExe = @"C:\Program Files\KillerPDF\KillerPDF.exe";

    private static PortableInstallPlan Choose(bool allUsers, bool desktop = false, bool machine = false,
        string? launcher = Launcher, string? current = Current, bool selfContained = true)
        => PortableInstallPlan.Choose(allUsers, desktop, machine, launcher, current, UserExe, MachineExe, selfContained);

    // A packaged portable whose launcher is gone (moved, renamed, a pulled USB stick) runs the inner
    // multi-file app. Copying that one exe would register a broken install, so the install is refused.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WithoutALauncherAMultiFileAppCannotInstallItself(bool allUsers)
        => Assert.Equal(PortableInstallKind.Unavailable,
            Choose(allUsers, launcher: null, selfContained: false).Kind);

    [Fact]
    public void ALauncherInstallDoesNotDependOnTheInnerAppStandingAlone()
        => Assert.Equal(PortableInstallKind.Process, Choose(allUsers: false, selfContained: false).Kind);

    // The installed copy must not inherit the portable launcher's variables, or its first session
    // reads and writes settings in the portable data folder beside the launcher, and it must not hold
    // the launcher's extraction folder open as its working directory, or that folder is never removed.
    [Theory]
    [InlineData(null)]
    [InlineData(@"D:\Docs\Report.pdf")]
    public void TheInstalledRelaunchLeavesThePortableEnvironmentBehind(string? file)
    {
        // A launcher path that does not exist, so tests reading the portable data root concurrently are unaffected.
        string? priorPath = Environment.GetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH");
        string? priorPid = Environment.GetEnvironmentVariable("KILLERPDF_LAUNCHER_PID");
        System.Diagnostics.ProcessStartInfo start;
        try
        {
            Environment.SetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH", @"Q:\NoSuchFolder\KillerPDF-Portable.exe");
            Environment.SetEnvironmentVariable("KILLERPDF_LAUNCHER_PID", "4242");
            start = PortableInstallPlan.InstalledRelaunch(UserExe, file);
        }
        finally
        {
            Environment.SetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH", priorPath);
            Environment.SetEnvironmentVariable("KILLERPDF_LAUNCHER_PID", priorPid);
        }

        Assert.Equal(UserExe, start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal(System.IO.Path.GetDirectoryName(UserExe), start.WorkingDirectory);
        Assert.DoesNotContain("KILLERPDF_LAUNCHER_PATH", start.Environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("KILLERPDF_LAUNCHER_PID", start.Environment.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(file is null ? [] : [file], start.ArgumentList);
    }

    // The portable launcher refuses /install-user, /silent and the register switches (#259), so
    // the plan must hand it the one request it accepts from the app, or every install fails.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TheLauncherAcceptsTheInstallThePlanAsksFor(bool allUsers, bool desktop)
    {
        var plan = Choose(allUsers, desktop);
        Assert.Equal(PortableInstallKind.Process, plan.Kind);
        Assert.Equal(Launcher, plan.Program);
        Assert.Equal(allUsers, plan.Elevate);
        Assert.Equal(allUsers ? MachineExe : UserExe, plan.TargetExe);

        string[] args = plan.Arguments!.Split(' ');
        Assert.DoesNotContain(args, KillerLauncher.PortableLauncherPolicy.IsInstallationArgument);
        Assert.Equal((allUsers, desktop), KillerLauncher.PortableLauncherPolicy.ReadInAppInstall(args));
    }

    [Fact]
    public void WithoutALauncherAllUsersReRunsThisExeAndPerUserCopiesInProcess()
    {
        var machine = Choose(allUsers: true, launcher: null);
        Assert.Equal(Current, machine.Program);
        Assert.True(machine.Elevate);
        var user = Choose(allUsers: false, launcher: null);
        Assert.Equal(PortableInstallKind.InProcess, user.Kind);
        Assert.Null(user.Program);
        Assert.Equal(UserExe, user.TargetExe);
    }

    [Fact]
    public void PerUserInstallBesideAnAllUsersCopyIsRefused()
    {
        Assert.Equal(PortableInstallKind.RefusedSecondCopy, Choose(allUsers: false, machine: true).Kind);
        // Ticking all users updates the existing machine copy in place.
        Assert.Equal(PortableInstallKind.Process, Choose(allUsers: true, machine: true).Kind);
    }

    [Fact]
    public void AllUsersWithNoExecutablePathIsUnavailable()
        => Assert.Equal(PortableInstallKind.Unavailable, Choose(allUsers: true, launcher: null, current: null).Kind);
}
