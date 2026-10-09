using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfCompactedGraphicsStateTests
{
    [Theory]
    [InlineData("")]
    [InlineData("State")]
    public void CompactedResourceNamesPreserveIndependentFillAndStrokeOpacity(string resourceName)
    {
        // CompactedPDFSyntaxTest uses an empty ExtGState name with ca=0.33 and CA=0.66.
        // The expected channels follow those source values, independently of a legacy renderer.
        static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(20, 10,
            Encoding.ASCII.GetBytes($"/{resourceName} gs 1 0. .0 rg 0 0 10 10 re f 2 w 0 0 0 RG 15 0 m 15 10 l S")).Build());
        var pageEntry = PdfPageTree.Read(source).Pages[0];
        var pageDictionary = (PdfDictionary)source.Resolve(pageEntry.Reference);
        var state = new PdfDictionary([new(Name("ca"), new PdfReal(.33)), new(Name("CA"), new PdfReal(.66))]);
        var resources = new PdfDictionary([new(Name("ExtGState"), new PdfDictionary([new(Name(resourceName), state)]))]);
        var update = new PdfIncrementalUpdateBuilder(source);
        update.ReplaceObject(pageEntry.Reference.ObjectNumber, new PdfDictionary(
            pageDictionary.Where(pair => !pair.Key.Equals(Name("Resources"))).Append(new(Name("Resources"), resources))));
        var renderer = new PdfPageRenderer(PdfDocument.Open(update.Build()));
        var options = new PdfRenderOptions(20, 10) { CacheResult = false };
        var full = renderer.Render(0, options);
        Assert.Empty(full.Diagnostics);
        Assert.Equal(new byte[] {171, 171, 255, 255}, full.Pixels.Slice((5 * 20 + 5) * 4, 4).ToArray());
        Assert.Equal(new byte[] {87, 87, 87, 255}, full.Pixels.Slice((5 * 20 + 15) * 4, 4).ToArray());
        byte[] region = new byte[12 * 3 * 4];
        Assert.Empty(renderer.RenderRegionInto(0, options, 4, 4, 12, 3, region));
        for (int row = 0; row < 3; row++)
            Assert.Equal(full.Pixels.Slice(((4 + row) * 20 + 4) * 4, 12 * 4).ToArray(),
                region.AsSpan(row * 12 * 4, 12 * 4).ToArray());
    }
}
