using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ReplaceFileAtomicallyTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kp-replace-" + Guid.NewGuid().ToString("N"));

    public ReplaceFileAtomicallyTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    [Fact]
    public void ReplacesAnExistingDestinationAndLeavesNoTemporaryFile()
    {
        string finished = Path.Combine(_folder, "finished.pdf");
        string destination = Path.Combine(_folder, "document.pdf");
        File.WriteAllText(finished, "new");
        File.WriteAllText(destination, "old");

        PdfEngineIntegration.ReplaceFileAtomically(finished, destination);

        Assert.Equal("new", File.ReadAllText(destination));
        Assert.Equal("new", File.ReadAllText(finished));
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public void CreatesADestinationThatDoesNotExistYet()
    {
        string finished = Path.Combine(_folder, "finished.pdf");
        string destination = Path.Combine(_folder, "document.pdf");
        File.WriteAllText(finished, "new");

        PdfEngineIntegration.ReplaceFileAtomically(finished, destination);

        Assert.Equal("new", File.ReadAllText(destination));
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }

    [Fact]
    public void KeepsThePreviousDestinationWhenTheFinishedFileIsMissing()
    {
        string destination = Path.Combine(_folder, "document.pdf");
        File.WriteAllText(destination, "old");

        Assert.Throws<FileNotFoundException>(() => PdfEngineIntegration.ReplaceFileAtomically(
            Path.Combine(_folder, "missing.pdf"), destination));

        Assert.Equal("old", File.ReadAllText(destination));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void KeepsThePreviousDestinationWhenItIsLockedByAnotherProgram()
    {
        string finished = Path.Combine(_folder, "finished.pdf");
        string destination = Path.Combine(_folder, "document.pdf");
        File.WriteAllText(finished, "new");
        File.WriteAllText(destination, "old");

        using (new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<Exception>(() =>
                PdfEngineIntegration.ReplaceFileAtomically(finished, destination));
        }

        Assert.Equal("old", File.ReadAllText(destination));
        Assert.Equal(2, Directory.GetFiles(_folder).Length);
    }
    [Theory]
    [InlineData(1176)]
    [InlineData(1177)]
    public void PartialReplacementKeepsTheFinishedImageAfterTheCallerDeletesItsTemporary(int nativeError)
    {
        string finished = Path.Combine(_folder, "finished.pdf");
        string destination = Path.Combine(_folder, "document.pdf");
        File.WriteAllText(finished, "Complete replacement");
        File.WriteAllText(destination, "Original");
        string? staged = null;
        var files = new AtomicFileOutput.FileOperations
        {
            Replace = (source, target, backup) =>
            {
                staged = source;
                if (nativeError == 1177) File.Move(target, backup ?? target + ".native-backup");
                else File.Delete(target);
                throw new IOException("Partial replacement.", unchecked((int)(0x80070000u + nativeError)));
            },
            Move = (source, target) => throw new IOException("Publication and restoration are unavailable.")
        };
        Exception? error;
        try { error = Record.Exception(() => PdfEngineIntegration.ReplaceFileAtomically(finished, destination, files)); }
        finally { File.Delete(finished); }
        Assert.IsAssignableFrom<IOException>(error);
        Assert.True(File.Exists(staged), "The caller's finished temporary is gone, so the complete sibling must be retained.");
        Assert.Equal("Complete replacement", File.ReadAllText(staged!));
        Assert.Contains(staged!, error!.Message);
    }
}
