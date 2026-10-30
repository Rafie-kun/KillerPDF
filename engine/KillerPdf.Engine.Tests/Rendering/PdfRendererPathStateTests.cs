using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfRendererPathStateTests
{
    // The current path is not part of the graphics state, so q and Q in the middle of a path leave it alone.
    // dvipdfm-family output repeats "q 1 0 0 1 x y cm Q" between the segments of a box.
    [Fact]
    public void ASavedAndRestoredStateInTheMiddleOfAPathKeepsThePath()
    {
        PdfRenderedPage page = Render("1 0 0 rg 20 20 m q Q 200 20 l q 1 0 0 1 5 5 cm Q 200 200 l 20 200 l f");

        Assert.DoesNotContain(page.Diagnostics, message => message.Contains("not implemented", StringComparison.Ordinal));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(page, 100, 100));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(page, 190, 230));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(page, 5, 195));
    }

    [Fact]
    public void ThePathStillEndsAtItsPaintingOperatorAfterARestore()
    {
        // The restore must not make a finished path linger: nothing paints after the f. Pixels are BGRA.
        PdfRenderedPage page = Render("1 0 0 rg 20 20 m 200 20 l 200 200 l 20 200 l f q Q 0 0 1 rg f");

        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(page, 100, 100));
    }

    private static byte[] Pixel(PdfRenderedPage page, int x, int y)
        => page.Pixels.Span.Slice((y * 256 + x) * 4, 4).ToArray();

    private static PdfRenderedPage Render(string content)
        => new PdfPageRenderer(PdfDocument.OpenWithCompatibilityRecovery(Pdf(content)))
            .Render(0, new PdfRenderOptions(256, 256) { CacheResult = false });

    private static byte[] Pdf(string content)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 256 256] /Resources << >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        ];
        var pdf = new StringBuilder("%PDF-1.7\n", content.Length + 1024);
        var offsets = new List<int>();
        foreach (string value in objects)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{offsets.Count} 0 obj\n{value}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
