using System;
using System.IO;
using System.Security.Cryptography;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// A failed or interrupted update can leave its working files beside the installed exe: the
/// publication's ".old-", ".new-" and ".restore-" copies and the trusted path's staged
/// ".update-&lt;guid&gt;.exe" with its recovery report. They go once they are over a day old, when a
/// recovery copy can no longer matter, and the trusted staging is where that sweep runs, because
/// it is the one step that knows the installed exe is in place (BF-A010).
/// </summary>
public sealed class PublicationLeftoverSweepTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "killerpdf-leftover-sweep-" + Guid.NewGuid().ToString("N"));

    public PublicationLeftoverSweepTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void AgedLeftoversGoAndFreshOnesStay()
    {
        string installed = Fake(AppInfo.ExeName);
        string[] aged = [Aged(".update-" + Guid.NewGuid().ToString("N") + ".exe"), Aged(AppInfo.ExeName + ".old-" + Guid.NewGuid().ToString("N"))];
        string agedReport = Aged(Path.GetFileName(aged[0]) + ".recovery.txt");
        string fresh = Fake(".update-" + Guid.NewGuid().ToString("N") + ".exe");
        string unrelated = Aged("notes.txt");

        ExecutableDeployment.RemovePublicationLeftovers(installed);

        foreach (string path in aged) Assert.False(File.Exists(path), path);
        Assert.False(File.Exists(agedReport));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(unrelated));
        Assert.True(File.Exists(installed));
    }

    [Fact]
    public void TheTrustedStagingSweepsTheInstallationFolder()
    {
        Fake(AppInfo.ExeName);
        string aged = Aged(".update-" + Guid.NewGuid().ToString("N") + ".exe");
        string download = Path.Combine(_directory, "download.exe");
        byte[] bytes = new byte[256];
        Random.Shared.NextBytes(bytes);
        File.WriteAllBytes(download, bytes);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string staged = Path.Combine(_directory, ".update-" + Guid.NewGuid().ToString("N") + ".exe");

        TrustedUpdate.Stage(download, staged, hash, _ => { });

        Assert.True(File.Exists(staged));
        Assert.False(File.Exists(aged), "The aged staged copy survived the trusted staging.");
    }

    private string Fake(string name)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private string Aged(string name)
    {
        string path = Fake(name);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        return path;
    }
}
