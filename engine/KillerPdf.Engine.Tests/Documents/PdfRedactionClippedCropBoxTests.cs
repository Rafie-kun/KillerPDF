using System.Globalization;
using System.Text;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

// A crop box larger than the media box shows only the part inside the media box. A region laid on
// that page lands on what is really drawn, so a stroke it cuts is not something the page can give up
// without becoming an image, however large the crop box says the page is.
public sealed class PdfRedactionClippedCropBoxTests
{
    private static readonly PdfContentBounds Middle = new(50, 40, 150, 60);

    [Fact]
    public void ThePageIsAsLargeAsTheMediaBoxItsCropBoxOverreaches()
    {
        PdfDocument document = PdfDocument.Open(Page());

        PdfPageContent content = new PdfPageContentReader(document).Read(0);

        Assert.Equal(200, content.Width);
        Assert.Equal(100, content.Height);
    }

    [Fact]
    public void AStrokeTheRegionCutsAsksForAnImageAndIsRefusedWithoutOne()
    {
        PdfDocument document = PdfDocument.Open(Page());
        var regions = new[] { new PdfRedactionRegion(0, Middle) };

        PdfRedactionPageAnalysis page = Assert.Single(PdfContentRedaction.Analyze(document, regions).Pages);
        InvalidOperationException refusal = Assert.Throws<InvalidOperationException>(
            () => PdfContentRedaction.Apply(document, regions));

        Assert.Equal(PdfRedactionPageMethod.Raster, page.Method);
        Assert.Contains("Vector", page.RasterReasons);
        Assert.Equal("Pages 1 cannot be redacted without replacing them with an image.", refusal.Message);
    }

    [Fact]
    public void ApprovingTheImageRedactsAndVerifies()
    {
        PdfDocument document = PdfDocument.Open(Page());

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, Middle)], new PdfContentRedactionOptions { ApprovedRasterPages = [0] });

        Assert.True(result.Verification.Succeeded);
        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(result.Document)));
    }

    private static byte[] Page()
    {
        const string content = "1 w 0 0 m 100 100 l S";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /CropBox [0 0 612 792] /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
        ];
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(text.Length);
            text.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            text.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        text.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }
}
