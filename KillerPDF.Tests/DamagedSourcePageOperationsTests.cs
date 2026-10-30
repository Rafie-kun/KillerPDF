using System.IO;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// A file the viewer opens through compatibility recovery must also work as a page source for
// Extract, Split, Merge and Rebuild, instead of failing with a damaged-structure error (#406).
public sealed class DamagedSourcePageOperationsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "killerpdf-damaged-source-" + Guid.NewGuid().ToString("N"));

    public DamagedSourcePageOperationsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    // Save, Save As, Print, Extract and Reduce all start here, so a file the viewer shows has to
    // come out strict-readable or none of them can finish.
    [Fact]
    public void TheSavePipelineNormalizesAFileTheViewerRecovers()
    {
        string path = Write("damaged.pdf", DamagedTwoPagePdf());

        PdfEngineIntegration.RepairHarmlessSaveArtifacts(path);

        Assert.Equal(2, PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(path))).Count);
    }

    [Fact]
    public void TheSavePipelineLeavesAHealthyFileAlone()
    {
        byte[] healthy = new PdfDocumentBuilder().AddBlankPage().Build();
        string path = Write("healthy.pdf", healthy);

        PdfEngineIntegration.RepairHarmlessSaveArtifacts(path);

        Assert.Equal(healthy, File.ReadAllBytes(path));
    }

    [Fact]
    public void ExtractPages_ReadsASourceTheViewerRecovers()
    {
        byte[] damaged = DamagedTwoPagePdf();

        byte[] extracted = PdfEngineIntegration.ExtractPages(damaged, [1]);

        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(extracted)));
    }

    [Fact]
    public void SplitPages_ReadsASourceTheViewerRecovers()
    {
        byte[] damaged = DamagedTwoPagePdf();

        IReadOnlyList<byte[]> pages = PdfEngineIntegration.SplitPages(damaged);

        Assert.Equal(2, pages.Count);
        Assert.All(pages, page => Assert.Single(PdfPageInformation.Read(PdfDocument.Open(page))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MergeFiles_ReadsASourceTheViewerRecoversInEitherPosition(bool damagedFirst)
    {
        string damaged = Write("damaged.pdf", DamagedTwoPagePdf());
        string healthy = Write("healthy.pdf", new PdfDocumentBuilder().AddBlankPage().Build());
        byte[] original = File.ReadAllBytes(damaged);

        byte[] merged = PdfEngineIntegration.MergeFiles(damagedFirst ? [damaged, healthy] : [healthy, damaged]);

        Assert.Equal(3, PdfPageInformation.Read(PdfDocument.Open(merged)).Count);
        Assert.Equal(original, File.ReadAllBytes(damaged));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MergeDocuments_ReadsASourceTheViewerRecoversInEitherPosition(bool damagedFirst)
    {
        byte[] damaged = DamagedTwoPagePdf();
        byte[] healthy = new PdfDocumentBuilder().AddBlankPage().Build();

        byte[] merged = PdfEngineIntegration.MergeDocuments(damagedFirst ? [damaged, healthy] : [healthy, damaged]);

        Assert.Equal(3, PdfPageInformation.Read(PdfDocument.Open(merged)).Count);
    }

    [Fact]
    public void MergeFiles_KeepsAHealthyFirstFileAsTheUnchangedPrefix()
    {
        byte[] first = new PdfDocumentBuilder().AddBlankPage().Build();
        string healthy = Write("first.pdf", first);
        string damaged = Write("damaged.pdf", DamagedTwoPagePdf());

        byte[] merged = PdfEngineIntegration.MergeFiles([healthy, damaged]);

        Assert.True(merged.AsSpan(0, first.Length).SequenceEqual(first));
    }

    [Fact]
    public void RebuildDocument_ReadsASourceTheViewerRecovers()
    {
        string damaged = Write("damaged.pdf", DamagedTwoPagePdf());
        string rebuilt = Path.Combine(_directory, "rebuilt.pdf");

        PdfEngineIntegration.RebuildDocument(damaged, rebuilt);

        Assert.Equal(2, PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(rebuilt))).Count);
    }

    // A valid two-page file whose startxref points past the end, which the strict parser refuses
    // and compatibility recovery rebuilds. The asserts keep the fixture honest.
    private static byte[] DamagedTwoPagePdf()
    {
        byte[] valid = new PdfDocumentBuilder().AddBlankPage(200, 300).AddBlankPage(300, 200).Build();
        string text = Encoding.Latin1.GetString(valid);
        int marker = text.LastIndexOf("startxref", StringComparison.Ordinal);
        int numberStart = text.IndexOf('\n', marker) + 1;
        int numberEnd = text.IndexOf('\n', numberStart);
        byte[] damaged = Encoding.Latin1.GetBytes(
            text[..numberStart] + (valid.Length + 5000).ToString() + text[numberEnd..]);
        Assert.ThrowsAny<Exception>(() => PdfDocument.Open(damaged));
        Assert.Equal(2, PdfPageInformation.Read(PdfDocument.OpenWithCompatibilityRecovery(damaged)).Count);
        return damaged;
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
