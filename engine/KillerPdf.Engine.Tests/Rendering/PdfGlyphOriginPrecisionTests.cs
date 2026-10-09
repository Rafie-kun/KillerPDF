using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfGlyphOriginPrecisionTests
{
    [Theory]
    [InlineData(PdfStandardFont.TimesRoman, 3.37, 10.61)]
    [InlineData(PdfStandardFont.TimesRoman, 3.03, 10.19)]
    [InlineData(PdfStandardFont.TimesRoman, -1.17, 10.97)]
    [InlineData(PdfStandardFont.HelveticaBold, 3.37, 10.61)]
    [InlineData(PdfStandardFont.HelveticaBold, 3.03, 10.19)]
    [InlineData(PdfStandardFont.HelveticaBold, -1.17, 10.97)]
    [InlineData(PdfStandardFont.Courier, 3.37, 10.61)]
    [InlineData(PdfStandardFont.Courier, 3.03, 10.19)]
    [InlineData(PdfStandardFont.Courier, -1.17, 10.97)]
    public void CachedTextRetainsFractionalSourceOrigins(PdfStandardFont font, double x, double y)
    {
        var content = new PdfContentStreamBuilder().BeginText().SetFont(font, 9)
            .SetTextMatrix(1, 0, 0, 1, x, y).ShowLatin1Text("Quick brown foxes jump").EndText();
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(120, 30, content).Build());
        var options = new PdfRenderOptions(300, 75, includeAnnotations: false, includeFormFields: false);
        AssertSourceOrigins(document, options, 500);
    }

    [Theory]
    [InlineData(0, 1, -1, 0)]
    [InlineData(-1, 0, 0, 1)]
    [InlineData(1, 0.5, 0.25, 1)]
    [InlineData(0.5, 0, 0, 1.5)]
    public void CachedTransformedTextRetainsFractionalSourceOrigins(double a, double b, double c, double d)
    {
        var content = new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.HelveticaBold, 24)
            .SetTextMatrix(a, b, c, d, 40.37, 40.61).ShowLatin1Text("B").EndText();
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(80, 80, content).Build());
        var options = new PdfRenderOptions(160, 160, includeAnnotations: false, includeFormFields: false);
        AssertSourceOrigins(document, options, 100);
    }

    [Fact]
    public void IntegerGlyphPhasesMatchScalarRasterization()
    {
        var content = new PdfContentStreamBuilder();
        for (int index = 0; index < 640; index++)
            content.BeginText().SetFont(PdfStandardFont.TimesRoman, 5 + index * 0.0015)
                .SetTextMatrix(1, 0.15, -0.05, 1,
                    3.37 + index % 32 * 10, 5.61 + index / 32 * 9)
                .ShowLatin1Text("B").EndText();
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(320, 200, content).Build());
        var options = new PdfRenderOptions(960, 600, includeAnnotations: false, includeFormFields: false);
        var scalar = new PdfPageRenderer(document) { UseTransformedGlyphPoints = false }.Render(0, options);
        var integer = new PdfPageRenderer(document).Render(0, options);
        Assert.Equal(scalar.Pixels.ToArray(), integer.Pixels.ToArray());
        Assert.Empty(integer.Diagnostics);
    }

    [Fact]
    public void ManyGlyphSizesRetainSourceOriginsWithinGeometryBudget()
    {
        var content = new PdfContentStreamBuilder();
        for (int index = 0; index < 640; index++)
            content.BeginText().SetFont(PdfStandardFont.HelveticaBold, 5 + index * 0.0015)
                .SetTextMatrix(1, 0, 0, 1, 3.37 + index % 32 * 10, 5.61 + index / 32 * 9)
                .ShowLatin1Text("B").EndText();
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(320, 200, content).Build());
        var options = new PdfRenderOptions(960, 600, includeAnnotations: false, includeFormFields: false);
        AssertSourceOrigins(document, options, 2000, static renderer =>
        {
            Assert.InRange(renderer.GlyphGeometryCacheCount, 1, 256);
            Assert.InRange(renderer.GlyphGeometryCacheBytes, 1, 512L * 1024);
        });
    }

    [Fact]
    public void FractionalHelveticaCaptionsMatchScalarRasterization()
    {
        var content = new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 9)
            .SetTextMatrix(1, 0, 0, 1, 20, 255)
            .ShowLatin1Text("Last gap finishes at 480, therefore 480 to 490 is 10 unit on. Start is always lower left corner.")
            .EndText();
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(800, 800, content).Build());
        var options = new PdfRenderOptions(512, 512, includeAnnotations: false, includeFormFields: false);
        var scalar = new PdfPageRenderer(document) { UseTransformedGlyphPoints = false }.Render(0, options);
        var integer = new PdfPageRenderer(document).Render(0, options);
        Assert.Equal(scalar.Pixels.ToArray(), integer.Pixels.ToArray());
    }

    private static void AssertSourceOrigins(PdfDocument document, PdfRenderOptions options, int minimumInked,
        Action<PdfPageRenderer>? inspect = null)
    {
        var directRenderer = new PdfPageRenderer(document) { UseGlyphMaskCache = false };
        var cachedRenderer = new PdfPageRenderer(document);
        var direct = directRenderer.Render(0, options);
        var cached = cachedRenderer.Render(0, options);
        int maximum = 0, inked = 0;
        for (int offset = 0; offset < direct.Pixels.Length; offset += 4)
        {
            if (direct.Pixels.Span[offset] < 255) inked++;
            for (int channel = 0; channel < 3; channel++)
                maximum = Math.Max(maximum, Math.Abs(
                    direct.Pixels.Span[offset + channel] - cached.Pixels.Span[offset + channel]));
        }
        Assert.True(inked > minimumInked, $"Expected painted text, found {inked} inked pixels.");
        Assert.True(cachedRenderer.GlyphMaskCacheCount > 0);
        // The direct path retains the declared text matrix and advances. Caching must
        // preserve those positions within the rasterizer's small edge-rounding error.
        Assert.True(maximum <= 8, $"Cached source position changed a channel by {maximum}.");
        inspect?.Invoke(cachedRenderer);
    }
}
