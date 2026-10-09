using System.Globalization;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfMixedSpotCoverageTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 64)]
    [InlineData(false, 128)]
    [InlineData(false, 254)]
    [InlineData(false, 255)]
    [InlineData(true, 1)]
    [InlineData(true, 64)]
    [InlineData(true, 128)]
    [InlineData(true, 254)]
    [InlineData(true, 255)]
    public void MixedDeviceNEdgeRetainsProcessAndNamedInk(bool overprint, int coverage)
    {
        string edge = (coverage / 256d).ToString("R", CultureInfo.InvariantCulture);
        var geometry = Render("1 0 0 rg 0 0 1 1 re f 1 1 1 rg 0 0 " + edge + " 1 re f", false);
        Assert.Equal(coverage, geometry.Pixels.Span[0]);
        var actual = Render("0 0 0.25 0 k 0 0 1 1 re f /Overprint gs "
            + "/Spot cs 1 scn 0 0 1 1 re f /" + (overprint ? "Overprint" : "Knockout")
            + " gs /Mixed cs 1 1 scn 0 0 " + edge + " 1 re f "
            + "/Overprint gs /Spot cs 0 scn 0 0 1 1 re f", true);
        // Repainting the same named plate with zero tint removes its ink. The
        // declared Cyan remains, while unrelated Yellow survives only uncovered
        // knockout geometry. A plain process swatch supplies an independent oracle.
        int yellow = overprint ? 64 : (64 * (255 - coverage) + 127) / 255;
        string cyanTint = (coverage / 255d).ToString("R", CultureInfo.InvariantCulture);
        string yellowTint = (yellow / 255d).ToString("R", CultureInfo.InvariantCulture);
        var expected = Render(cyanTint + " 0 " + yellowTint + " 0 k 0 0 1 1 re f", true);
        Assert.Empty(actual.Diagnostics);
        Assert.Empty(expected.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    private static PdfRenderedPage Render(string content, bool native)
    {
        static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        static PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1, Encoding.ASCII.GetBytes(content)).Build());
        var root = (PdfDictionary)document.Resolve((PdfIndirectReference)document.Trailer[Name("Root")]);
        var pages = (PdfDictionary)document.Resolve((PdfIndirectReference)root[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)document.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(document);
        static PdfDictionary Policy(bool overprint) => new([
            Entry("op", new PdfBoolean(overprint)), Entry("OPM", new PdfInteger(0))]);
        var mixed = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("FunctionType", new PdfInteger(4)), Entry("Domain", Numbers(0, 1, 0, 1)),
            Entry("Range", Numbers(0, 1, 0, 1, 0, 1, 0, 1))]), Encoding.ASCII.GetBytes("{ 0.5 mul 0 0 }")));
        var resources = new PdfDictionary([
            Entry("ExtGState", new PdfDictionary([
                Entry("Overprint", Policy(true)), Entry("Knockout", Policy(false))])),
            Entry("ColorSpace", new PdfDictionary([
                Entry("Spot", new PdfArray([Name("Separation"), Name("CustomMagenta"), Name("DeviceCMYK"),
                    new PdfDictionary([Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
                        Entry("C0", Numbers(0, 0, 0, 0)), Entry("C1", Numbers(0, 1, 0, 0)), Entry("N", new PdfInteger(1))])])),
                Entry("Mixed", new PdfArray([Name("DeviceN"), new PdfArray([Name("Cyan"), Name("CustomMagenta")]),
                    Name("DeviceCMYK"), mixed]))]))]);
        var entries = page.Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources));
        if (native) entries = entries.Append(Entry("Group", new PdfDictionary([
            Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))])));
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(entries));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions(1, 1) { CacheResult = false, MaximumParallelism = 1 });
    }
}
