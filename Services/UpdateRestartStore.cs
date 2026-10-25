using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace KillerPDF.Services;

internal sealed record UpdateRestartSnapshot(string[] Files, string? ActiveFile);

internal sealed class UpdateRestartStore
{
    private const int MaxBytes = 1024 * 1024;
    private readonly string _directory;
    private readonly string _installation;
    private readonly string _account;
    private readonly int _session;
    private readonly string _pending;
    private readonly string _claim;
    private readonly string _lock;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<int, long, bool> _isProcessAlive;

    public UpdateRestartStore(string directory, string installationIdentity, string accountIdentity,
        int sessionId, Func<DateTimeOffset>? now = null, Func<int, long, bool>? isProcessAlive = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountIdentity);
        _directory = Path.GetFullPath(directory);
        _installation = installationIdentity;
        _account = accountIdentity;
        _session = sessionId;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _isProcessAlive = isProcessAlive ?? IsProcessAlive;
        string scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new object[] { installationIdentity, accountIdentity, sessionId }))));
        _pending = Path.Combine(_directory, "update-restart-v1-" + scope + ".json");
        _claim = _pending + ".claim";
        _lock = _pending + ".lock";
    }

    public Guid Stage(IEnumerable<string> files, string? activeFile)
    {
        ArgumentNullException.ThrowIfNull(files);
        var paths = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long characters = 0;
        foreach (string file in files)
        {
            string path = Path.GetFullPath(file);
            if (!seen.Add(path)) continue;
            characters += path.Length;
            if (characters > MaxBytes) throw new InvalidDataException("The update restart record is too large.");
            paths.Add(path);
        }
        string? active = activeFile is null ? null : Path.GetFullPath(activeFile);
        active = paths.FirstOrDefault(path => string.Equals(path, active, StringComparison.OrdinalIgnoreCase))
            ?? paths.FirstOrDefault();
        using var process = Process.GetCurrentProcess();
        Guid token = Guid.NewGuid();
        var record = new RestartRecord(1, _installation, _account, _session, token,
            process.Id, process.StartTime.ToUniversalTime().Ticks, _now(), paths.ToArray(), active);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(record);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("The update restart record is too large.");

        Directory.CreateDirectory(_directory);
        using var gate = AcquireLock();
        if (!RemoveExpiredClaim()) throw new IOException("An update restart claim is still pending.");
        bool created = false;
        try
        {
            using var output = new FileStream(_pending, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough);
            created = true;
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }
        catch (Exception writeError)
        {
            if (created)
            {
                try { File.Delete(_pending); }
                catch (Exception cleanupError) { throw new AggregateException(writeError, cleanupError); }
            }
            throw;
        }
        return token;
    }

    public UpdateRestartSnapshot? TryClaim()
    {
        if (!StoreDirectoryExists()) return null;
        using var gate = AcquireLock();
        if (!RemoveExpiredClaim()) return null;
        RestartRecord? record = ReadRecord(_pending, out bool exists);
        if (!exists) return null;
        if (!IsValid(record))
        {
            File.Delete(_pending);
            return null;
        }
        if (_isProcessAlive(record!.SourcePid, record.SourceStartUtcTicks)) return null;

        // A crash after this rename leaves a claim that cannot be replayed.
        File.Move(_pending, _claim);
        try { File.Delete(_claim); }
        catch (Exception deleteError)
        {
            try { File.Move(_claim, _pending); }
            catch (Exception recoveryError) { throw new AggregateException(deleteError, recoveryError); }
            throw;
        }
        return new UpdateRestartSnapshot(record.Files!, record.ActiveFile);
    }

    public bool Cancel(Guid token)
    {
        if (!StoreDirectoryExists()) return false;
        using var gate = AcquireLock();
        RestartRecord? record = ReadRecord(_pending, out _);
        if (record is null || token == Guid.Empty || record.Token != token
            || record.Version != 1 || record.InstallationIdentity != _installation
            || record.AccountIdentity != _account || record.SessionId != _session) return false;
        File.Delete(_pending);
        return true;
    }

    private bool StoreDirectoryExists()
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(_directory); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & FileAttributes.Directory) == 0)
            throw new IOException("The update restart storage path is not a directory.");
        return true;
    }

    private FileStream AcquireLock()
    {
        // Only retry actual sharing conflicts. Permission and storage errors remain checked failures.
        for (int attempt = 0; ; attempt++)
        {
            try { return new FileStream(_lock, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 && attempt < 200)
            {
                Thread.Sleep(10);
            }
        }
    }

    private RestartRecord? ReadRecord(string path, out bool exists)
    {
        exists = false;
        FileStream input;
        try { input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return null; }
        using (input)
        {
            exists = true;
            if (input.Length > MaxBytes) return null;
            byte[] bytes = new byte[(int)input.Length];
            input.ReadExactly(bytes);
            try { return JsonSerializer.Deserialize<RestartRecord>(bytes); }
            catch (JsonException) { return null; }
        }
    }

    private bool RemoveExpiredClaim()
    {
        RestartRecord? record = ReadRecord(_claim, out bool exists);
        if (!exists) return true;
        if (IsValid(record)) return false;
        File.Delete(_claim);
        return true;
    }

    private bool IsValid(RestartRecord? record)
    {
        if (record is null || record.Version != 1 || record.InstallationIdentity != _installation
            || record.AccountIdentity != _account || record.SessionId != _session
            || record.Token == Guid.Empty || record.SourcePid <= 0 || record.SourceStartUtcTicks <= 0
            || record.Files is null) return false;
        var age = _now() - record.CreatedUtc;
        if (age > TimeSpan.FromHours(24) || age < -TimeSpan.FromMinutes(5)) return false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in record.Files)
        {
            try
            {
                if (!Path.IsPathFullyQualified(path) || Path.GetFullPath(path) != path || !seen.Add(path)) return false;
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
            catch (PathTooLongException) { return false; }
        }
        return record.Files.Length == 0 ? record.ActiveFile is null
            : record.Files.Contains(record.ActiveFile, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsProcessAlive(int pid, long startUtcTicks)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == startUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private sealed record RestartRecord(int Version, string InstallationIdentity, string AccountIdentity,
        int SessionId, Guid Token, int SourcePid, long SourceStartUtcTicks, DateTimeOffset CreatedUtc,
        string[]? Files, string? ActiveFile);
}
