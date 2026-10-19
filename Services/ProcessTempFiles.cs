using System.IO;

namespace KillerPDF.Services;

internal sealed class ProcessTempFiles : IDisposable
{
    private const string LeaseName = "owner.lease";
    private readonly string root;
    private readonly List<string> paths = [];
    private readonly Lazy<(string Directory, FileStream Lease)> owned;
    private bool disposed;

    public ProcessTempFiles(string root)
    {
        this.root = Path.GetFullPath(root);
        owned = new(() =>
        {
            string directory = Path.Combine(this.root, "session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var lease = new FileStream(Path.Combine(directory, LeaseName),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            return (directory, lease);
        });
    }

    public string DirectoryPath
    {
        get
        {
            lock (paths)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return owned.Value.Directory;
            }
        }
    }

    public string CreatePath(string tag)
    {
        lock (paths)
        {
            string path = Path.Combine(DirectoryPath, $"killerpdf_{tag}_{Guid.NewGuid():N}.pdf");
            paths.Add(path);
            return path;
        }
    }

    public void CleanupOwned()
    {
        lock (paths)
        {
            foreach (string path in paths)
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            paths.Clear();
        }
    }

    public void CleanupStale()
    {
        try
        {
            if (!Directory.Exists(root)
                || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
            foreach (string directory in Directory.GetDirectories(root, "session-*"))
            {
                try
                {
                    string name = Path.GetFileName(directory);
                    if (!Guid.TryParseExact(name.AsSpan("session-".Length), "N", out _)
                        || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    string leasePath = Path.Combine(directory, LeaseName);
                    if (!File.Exists(leasePath)
                        || (File.GetAttributes(leasePath) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    // An active process holds this handle exclusively for its whole lifetime.
                    using (var lease = new FileStream(leasePath, FileMode.Open,
                        FileAccess.ReadWrite, FileShare.None))
                    {
                        foreach (string file in Directory.GetFiles(directory, "killerpdf_*.pdf"))
                            try { File.Delete(file); } catch { }
                    }
                    File.Delete(leasePath);
                    // Unknown files prevent removal instead of being recursively deleted.
                    Directory.Delete(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (paths)
        {
            if (disposed) return;
            CleanupOwned();
            if (owned.IsValueCreated) owned.Value.Lease.Dispose();
            disposed = true;
        }
    }
}
