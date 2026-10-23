using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfRenderCompletenessTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidatedPageStructureRecovery_PreservesIntactPixelsAndEncoding(bool removeCatalog)
    {
        byte[] intact = new PdfDocumentBuilder().AddPage(32, 32,
            "1 0 0 rg 4 4 24 24 re f"u8.ToArray()).Build();
        PdfDocument document = PdfDocument.Open(intact);
        var catalogReference = Assert.IsType<PdfIndirectReference>(document.Trailer[new PdfName("Root"u8)]);
        var catalog = Assert.IsType<PdfDictionary>(document.Resolve(catalogReference));
        var pagesReference = Assert.IsType<PdfIndirectReference>(catalog[new PdfName("Pages"u8)]);
        int damagedObject = removeCatalog ? catalogReference.ObjectNumber : pagesReference.ObjectNumber;
        string damaged = Regex.Replace(Encoding.Latin1.GetString(intact),
            $@"(?m)^{damagedObject} 0 obj\b[\s\S]*?endobj", match => new string(' ', match.Length));
        Assert.NotEqual(intact, Encoding.Latin1.GetBytes(damaged));
        string intactPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        string damagedPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(intactPath, intact);
            File.WriteAllBytes(damagedPath, Encoding.Latin1.GetBytes(damaged));
            using var clean = PdfPageRenderSession.OpenEngineFirst(intactPath, 32, 32);
            using var recovered = PdfPageRenderSession.OpenEngineFirst(damagedPath, 32, 32);
            byte[] expected = clean.RenderPage(0).Pixels;
            Assert.Contains(expected, component => component < 128);
            PdfRenderedPage preview = recovered.RenderFittedPage(0, 32, 32);
            Assert.Equal(expected, preview.Pixels);
            Assert.Contains(preview.RenderDiagnostics, diagnostic => diagnostic.Message ==
                "Invalid catalog or page-tree links were recovered from intact indirect page objects.");
            Assert.Equal(expected, recovered.RenderPageForEncoding(0).Pixels.ToArray());
            Assert.Equal(expected, recovered.RenderPage(0).Pixels);
        }
        finally { File.Delete(intactPath); File.Delete(damagedPath); }
    }

    [Theory]
    [InlineData("q UnsupportedPaint Q")]
    [InlineData("q /Missing Do Q")]
    public void RecoveredPageStructureWithOmissions_RemainsUnpublishable(string content)
    {
        byte[] intact = new PdfDocumentBuilder().AddPage(32, 32, Encoding.ASCII.GetBytes(content)).Build();
        PdfDocument document = PdfDocument.Open(intact);
        var catalogReference = Assert.IsType<PdfIndirectReference>(document.Trailer[new PdfName("Root"u8)]);
        string damaged = Regex.Replace(Encoding.Latin1.GetString(intact),
            $@"(?m)^{catalogReference.ObjectNumber} 0 obj\b[\s\S]*?endobj", match => new string(' ', match.Length));
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, Encoding.Latin1.GetBytes(damaged));
            using var session = PdfPageRenderSession.OpenEngineFirst(path, 32, 32);
            PdfRenderedPage preview = session.RenderFittedPage(0, 32, 32);
            Assert.Contains(preview.RenderDiagnostics, item => item.Kind == PdfRenderDiagnosticKind.Recovery);
            Assert.Contains(preview.RenderDiagnostics, item => item.Kind == PdfRenderDiagnosticKind.OmittedContent);
            Assert.Throws<InvalidOperationException>(() => session.RenderPageForEncoding(0));
            Assert.Throws<InvalidOperationException>(() => session.RenderPage(0));
            Assert.Throws<InvalidOperationException>(() => session.RenderBasePage(0));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("q /Missing Do Q")]
    [InlineData("q UnsupportedPaint Q")]
    public void OmittedContent_BlocksExportOcrAndEncodingButPreservesPreviewDiagnostics(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(120, 80, Encoding.ASCII.GetBytes(content)).Build());
            using var session = PdfPageRenderSession.OpenEngineFirst(path, 1.0);
            PdfRenderedPage preview = session.RenderFittedPage(0, 120, 80);
            Assert.Contains(preview.RenderDiagnostics, item => item.Kind == PdfRenderDiagnosticKind.OmittedContent);
            Assert.Contains("Page 1", Assert.Throws<InvalidOperationException>(() => preview.RequireComplete(0)).Message);
            Assert.Throws<InvalidOperationException>(() => session.RenderPage(0));
            Assert.Throws<InvalidOperationException>(() => session.RenderBasePage(0));
            Assert.Throws<InvalidOperationException>(() => session.RenderPageForEncoding(0));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RecoveredStandardFont_RemainsPublishable()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(120, 80,
                "BT /Helvetica 14 Tf 10 30 Td (Recovered) Tj ET"u8.ToArray()).Build());
            using var session = PdfPageRenderSession.OpenEngineFirst(path, 1.0);
            PdfRenderedPage result = session.RenderPage(0);
            Assert.Contains(result.RenderDiagnostics, item => item.Kind == PdfRenderDiagnosticKind.Recovery);
            Assert.DoesNotContain(result.RenderDiagnostics, item => item.BlocksPublication);
            Assert.Contains(result.Pixels, component => component < 128);
        }
        finally { File.Delete(path); }
    }

    // Thumbnails are for display, like the viewer. A page that cannot be published still shows
    // what rendered instead of leaving the page list with a label-only tile.
    [Theory]
    [InlineData("q /Missing Do Q")]
    [InlineData("q UnsupportedPaint Q")]
    public void PreviewKeepsThePixelsOfAPageThatCannotBePublished(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(120, 80,
                Encoding.ASCII.GetBytes("1 0 0 rg 4 4 60 40 re f " + content)).Build());
            using var session = PdfPageRenderSession.OpenEngineFirst(path, 60, 40);
            PdfRenderedPage preview = session.RenderPreview(0);
            Assert.Equal((60, 40), (preview.Width, preview.Height));
            Assert.Contains(preview.RenderDiagnostics, item => item.Kind == PdfRenderDiagnosticKind.OmittedContent);
            Assert.Contains(Enumerable.Range(0, preview.Pixels.Length / 4), pixel =>
                preview.Pixels[pixel * 4 + 2] > 200 && preview.Pixels[pixel * 4 + 1] < 60);
            Assert.Throws<InvalidOperationException>(() => session.RenderPage(0));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PageThumbnailsRenderThroughThePreview()
    {
        string root = Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!;
        foreach (string file in new[] { Path.Combine("Models", "PageThumbnailVm.cs"), Path.Combine("Shell", "PageOperations.cs") })
        {
            string source = File.ReadAllText(Path.Combine(root, file));
            Assert.Contains("renderSession.RenderPreview(", source);
            Assert.DoesNotContain("renderSession.RenderPage(", source);
        }
    }

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public void MixedRecoveryAndOmission_DoesNotHideMaterialFailure()
    {
        var diagnostics = PdfRenderCompleteness.Classify([
            "Missing standard font resource /Helvetica was recovered.",
            "An undecodable Type 3 glyph was omitted."]);
        Assert.Throws<InvalidOperationException>(() => PdfRenderCompleteness.RequireComplete(3, diagnostics));
    }
}
