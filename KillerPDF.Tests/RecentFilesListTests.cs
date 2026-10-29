using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The recent files list compared paths as plain text, so a file opened once as C:\a\b.pdf and once
/// as C:/a/b.pdf (a script, Git Bash, another program) showed up twice on the start screen.
/// </summary>
public sealed class RecentFilesListTests
{
    [Fact]
    public void TheSameFileWithForwardSlashesDoesNotAddASecondEntry()
    {
        var list = KillerPDF.Services.RecentFilesList.With(new[] { @"C:\Docs\Guide.pdf", @"C:\Docs\Letter.pdf" }, "C:/Docs/Guide.pdf", 10);
        Assert.Equal(new[] { @"C:\Docs\Guide.pdf", @"C:\Docs\Letter.pdf" }, list);
    }

    [Fact]
    public void StoredForwardSlashEntriesMergeWithTheirBackslashTwin()
    {
        var list = KillerPDF.Services.RecentFilesList.With(new[] { @"C:\Docs\Letter.pdf", "C:/Docs/Guide.pdf" }, @"c:\docs\guide.pdf", 10);
        Assert.Equal(new[] { @"c:\docs\guide.pdf", @"C:\Docs\Letter.pdf" }, list);
    }

    [Fact]
    public void TheListStaysCapped()
    {
        var list = KillerPDF.Services.RecentFilesList.With(new[] { @"C:\1.pdf", @"C:\2.pdf", @"C:\3.pdf" }, @"C:\4.pdf", 3);
        Assert.Equal(new[] { @"C:\4.pdf", @"C:\1.pdf", @"C:\2.pdf" }, list);
    }
}
