using System;
using System.IO;
using KillerLauncher;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PortableLauncherPolicyTests
{
    [Theory]
    [InlineData("/install-user")]
    [InlineData("/silent")]
    [InlineData("/register-user")]
    [InlineData("/register-machine")]
    [InlineData("/REGISTER-USER")]
    public void PortablePackageRejectsInstallationAndRegistrationArguments(string argument)
    {
        Assert.True(PortableLauncherPolicy.IsInstallationArgument(argument));
    }

    [Theory]
    [InlineData("document.pdf")]
    [InlineData("/verify")]
    [InlineData("--verify")]
    public void PortablePackageAllowsOrdinaryArguments(string argument)
    {
        Assert.False(PortableLauncherPolicy.IsInstallationArgument(argument));
    }

    [Theory]
    [InlineData("/install-from-portable")]
    [InlineData("/install-from-portable /desktop")]
    [InlineData("/install-from-portable /all-users /desktop")]
    public void TheAppsOwnInstallRequestIsNotAnInstallerArgument(string arguments)
    {
        string[] args = arguments.Split(' ');
        Assert.DoesNotContain(args, PortableLauncherPolicy.IsInstallationArgument);
        Assert.NotNull(PortableLauncherPolicy.ReadInAppInstall(args));
    }

    [Theory]
    [InlineData("")]
    [InlineData("document.pdf")]
    [InlineData("/desktop /install-from-portable")]
    [InlineData("/install-from-portable document.pdf")]
    [InlineData("/install-from-portable /install-dir=C:\\Elsewhere")]
    [InlineData("/install-from-portable /silent")]
    [InlineData("/install-from-portable /desktop /desktop")]
    public void AnythingElseIsNotAnInAppInstall(string arguments)
    {
        string[] args = arguments.Length == 0 ? [] : arguments.Split(' ');
        Assert.Null(PortableLauncherPolicy.ReadInAppInstall(args));
    }

    [Fact]
    public void LockedPortableDirectoryIsLeftForTheNextSweep()
    {
        string directory = Path.Combine(Path.GetTempPath(), "killerpdf-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "locked.bin");
        File.WriteAllBytes(file, [1, 2, 3]);

        try
        {
            using (File.Open(file, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.False(PortableDirectoryCleanup.TryDelete(directory, attempts: 1, baseDelayMilliseconds: 0));
                Assert.True(Directory.Exists(directory));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    // A previous copy a late reader held after a completed upgrade is left beside the install.
    // The next install finds it by its exact name and removes it, and nothing else.
    [Fact]
    public void LeftoverInstallFoldersAreFoundByTheirExactNames()
    {
        string parent = Path.Combine(Path.GetTempPath(), "killerpdf-leftovers-" + Guid.NewGuid().ToString("N"));
        string destination = Path.Combine(parent, "KillerPDF");
        string previous = destination + ".previous-" + Guid.NewGuid().ToString("N");
        string staging = destination + ".staging-" + Guid.NewGuid().ToString("N");
        string[] kept =
        [
            destination,
            destination + ".previous-notaguid",
            destination + ".backup-" + Guid.NewGuid().ToString("N"),
            Path.Combine(parent, "KillerPDFX.previous-" + Guid.NewGuid().ToString("N")),
            Path.Combine(parent, "Other.previous-" + Guid.NewGuid().ToString("N")),
        ];
        try
        {
            foreach (string directory in kept) Directory.CreateDirectory(directory);
            Directory.CreateDirectory(previous);
            Directory.CreateDirectory(staging);

            Assert.Equal([previous, staging], [.. PortableDirectoryCleanup.LeftoverSiblings(destination).Order(StringComparer.OrdinalIgnoreCase)],
                StringComparer.OrdinalIgnoreCase);
            Assert.Empty(PortableDirectoryCleanup.LeftoverSiblings(Path.Combine(parent, "Missing", "KillerPDF")));
        }
        finally
        {
            if (Directory.Exists(parent)) Directory.Delete(parent, recursive: true);
        }
    }
}
