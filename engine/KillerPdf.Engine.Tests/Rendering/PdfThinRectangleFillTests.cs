using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
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

    // On a CMYK page an opaque process fill takes the combined opaque-fill path, which used to
    // rasterize the rule at its exact coverage: CMYK rules stayed faint while gray ones snapped.
    [Theory]
    [InlineData("0 0 0 1 k")]
    [InlineData("0 g")]
    public void ThinRuleOnACmykPageIsOneSolidPixelRow(string color)
    {
        PdfRenderedPage page = Render($"{color} 10 50.3 80 0.343 re f 10 10 10 10 re f", cmykPage: true);
        int solid = Gray(page, 15, 85);
        Assert.InRange(solid, 0, 100);
        for (int x = 10; x < 90; x++)
        {
            Assert.Equal(solid, Gray(page, x, 49));
            Assert.Equal(255, Gray(page, x, 48));
            Assert.Equal(255, Gray(page, x, 50));
        }
    }

    // A rule filled with a pattern is painted through a clip made from the same rectangle.
    [Fact]
    public void ThinRuleFilledWithAPatternIsOneSolidPixelRow()
    {
        PdfRenderedPage page = Render("/Pattern cs /P0 scn 10 50.3 80 0.343 re f", pattern: true);
        for (int x = 10; x < 90; x++)
        {
            Assert.Equal(0, Gray(page, x, 49));
            Assert.Equal(255, Gray(page, x, 48));
            Assert.Equal(255, Gray(page, x, 50));
        }
    }

    private static int Gray(PdfRenderedPage page, int x, int y) => page.Pixels.Span[(y * page.Width + x) * 4];

    private static PdfRenderedPage Render(string content, bool cmykPage = false, bool pattern = false)
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
            Encoding.ASCII.GetBytes(content)).Build());
        if (cmykPage || pattern)
        {
            PdfPageTreeEntry entry = PdfPageTree.Read(document).Pages[0];
            var original = Assert.IsType<PdfDictionary>(document.Resolve(entry.Reference));
            var update = new PdfIncrementalUpdateBuilder(document);
            List<KeyValuePair<PdfName, PdfObject>> page = [.. original.Where(item =>
                !item.Key.Equals(Name("Group")) && !item.Key.Equals(Name("Resources")))];
            if (cmykPage)
                page.Add(new(Name("Group"), new PdfDictionary([
                    new(Name("S"), Name("Transparency")), new(Name("CS"), Name("DeviceCMYK"))])));
            if (pattern)
            {
                // A black axial shading covering the page, so the pattern paints black wherever
                // its clip lets it.
                var function = new PdfDictionary([new(Name("FunctionType"), new PdfInteger(2)),
                    new(Name("Domain"), new PdfArray([new PdfInteger(0), new PdfInteger(1)])),
                    new(Name("C0"), new PdfArray([new PdfInteger(0)])),
                    new(Name("C1"), new PdfArray([new PdfInteger(0)])), new(Name("N"), new PdfInteger(1))]);
                var shading = new PdfDictionary([new(Name("ShadingType"), new PdfInteger(2)),
                    new(Name("ColorSpace"), Name("DeviceGray")),
                    new(Name("Coords"), new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(100), new PdfInteger(0)])),
                    new(Name("Function"), function), new(Name("Extend"), new PdfArray([new PdfBoolean(true), new PdfBoolean(true)]))]);
                var patternDictionary = new PdfDictionary([new(Name("PatternType"), new PdfInteger(2)),
                    new(Name("Shading"), shading)]);
                page.Add(new(Name("Resources"), new PdfDictionary([
                    new(Name("Pattern"), new PdfDictionary([new(Name("P0"), update.AddObject(patternDictionary))]))])));
            }
            update.ReplaceObject(entry.Reference.ObjectNumber, new PdfDictionary(page));
            document = PdfDocument.Open(update.Build());
        }
        return new PdfPageRenderer(document)
            .Render(0, new PdfRenderOptions(100, 100, includeAnnotations: false, includeFormFields: false));
    }

    private static PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
}
