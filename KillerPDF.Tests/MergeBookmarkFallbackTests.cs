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
