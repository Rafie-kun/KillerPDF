using System.IO;
using System.Diagnostics;
using System.Text.Json.Nodes;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class UpdateRestartStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kp-update-restart-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private UpdateRestartStore Store() => new(_root, "install", "account", 3, () => _now, (_, _) => false);

    [Fact]
    public void SavedFilesReturnInOrderOnceWithTheSelectedFile()
    {
        string first = Path.Combine(_root, "日本語 'quoted'.pdf");
        string second = Path.Combine(_root, "second.pdf");
        Store().Stage([first, second], second);

        var restored = Assert.IsType<UpdateRestartSnapshot>(Store().TryClaim());

        Assert.Equal(new[] { first, second }, restored.Files);
        Assert.Equal(second, restored.ActiveFile);
        Assert.Null(Store().TryClaim());
    }

    [Fact]
    public void PathsAreNormalizedAndDistinctIgnoringCaseWithAnActiveFallback()
    {
        string first = Path.Combine(_root, "first.pdf");
        string second = Path.Combine(_root, "second.pdf");
        Store().Stage([Path.Combine(_root, "folder", "..", "first.pdf"), first.ToUpperInvariant(), second], "missing.pdf");
        var restored = Assert.IsType<UpdateRestartSnapshot>(Store().TryClaim());
        Assert.Equal(new[] { first, second }, restored.Files);
        Assert.Equal(first, restored.ActiveFile);
    }

    [Fact]
    public void EmptySnapshotIsDifferentFromNoPendingSnapshot()
    {
        Store().Stage([], null);
        var restored = Assert.IsType<UpdateRestartSnapshot>(Store().TryClaim());
        Assert.Empty(restored.Files);
        Assert.Null(restored.ActiveFile);
        Assert.Null(Store().TryClaim());
    }

    [Fact]
    public void DefaultProcessIdentityRetainsALiveSourceButAllowsAReusedPid()
    {
        var live = new UpdateRestartStore(_root, "install", "account", 3, () => _now);
        live.Stage([], null);
        string path = Pending();
        byte[] original = File.ReadAllBytes(path);
        Assert.Null(live.TryClaim());
        Assert.Equal(original, File.ReadAllBytes(path));

        ChangeRecord(record => record["SourceStartUtcTicks"] = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks + 1);
        Assert.NotNull(live.TryClaim());
    }

    [Fact]
    public void DefaultProcessIdentityAllowsANonexistentSource()
    {
        Store().Stage([], null);
        ChangeRecord(record => record["SourcePid"] = int.MaxValue);
        var reader = new UpdateRestartStore(_root, "install", "account", 3, () => _now);
        Assert.NotNull(reader.TryClaim());
    }

    [Theory]
    [InlineData("another-install", "account", 3)]
    [InlineData("install", "another-account", 3)]
    [InlineData("install", "account", 4)]
    public void OtherScopesCannotConsumeTheSnapshot(string installation, string account, int session)
    {
        Store().Stage([], null);
        string path = Pending();
        byte[] original = File.ReadAllBytes(path);
        Assert.Null(new UpdateRestartStore(_root, installation, account, session, () => _now, (_, _) => false).TryClaim());
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.NotNull(Store().TryClaim());
    }

    [Theory]
    [InlineData("version")]
    [InlineData("installation")]
    [InlineData("account")]
    [InlineData("session")]
    [InlineData("stale")]
    [InlineData("future")]
    [InlineData("malformed")]
    [InlineData("oversize")]
    [InlineData("relative-path")]
    [InlineData("missing-files")]
    [InlineData("invalid-source")]
    public void InvalidRecordsAreRejectedWithoutTouchingUnrelatedFiles(string damage)
    {
        Store().Stage([], null);
        string path = Pending();
        string sentinel = Path.Combine(_root, "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        switch (damage)
        {
            case "malformed": File.WriteAllText(path, "{broken"); break;
            case "oversize": File.WriteAllBytes(path, new byte[1024 * 1024 + 1]); break;
            default:
                ChangeRecord(record =>
                {
                    switch (damage)
                    {
                        case "version": record["Version"] = 2; break;
                        case "installation": record["InstallationIdentity"] = "other"; break;
                        case "account": record["AccountIdentity"] = "other"; break;
                        case "session": record["SessionId"] = 42; break;
                        case "stale": record["CreatedUtc"] = _now.AddHours(-25); break;
                        case "future": record["CreatedUtc"] = _now.AddMinutes(6); break;
                        case "relative-path": record["Files"] = new JsonArray("relative.pdf"); break;
                        case "missing-files": record.Remove("Files"); break;
                        case "invalid-source": record["SourcePid"] = 0; break;
                    }
                });
                break;
        }
        Assert.Null(Store().TryClaim());
        Assert.False(File.Exists(path));
        Assert.Equal("keep", File.ReadAllText(sentinel));
    }

    [Fact]
    public void StageNeverOverwritesPendingStateIncludingWhenNewStateIsTooLarge()
    {
        Store().Stage([], null);
        string path = Pending();
        byte[] original = File.ReadAllBytes(path);
        Assert.Throws<IOException>(() => Store().Stage([Path.Combine(_root, "new.pdf")], null));
        var oversized = Enumerable.Range(0, 2000)
            .Select(index => Path.Combine(_root, new string('\u65e5', 100) + index + ".pdf"));
        Assert.Throws<InvalidDataException>(() => Store().Stage(oversized, null));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.NotNull(Store().TryClaim());
    }

    [Fact]
    public void CancelRequiresTheMatchingTokenAndReportsDeletionFailure()
    {
        Guid token = Store().Stage([], null);
        string path = Pending();
        byte[] original = File.ReadAllBytes(path);
        Assert.False(Store().Cancel(Guid.NewGuid()));
        Assert.Equal(original, File.ReadAllBytes(path));
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Store().Cancel(token));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        Assert.True(Store().Cancel(token));
        Assert.False(Store().Cancel(token));
        Assert.Null(Store().TryClaim());
    }

    [Fact]
    public void CancelDoesNotRemoveARecordWithMismatchedScopeEvenWithItsToken()
    {
        Guid token = Store().Stage([], null);
        ChangeRecord(record => record["AccountIdentity"] = "other");
        string path = Pending();
        byte[] original = File.ReadAllBytes(path);
        Assert.False(Store().Cancel(token));
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void FailedClaimDeletionPreservesTheSnapshotForRetry()
    {
        Store().Stage([], null);
        string path = Pending();
        byte[] original = File.ReadAllBytes(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Store().TryClaim());
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
        Assert.NotNull(Store().TryClaim());
    }

    [Fact]
    public void FailedReadPreservesTheSnapshotForRetry()
    {
        Store().Stage([], null);
        string path = Pending();
        byte[] original = File.ReadAllBytes(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => Store().TryClaim());
        }
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.NotNull(Store().TryClaim());
    }

    [Fact]
    public void FailedWriteDoesNotDamageAnExistingFilesystemEntry()
    {
        Directory.CreateDirectory(_root);
        string blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "keep");
        var store = new UpdateRestartStore(blocked, "install", "account", 3);
        Assert.Throws<IOException>(() => store.Stage([], null));
        Assert.Equal("keep", File.ReadAllText(blocked));
    }

    [Fact]
    public void InvalidStoreRootIsReportedInsteadOfTreatedAsAnAbsentSnapshot()
    {
        Directory.CreateDirectory(_root);
        string blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "keep");
        var store = new UpdateRestartStore(blocked, "install", "account", 3);
        Assert.ThrowsAny<IOException>(() => store.TryClaim());
        Assert.ThrowsAny<IOException>(() => store.Cancel(Guid.NewGuid()));
        Assert.Equal("keep", File.ReadAllText(blocked));
    }

    [Fact]
    public async Task CompetingConsumersRestoreExactlyOnce()
    {
        Store().Stage([], null);
        using var start = new ManualResetEventSlim();
        Task<UpdateRestartSnapshot?> Read() => Task.Run(() => { start.Wait(); return Store().TryClaim(); });
        Task<UpdateRestartSnapshot?> first = Read();
        Task<UpdateRestartSnapshot?> second = Read();
        start.Set();
        var snapshots = await Task.WhenAll(first, second);
        Assert.Single(snapshots, snapshot => snapshot is not null);
        Assert.Null(Store().TryClaim());
    }

    [Fact]
    public void AbandonedClaimNeverReplaysAndOnlyExpiresAfterItsLifetime()
    {
        Store().Stage([], null);
        string path = Pending();
        string claim = path + ".claim";
        File.Move(path, claim);
        byte[] original = File.ReadAllBytes(claim);
        Assert.Null(Store().TryClaim());
        Assert.Throws<IOException>(() => Store().Stage([], null));
        Assert.Equal(original, File.ReadAllBytes(claim));
        var later = new UpdateRestartStore(_root, "install", "account", 3, () => _now.AddHours(25), (_, _) => false);
        later.Stage([], null);
        Assert.NotNull(later.TryClaim());
        Assert.False(File.Exists(claim));
    }

    private string Pending() => Assert.Single(Directory.GetFiles(_root, "*.json"));

    private void ChangeRecord(Action<JsonObject> change)
    {
        string path = Pending();
        var record = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        change(record);
        File.WriteAllText(path, record.ToJsonString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
