using System.IO;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// Merge and the form handlers read pages from a file the viewer already shows, so they read it the
// way the viewer does (TX-005, TX-012).
public sealed class PageSourceRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "killerpdf-page-source-" + Guid.NewGuid().ToString("N"));

    public PageSourceRecoveryTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void ReadPageInformation_ReadsAFileOnlyRecoveryCanOpen()
    {
        string path = Write("damaged.pdf", DamagedTwoPagePdf());

        IReadOnlyList<PdfPageInformation> pages = PdfEngineIntegration.ReadPageInformation(path);

        Assert.Equal(2, pages.Count);
        Assert.Equal(200, pages[0].Width);
    }

    [Fact]
    public void ValidateDocument_AcceptsAFileOnlyRecoveryCanOpen()
    {
        string path = Write("damaged.pdf", DamagedTwoPagePdf());

        PdfEngineIntegration.ValidateDocument(path);
    }

    [Fact]
    public void ValidateDocument_StillRefusesAFileNoReaderCanOpen()
    {
        string path = Write("junk.pdf", Encoding.ASCII.GetBytes("This is not a PDF."));

        Assert.ThrowsAny<Exception>(() => PdfEngineIntegration.ValidateDocument(path));
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
