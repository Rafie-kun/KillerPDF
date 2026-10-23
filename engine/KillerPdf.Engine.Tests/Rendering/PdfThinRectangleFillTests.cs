using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// Table rules are often filled rectangles thinner than a device pixel. Painted at their exact
// coverage they came out as faint gray lines; PDFium, Poppler and Acrobat's thin-line
// enhancement draw them one solid pixel wide, so 1.9 does too.
public sealed class PdfThinRectangleFillTests
{
    [Fact]
    public void ThinHorizontalRuleIsOneSolidPixelRow()
    {
        // 50.3 to 50.643 points is device rows 49.357 to 49.7 at one pixel per point.
        PdfRenderedPage page = Render("0 g 10 50.3 80 0.343 re f");
        for (int x = 10; x < 90; x++)
        {
            Assert.Equal(0, Gray(page, x, 49));
            Assert.Equal(255, Gray(page, x, 48));
            Assert.Equal(255, Gray(page, x, 50));
        }
        Assert.Equal(255, Gray(page, 9, 49));
        Assert.Equal(255, Gray(page, 90, 49));
    }

    [Fact]
    public void ThinVerticalRuleIsOneSolidPixelColumn()
    {
        PdfRenderedPage page = Render("0 g 30.6 10 0.343 80 re f");
        for (int y = 10; y < 90; y++)
        {
            Assert.Equal(0, Gray(page, 30, y));
            Assert.Equal(255, Gray(page, 29, y));
            Assert.Equal(255, Gray(page, 31, y));
        }
    }

    // Only rectangles thinner than a pixel change; a wider one keeps its partial edge rows.
    [Fact]
    public void RectangleWiderThanAPixelKeepsItsCoverage()
    {
        PdfRenderedPage page = Render("0 g 10 50.3 80 1.5 re f");
        Assert.InRange(Gray(page, 40, 48), 1, 254);
        Assert.InRange(Gray(page, 40, 49), 1, 254);
    }

    // A rotated rule is not snapped to the pixel grid, and a rectangle with no area paints nothing.
    [Fact]
    public void RotatedOrEmptyRectanglesAreUnchanged()
    {
        PdfRenderedPage rotated = Render("0 g q 0.8 0.6 -0.6 0.8 50 50 cm -40 0 80 0.343 re f Q");
        byte[] pixels = rotated.Pixels.ToArray();
        int darkest = 255;
        for (int index = 0; index < pixels.Length; index += 4) darkest = Math.Min(darkest, (int)pixels[index]);
        Assert.InRange(darkest, 1, 254);

        PdfRenderedPage empty = Render("0 g 10 50 80 0 re f");
        Assert.All(empty.Pixels.ToArray(), value => Assert.Equal(255, value));
    }

    private static int Gray(PdfRenderedPage page, int x, int y) => page.Pixels.Span[(y * page.Width + x) * 4];

    private static PdfRenderedPage Render(string content) =>
        new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
                Encoding.ASCII.GetBytes(content)).Build()))
            .Render(0, new PdfRenderOptions(100, 100, includeAnnotations: false, includeFormFields: false));
}
