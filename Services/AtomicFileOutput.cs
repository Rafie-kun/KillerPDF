using System.IO;

namespace KillerPDF.Services;

internal static class AtomicFileOutput
{
    internal static void Write(string destination, ReadOnlyMemory<byte> bytes, CancellationToken cancellation = default)
        => Write(destination, stream => stream.Write(bytes.Span), cancellation);

    internal static void Write(string destination, Action<Stream> write, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        string temporary = TemporaryPath(destination);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            Publish(temporary, destination, cancellation);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static Task WriteAsync(string destination, ReadOnlyMemory<byte> bytes, CancellationToken cancellation = default)
        => WriteAsync(destination, stream => stream.WriteAsync(bytes, cancellation).AsTask(), cancellation);

    internal static async Task WriteAsync(string destination, Func<Stream, Task> write, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        string temporary = TemporaryPath(destination);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
            {
                await write(stream).ConfigureAwait(false);
                await stream.FlushAsync(cancellation).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            Publish(temporary, destination, cancellation);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string TemporaryPath(string destination) => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(destination))!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

    private static void Publish(string temporary, string destination, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if (File.Exists(destination))
        {
            // Match the existing replacement policy, using the already completed sibling file.
            try { File.Replace(temporary, destination, null, ignoreMetadataErrors: true); return; }
            catch (IOException) { }
            catch (PlatformNotSupportedException) { }
        }
        cancellation.ThrowIfCancellationRequested();
        File.Move(temporary, destination, overwrite: true);
    }
}
