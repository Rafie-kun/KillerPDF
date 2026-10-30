using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// A source whose pages are fine but whose outline is damaged still merges, without its bookmarks,
// the same way the other page-copy paths in PdfEngineIntegration carry on (TX-013).
public sealed class MergeBookmarkFallbackTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "killerpdf-merge-bookmarks-" + Guid.NewGuid().ToString("N"));

    public MergeBookmarkFallbackTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MergeFiles_KeepsThePagesOfASourceWithADamagedOutline(bool damagedFirst)
    {
        string healthy = Write("healthy.pdf", new PdfDocumentBuilder().AddBlankPage().Build());
        string damaged = Write("damaged.pdf", DamagedOutline());

        byte[] merged = PdfEngineIntegration.MergeFiles(damagedFirst ? [damaged, healthy] : [healthy, damaged]);

        Assert.Equal(3, PdfPageInformation.Read(PdfDocument.Open(merged)).Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MergeDocuments_KeepsThePagesOfASourceWithADamagedOutline(bool damagedFirst)
    {
        byte[] healthy = new PdfDocumentBuilder().AddBlankPage().Build();
        byte[] damaged = DamagedOutline();

        byte[] merged = PdfEngineIntegration.MergeDocuments(damagedFirst ? [damaged, healthy] : [healthy, damaged]);

        Assert.Equal(3, PdfPageInformation.Read(PdfDocument.Open(merged)).Count);
    }

    // One damaged outline costs only that source its bookmarks, and the caller is told which (F3-004).
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MergeFiles_KeepsTheBookmarksOfHealthySourcesAndNamesTheDamagedOne(int damagedIndex)
    {
        string[] paths =
        [
            Write("alpha.pdf", Bookmarked("Alpha chapter")),
            Write("beta.pdf", Bookmarked("Beta chapter")),
            Write("gamma.pdf", Bookmarked("Gamma chapter"))
        ];
        string[] healthyTitles = [.. new[] { "Alpha chapter", "Beta chapter", "Gamma chapter" }.Where((_, index) => index != damagedIndex)];
        paths[damagedIndex] = Write("damaged.pdf", DamagedOutline());

        byte[] merged = PdfEngineIntegration.MergeFiles(paths, out IReadOnlyList<string> warnings);

        Assert.Equal(4, PdfPageInformation.Read(PdfDocument.Open(merged)).Count);
        Assert.Equal(healthyTitles, Titles(merged));
        string warning = Assert.Single(warnings);
        Assert.Contains(paths[damagedIndex], warning);
    }

    [Fact]
    public void MergeFiles_DropsEachDamagedOutlineSeparately()
    {
        string[] paths =
        [
            Write("one.pdf", DamagedOutline()),
            Write("two.pdf", Bookmarked("Healthy chapter")),
            Write("three.pdf", DamagedOutline())
        ];

        byte[] merged = PdfEngineIntegration.MergeFiles(paths, out IReadOnlyList<string> warnings);

        Assert.Equal(["Healthy chapter"], Titles(merged));
        Assert.Equal(2, warnings.Count);
        Assert.Contains(paths[0], warnings[0]);
        Assert.Contains(paths[2], warnings[1]);
    }

    // A folder or archive import skips unreadable entries, but a damaged outline leaves the pages readable.
    [Fact]
    public void MergeReadableFiles_KeepsThePagesOfASourceWithADamagedOutline()
    {
        string[] paths = [Write("healthy.pdf", Bookmarked("Healthy chapter")), Write("damaged.pdf", DamagedOutline())];

        byte[] merged = PdfEngineIntegration.MergeReadableFiles(paths);

        Assert.Equal(3, PdfPageInformation.Read(PdfDocument.Open(merged)).Count);
        Assert.Equal(["Healthy chapter"], Titles(merged));
    }

    [Fact]
    public void MergeFiles_ReportsNothingWhenEveryOutlineIsHealthy()
    {
        string[] paths = [Write("a.pdf", Bookmarked("Alpha chapter")), Write("b.pdf", Bookmarked("Beta chapter"))];

        byte[] merged = PdfEngineIntegration.MergeFiles(paths, out IReadOnlyList<string> warnings);

        Assert.Equal(["Alpha chapter", "Beta chapter"], Titles(merged));
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void MergeDocuments_KeepsTheBookmarksOfHealthySourcesAndNamesTheDamagedOne(int damagedIndex)
    {
        byte[][] sources = [Bookmarked("Alpha chapter"), Bookmarked("Beta chapter"), Bookmarked("Gamma chapter")];
        string[] healthyTitles = [.. new[] { "Alpha chapter", "Beta chapter", "Gamma chapter" }.Where((_, index) => index != damagedIndex)];
        sources[damagedIndex] = DamagedOutline();

        byte[] merged = PdfEngineIntegration.MergeDocuments(sources, out IReadOnlyList<string> warnings);

        Assert.Equal(healthyTitles, Titles(merged));
        Assert.Contains($"document {damagedIndex + 1}", Assert.Single(warnings));
    }

    private static byte[] Bookmarked(string title) =>
        new PdfDocumentBuilder().AddBlankPage().AddBookmark(title, 0).Build();

    private static string[] Titles(byte[] pdf) =>
        [.. PdfBookmarkReader.Read(PdfDocument.Open(pdf)).Select(bookmark => bookmark.Title)];

    // Two pages and two top-level bookmarks, with the second bookmark's /Prev pointing at the wrong object.
    private static byte[] DamagedOutline()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().AddBlankPage()
            .AddBookmark("One", 0).AddBookmark("Two", 1).Build();
        string text = Encoding.Latin1.GetString(source);
        Match prev = Regex.Match(text, @"/Prev (\d+) 0 R");
        Assert.True(prev.Success);
        // The first object is the catalog or a page, never an outline item, so the link cannot be reciprocal.
        string wrong = "/Prev " + new string('1', 1).PadLeft(prev.Groups[1].Length, '0') + " 0 R";
        text = text.Remove(prev.Index, prev.Length).Insert(prev.Index, wrong);
        return Encoding.Latin1.GetBytes(text);
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
