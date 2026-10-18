using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageGroupBackdropTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 0.5)]
    [InlineData(true, 0.5)]
    public void Render_PageGroupBlendsBeforeAddingWhitePaper(bool isolated, double opacity)
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10,
            new PdfContentStreamBuilder()
                .SetGraphicsState(new PdfGraphicsState(fillOpacity: opacity, blendMode: PdfBlendMode.Difference))
                .SetFillRgb(1, 0, 0).Rectangle(0, 0, 5, 10).Fill()).Build());
        PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
        var catalog = (PdfDictionary)document.Resolve((PdfIndirectReference)document.Trailer[Name("Root")]);
        var pages = (PdfDictionary)document.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)document.Resolve(reference);
        var group = new PdfDictionary([
            new(Name("S"), Name("Transparency")),
            new(Name("CS"), Name("DeviceRGB")),
            new(Name("I"), new PdfBoolean(isolated))]);
        var update = new PdfIncrementalUpdateBuilder(document);
        document = PdfDocument.Open(update.ReplaceObject(reference.ObjectNumber,
            new PdfDictionary(page.Append(new(Name("Group"), group)))).Build());

        var renderer = new PdfPageRenderer(document);
        var options = new PdfRenderOptions(10, 10);
        var rendered = renderer.Render(0, options);

        Assert.Empty(rendered.Diagnostics);
        byte paper = opacity == 1 ? (byte)0 : (byte)127;
        Assert.Equal(new byte[] { paper, paper, 255, 255 }, rendered.Pixels.Slice((5 * 10 + 2) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, rendered.Pixels.Slice((5 * 10 + 8) * 4, 4).ToArray());
        var transparent = renderer.Render(0, new PdfRenderOptions(10, 10, transparentBackground: true));
        Assert.Equal(new byte[] { 0, 0, 255, opacity == 1 ? (byte)255 : (byte)128 },
            transparent.Pixels.Slice((5 * 10 + 2) * 4, 4).ToArray());
        Assert.Equal(0, transparent.Pixels.Span[(5 * 10 + 8) * 4 + 3]);
        var region = renderer.RenderRegion(0, options, 2, 5, 7, 1);
        Assert.Equal(rendered.Pixels.Slice((5 * 10 + 2) * 4, 7 * 4).ToArray(), region.Pixels.ToArray());
        var plate = renderer.Render(0, options with { SeparationPlate = "Cyan" });
        Assert.Empty(plate.Diagnostics);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, plate.Pixels.Slice((5 * 10 + 8) * 4, 4).ToArray());
    }
}
