using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class AtomicFileOutputTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompletedWritePublishesAllBytes(bool asynchronous, bool existing)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "output.pdf");
        if (existing) File.WriteAllText(path, "Previous");
        byte[] replacement = "Complete replacement"u8.ToArray();
        try
        {
            if (asynchronous) await AtomicFileOutput.WriteAsync(path, replacement);
            else AtomicFileOutput.Write(path, replacement);
            Assert.Equal(replacement, File.ReadAllBytes(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Fact]
    public void LockedDestinationSurvivesPublicationFailure()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "output.pdf");
        byte[] original = "Previous"u8.ToArray();
        File.WriteAllBytes(path, original);
        try
        {
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Exception? error = Record.Exception(() => AtomicFileOutput.Write(path, "Replacement"u8.ToArray()));
                Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
            }
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialWriteFailurePreservesDestinationAndCleansStaging(bool asynchronous)
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "output.pdf");
        byte[] original = "Existing destination"u8.ToArray();
        File.WriteAllBytes(path, original);
        try
        {
            if (asynchronous)
                await Assert.ThrowsAsync<IOException>(() => AtomicFileOutput.WriteAsync(path, async stream =>
                {
                    await stream.WriteAsync("Partial replacement"u8.ToArray());
                    throw new IOException("Injected failure after write.");
                }));
            else
                Assert.Throws<IOException>(() => AtomicFileOutput.Write(path, stream =>
                {
                    stream.Write("Partial replacement"u8);
                    throw new IOException("Injected failure after write.");
                }));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Fact]
    public async Task CancellationAfterAsyncWritePreservesDestination()
    {
        string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "output.pdf");
        byte[] original = "Existing destination"u8.ToArray();
        File.WriteAllBytes(path, original);
        using var cancellation = new CancellationTokenSource();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AtomicFileOutput.WriteAsync(path, async stream =>
            {
                await stream.WriteAsync("Partial replacement"u8.ToArray());
                cancellation.Cancel();
            }, cancellation.Token));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(new[] { path }, Directory.GetFiles(directory));
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }
}
