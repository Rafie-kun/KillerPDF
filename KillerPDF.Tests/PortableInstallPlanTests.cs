using KillerPDF.Shell;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>The footer Install button's command choice for a portable copy: the same launcher and
/// /silent commands as 1.8.x, so install paths, registry keys and uninstall names do not change.</summary>
public sealed class PortableInstallPlanTests
{
    private const string Current = @"D:\Tools\KillerPDF.exe";
    private const string Launcher = @"D:\Tools\KillerPDF-Portable.exe";
    private const string UserExe = @"C:\Users\pat\AppData\Local\Programs\KillerPDF\KillerPDF.exe";
    private const string MachineExe = @"C:\Program Files\KillerPDF\KillerPDF.exe";

    private static PortableInstallPlan Choose(bool allUsers, bool desktop = false, bool machine = false,
        string? launcher = Launcher, string? current = Current)
        => PortableInstallPlan.Choose(allUsers, desktop, machine, launcher, current, UserExe, MachineExe);

    [Fact]
    public void PerUserInstallRunsTheLauncherUnelevated()
    {
        var plan = Choose(allUsers: false, desktop: true);
        Assert.Equal(PortableInstallKind.Process, plan.Kind);
        Assert.Equal(Launcher, plan.Program);
        Assert.Equal("/install-user /desktop", plan.Arguments);
        Assert.False(plan.Elevate);
        Assert.Equal(UserExe, plan.TargetExe);
        Assert.Equal("/install-user", Choose(allUsers: false).Arguments);
    }

    [Fact]
    public void AllUsersInstallRunsSilentElevatedThroughTheLauncher()
    {
        var plan = Choose(allUsers: true);
        Assert.Equal(PortableInstallKind.Process, plan.Kind);
        Assert.Equal(Launcher, plan.Program);
        Assert.Equal("/silent", plan.Arguments);
        Assert.True(plan.Elevate);
        Assert.Equal(MachineExe, plan.TargetExe);
        Assert.Equal("/silent /desktop", Choose(allUsers: true, desktop: true).Arguments);
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
