using System.IO;

namespace KillerPDF.Services;

internal static class AtomicFileOutput
{
    internal sealed class FileOperations
    {
        internal Action<string, string, string?> Replace = (source, destination, backup) => File.Replace(source, destination, backup, ignoreMetadataErrors: true);
        internal Action<string, string> Move = File.Move;
        internal Action<string> Delete = File.Delete;
    }

    internal static void Write(string destination, ReadOnlyMemory<byte> bytes, CancellationToken cancellation = default)
        => Write(destination, stream => stream.Write(bytes.Span), cancellation);

    internal static void Write(string destination, Action<Stream> write, CancellationToken cancellation = default)
        => Write(destination, write, cancellation, new FileOperations());

    internal static void Write(string destination, Action<Stream> write, CancellationToken cancellation, FileOperations files)
    {
        cancellation.ThrowIfCancellationRequested();
        string temporary = TemporaryPath(destination);
        bool retainTemporary = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            Publish(temporary, destination, cancellation, files, ref retainTemporary);
        }
        finally { if (!retainTemporary) TryDelete(temporary, files); }
    }

    internal static Task WriteAsync(string destination, ReadOnlyMemory<byte> bytes, CancellationToken cancellation = default)
        => WriteAsync(destination, stream => stream.WriteAsync(bytes, cancellation).AsTask(), cancellation);

    internal static Task WriteAsync(string destination, Func<Stream, Task> write, CancellationToken cancellation = default)
        => WriteAsync(destination, write, cancellation, new FileOperations());

    internal static async Task WriteAsync(string destination, Func<Stream, Task> write, CancellationToken cancellation, FileOperations files)
    {
        cancellation.ThrowIfCancellationRequested();
        string temporary = TemporaryPath(destination);
        bool retainTemporary = false;
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
            {
                await write(stream).ConfigureAwait(false);
                await stream.FlushAsync(cancellation).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            Publish(temporary, destination, cancellation, files, ref retainTemporary);
        }
        finally { if (!retainTemporary) TryDelete(temporary, files); }
    }

    private static string TemporaryPath(string destination) => Path.Combine(
        Path.GetDirectoryName(Path.GetFullPath(destination))!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

    private static void Publish(string temporary, string destination, CancellationToken cancellation, FileOperations files, ref bool retainTemporary)
    {
        string backup = TemporaryPath(destination) + ".bak";
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(destination))
            {
                // Replace preserves destination metadata. A unique backup also survives partial
                // native failures that remove the old destination name before returning an error.
                try
                {
                    files.Replace(temporary, destination, backup);
                    TryDelete(backup, files);
                    return;
                }
                catch (IOException) { retainTemporary = File.Exists(backup) || !File.Exists(destination); }
                catch (PlatformNotSupportedException) { }
            }
            cancellation.ThrowIfCancellationRequested();
            if (File.Exists(destination))
            {
                if (File.Exists(backup)) { retainTemporary = true; throw new IOException("The replacement left both destination and backup files."); }
                files.Move(destination, backup);
                retainTemporary = true;
            }
            files.Move(temporary, destination);
            retainTemporary = false;
            TryDelete(backup, files);
        }
        catch (Exception publicationError)
        {
            if (!File.Exists(destination))
            {
                retainTemporary = true;
                string recovery = File.Exists(backup) ? backup : temporary;
                try
                {
                    string restored = TemporaryPath(destination) + ".restore";
                    File.Copy(recovery, restored);
                    files.Move(restored, destination);
                    if (recovery == temporary && publicationError is not OperationCanceledException)
                    {
                        retainTemporary = false;
                        return;
                    }
                }
                catch (Exception recoveryError)
                {
                    throw RecoveryError(temporary, backup, new AggregateException(publicationError, recoveryError), cancellation);
                }
            }
            if (retainTemporary) throw RecoveryError(temporary, backup, publicationError, cancellation);
            throw;
        }
    }

    private static Exception RecoveryError(string temporary, string backup, Exception error, CancellationToken cancellation)
    {
        string message = "The file could not be published. Recovery files were kept. Staged: " + temporary + ". Backup: " + backup;
        return error is OperationCanceledException
            ? new OperationCanceledException(message, error, cancellation)
            : new IOException(message, error);
    }

    private static void TryDelete(string path, FileOperations files)
    {
        try { files.Delete(path); } catch { }
    }
}
