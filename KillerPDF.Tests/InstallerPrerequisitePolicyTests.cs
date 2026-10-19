using System.IO;
using KillerLauncher;
using Xunit;

namespace KillerPDF.Tests;

public sealed class InstallerPrerequisitePolicyTests
{
    [Fact]
    public void SilentInstallContinuesWhenDesktopRuntimeIsPresent() =>
        Assert.Null(InstallerPrerequisitePolicy.SilentInstallRejection(hasDesktopRuntime10: true));

    [Fact]
    public void SilentInstallReturnsStableFailureBeforeInstallWhenDesktopRuntimeIsMissing() =>
        Assert.Equal(InstallerPrerequisitePolicy.MissingDesktopRuntimeExitCode,
            InstallerPrerequisitePolicy.SilentInstallRejection(hasDesktopRuntime10: false));

    [Fact]
    public void X64WindowsFindsTheRuntimeInTheDotnetFolder()
    {
        using var programFiles = new TempProgramFiles(@"dotnet\shared\Microsoft.WindowsDesktop.App\10.0.3");
        Assert.True(InstallerPrerequisitePolicy.HasDesktopRuntime10(programFiles.Path, arm64Host: false));
    }

    [Fact]
    public void Arm64WindowsFindsTheX64RuntimeInItsOwnFolder()
    {
        using var programFiles = new TempProgramFiles(@"dotnet\x64\shared\Microsoft.WindowsDesktop.App\10.0.3");
        Assert.True(InstallerPrerequisitePolicy.HasDesktopRuntime10(programFiles.Path, arm64Host: true));
    }

    [Fact]
    public void Arm64WindowsDoesNotCountTheArm64RuntimeForTheX64App()
    {
        using var programFiles = new TempProgramFiles(@"dotnet\shared\Microsoft.WindowsDesktop.App\10.0.3");
        Assert.False(InstallerPrerequisitePolicy.HasDesktopRuntime10(programFiles.Path, arm64Host: true));
    }

    [Fact]
    public void AnOlderDesktopRuntimeDoesNotCount()
    {
        using var programFiles = new TempProgramFiles(@"dotnet\shared\Microsoft.WindowsDesktop.App\9.0.10");
        Assert.False(InstallerPrerequisitePolicy.HasDesktopRuntime10(programFiles.Path, arm64Host: false));
    }

    [Fact]
    public void AMissingDotnetFolderMeansNoRuntime()
    {
        using var programFiles = new TempProgramFiles();
        Assert.False(InstallerPrerequisitePolicy.HasDesktopRuntime10(programFiles.Path, arm64Host: false));
    }

    private sealed class TempProgramFiles : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kp-runtime-" + Guid.NewGuid().ToString("N"));

        public TempProgramFiles(params string[] folders)
        {
            Directory.CreateDirectory(Path);
            foreach (string folder in folders)
                Directory.CreateDirectory(System.IO.Path.Combine(Path, folder));
        }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
