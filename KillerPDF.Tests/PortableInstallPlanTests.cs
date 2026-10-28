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
        string? launcher = Launcher, string? current = Current)
        => PortableInstallPlan.Choose(allUsers, desktop, machine, launcher, current, UserExe, MachineExe);

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
