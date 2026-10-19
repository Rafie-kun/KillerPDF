using System.IO;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfRenderCompletenessTests
{
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

    [Fact]
    public void MixedRecoveryAndOmission_DoesNotHideMaterialFailure()
    {
        var diagnostics = PdfRenderCompleteness.Classify([
            "Missing standard font resource /Helvetica was recovered.",
            "An undecodable Type 3 glyph was omitted."]);
        Assert.Throws<InvalidOperationException>(() => PdfRenderCompleteness.RequireComplete(3, diagnostics));
    }
}
