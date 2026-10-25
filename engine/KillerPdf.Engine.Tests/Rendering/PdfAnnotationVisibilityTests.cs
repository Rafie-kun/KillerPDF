using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfAnnotationVisibilityTests
{
    [Fact]
    public void PrintOnlyAppearanceIsAbsentFromScreenRendering()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(CreatePdf(36)));

        PdfRenderedPage page = renderer.Render(0, new PdfRenderOptions(100, 100));

        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(page, 50, 50));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(page, 5, 95));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CachedScreenAndPrintAppearancesRemainIndependent(bool printFirst)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(CreatePdf(36)));
        foreach (bool printing in new[] { printFirst, !printFirst, printFirst })
        {
            PdfRenderedPage page = renderer.Render(0,
                new PdfRenderOptions(100, 100) { ForPrinting = printing });

            Assert.Equal(printing ? new byte[] { 255, 0, 0, 255 }
                : new byte[] { 255, 255, 255, 255 }, Pixel(page, 50, 50));
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(page, 5, 95));
        }
    }

    [Theory]
    [InlineData(2, false, false)]
    [InlineData(2, true, false)]
    [InlineData(34, false, false)]
    [InlineData(34, true, false)]
    [InlineData(4, false, true)]
    [InlineData(4, true, true)]
    [InlineData(0, false, true)]
    [InlineData(0, true, true)]
    public void ExistingHiddenAndOrdinaryAppearanceBehaviorIsPreserved(int flags, bool printing, bool visible)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(CreatePdf(flags)));
        PdfRenderedPage page = renderer.Render(0,
            new PdfRenderOptions(100, 100) { ForPrinting = printing });

        Assert.Equal(visible ? new byte[] { 255, 0, 0, 255 }
            : new byte[] { 255, 255, 255, 255 }, Pixel(page, 50, 50));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(page, 5, 95));
    }

    private static byte[] Pixel(PdfRenderedPage page, int x, int y) =>
        page.Pixels.Span.Slice((y * page.Width + x) * 4, 4).ToArray();

    private static byte[] CreatePdf(int flags)
    {
        const string appearance = "0 0 1 rg 0 0 60 60 re f";
        const string content = "0 1 0 rg 0 0 10 10 re f";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 6 0 R /Annots [4 0 R] >>",
            $"<< /Type /Annot /Subtype /Square /F {flags} /Rect [20 20 80 80] /AP << /N 5 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 60 60] /Resources << >> /Length {appearance.Length} >>\nstream\n{appearance}\nendstream",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
