using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfCmykPageGroupBackdropTests
{
    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(true, 1, false)]
    [InlineData(false, 0.5, false)]
    [InlineData(true, 0.5, false)]
    [InlineData(false, 1, true)]
    [InlineData(true, 1, true)]
    [InlineData(false, 0.5, true)]
    [InlineData(true, 0.5, true)]
    public void FirstCmykPageGroupPaintDoesNotBlendWithPaper(bool isolated, double opacity, bool named)
    {
        var options = new PdfRenderOptions(10, 10);
        var normalRenderer = new PdfPageRenderer(Create(isolated, PdfBlendMode.Normal, opacity, named));
        var differenceRenderer = new PdfPageRenderer(Create(isolated, PdfBlendMode.Difference, opacity, named));
        var normal = normalRenderer.Render(0, options);
        var difference = differenceRenderer.Render(0, options);
        Assert.Empty(normal.Diagnostics);
        Assert.Empty(difference.Diagnostics);
        // The first object sees transparent backdrop, so its blend mode has no effect.
        Assert.Equal(normal.Pixels.ToArray(), difference.Pixels.ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, difference.Pixels.Slice((5 * 10 + 8) * 4, 4).ToArray());
        var transparent = differenceRenderer.Render(0, new PdfRenderOptions(10, 10, transparentBackground: true));
        Assert.Equal(opacity == 1 ? 255 : 128, transparent.Pixels.Span[(5 * 10 + 2) * 4 + 3]);
        Assert.Equal(0, transparent.Pixels.Span[(5 * 10 + 8) * 4 + 3]);
        var region = differenceRenderer.RenderRegion(0, options, 2, 5, 7, 1);
        Assert.Equal(difference.Pixels.Slice((5 * 10 + 2) * 4, 7 * 4).ToArray(), region.Pixels.ToArray());
        if (opacity == 0.5)
        {
            // Poppler's independent render of this synthetic DeviceCMYK group gives RGB 127,214,247.
            Assert.Equal(new byte[] { 247, 214, 127, 255 }, difference.Pixels.Slice((5 * 10 + 2) * 4, 4).ToArray());
        }
        var plate = normalRenderer.Render(0, options with { SeparationPlate = "Cyan" });
        Assert.Empty(plate.Diagnostics);
        byte plateGray = opacity == 1 ? (byte)0 : (byte)127;
        Assert.Equal(new byte[] { plateGray, plateGray, plateGray, 255 },
            plate.Pixels.Slice((5 * 10 + 2) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, plate.Pixels.Slice((5 * 10 + 8) * 4, 4).ToArray());
    }

    private static PdfDocument Create(bool isolated, PdfBlendMode mode, double opacity, bool named)
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10,
            new PdfContentStreamBuilder().SetGraphicsState(new PdfGraphicsState(fillOpacity: opacity, blendMode: mode))
                .SetFillCmyk(1, 0, 0, 0).Rectangle(0, 0, 5, 10).Fill()).Build());
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        var catalog = (PdfDictionary)document.Resolve((PdfIndirectReference)document.Trailer[Name("Root")]);
        var pages = (PdfDictionary)document.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)document.Resolve(reference);
        if (named)
        {
            var resourcesValue = page[Name("Resources")];
            var resources = (PdfDictionary)(resourcesValue is PdfIndirectReference resourceReference
                ? document.Resolve(resourceReference) : resourcesValue);
            page = new PdfDictionary(page.Where(entry => !entry.Key.Equals(Name("Resources"))).Append(
                new KeyValuePair<PdfName, PdfObject>(Name("Resources"), new PdfDictionary(resources.Append(
                    new KeyValuePair<PdfName, PdfObject>(Name("ColorSpace"), new PdfDictionary([
                        new(Name("NativeInk"), Name("DeviceCMYK"))])))))));
        }
        var group = new PdfDictionary([
            new(Name("S"), Name("Transparency")), new(Name("CS"), Name(named ? "NativeInk" : "DeviceCMYK")),
            new(Name("I"), new PdfBoolean(isolated))]);
        return PdfDocument.Open(new PdfIncrementalUpdateBuilder(document).ReplaceObject(reference.ObjectNumber,
            new PdfDictionary(page.Append(new(Name("Group"), group)))).Build());
    }
}
