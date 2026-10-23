using System.Globalization;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfIndexedSpotCoverageTests
{
    public static TheoryData<bool, bool, int, int> Cases()
    {
        var data = new TheoryData<bool, bool, int, int>();
        foreach (bool mixed in (bool[])[false, true])
        foreach (bool overprint in (bool[])[false, true])
        foreach (int sourceSize in (int[])[1, 2])
        foreach (int coverage in (int[])[1, 64, 128, 254, 255])
            data.Add(mixed, overprint, sourceSize, coverage);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ClippedIndexedImageRetainsNamedInk(bool mixed, bool overprint, int sourceSize, int coverage)
    {
        string edge = (coverage / 256d).ToString("R", CultureInfo.InvariantCulture);
        // Clips keep their exact coverage, but a filled rectangle thinner than a pixel is painted
        // one whole pixel wide, so the coverage oracle starts its rectangle off the page.
        var geometry = Render("1 0 0 rg 0 0 1 1 re f 1 1 1 rg -1 0 "
            + (1 + coverage / 256d).ToString("R", CultureInfo.InvariantCulture) + " 1 re f",
            mixed, sourceSize, false);
        Assert.Equal(coverage, geometry.Pixels.Span[0]);
        var actual = Render("0 0 0.25 0 k 0 0 1 1 re f /Overprint gs "
            + "/Spot cs 1 scn 0 0 1 1 re f q /" + (overprint ? "Overprint" : "Knockout")
            + " gs 0 0 " + edge + " 1 re W n /Image Do Q "
            + "/Overprint gs /Spot cs 0 scn 0 0 1 1 re f", mixed, sourceSize, true);
        // A zero-tint repaint removes the named ink. Only the declared process
        // Cyan and the uncovered Yellow backdrop may remain after that repaint.
        int yellow = overprint ? 64 : (64 * (255 - coverage) + 127) / 255;
        string cyanTint = (mixed ? coverage / 255d : 0).ToString("R", CultureInfo.InvariantCulture);
        string yellowTint = (yellow / 255d).ToString("R", CultureInfo.InvariantCulture);
        var expected = Render(cyanTint + " 0 " + yellowTint + " 0 k 0 0 1 1 re f",
            mixed, sourceSize, true);
        Assert.Empty(actual.Diagnostics);
        Assert.Empty(expected.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    private static PdfRenderedPage Render(string content, bool mixed, int sourceSize, bool native)
    {
        static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        static PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
        static PdfDictionary Policy(bool overprint) => new([
            Entry("op", new PdfBoolean(overprint)), Entry("OPM", new PdfInteger(0))]);
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1, Encoding.ASCII.GetBytes(content)).Build());
        var root = (PdfDictionary)document.Resolve((PdfIndirectReference)document.Trailer[Name("Root")]);
        var pages = (PdfDictionary)document.Resolve((PdfIndirectReference)root[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)document.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(document);
        var transform = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("FunctionType", new PdfInteger(4)), Entry("Domain", Numbers(0, 1, 0, 1)),
            Entry("Range", Numbers(0, 1, 0, 1, 0, 1, 0, 1))]), Encoding.ASCII.GetBytes("{ 0.5 mul 0 0 }")));
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(sourceSize)),
            Entry("Height", new PdfInteger(sourceSize)), Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("ColorSpace", new PdfArray([Name("Indexed"), Name(mixed ? "Mixed" : "Spot"),
                new PdfInteger(0), new PdfString(mixed ? [255, 255] : [255], PdfStringForm.Hexadecimal)]))
        ]), new byte[sourceSize * sourceSize]));
        var resources = new PdfDictionary([
            Entry("ExtGState", new PdfDictionary([
                Entry("Overprint", Policy(true)), Entry("Knockout", Policy(false))])),
            Entry("XObject", new PdfDictionary([Entry("Image", image)])),
            Entry("ColorSpace", new PdfDictionary([
                Entry("Spot", new PdfArray([Name("Separation"), Name("CustomMagenta"), Name("DeviceCMYK"),
                    new PdfDictionary([Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
                        Entry("C0", Numbers(0, 0, 0, 0)), Entry("C1", Numbers(0, 1, 0, 0)), Entry("N", new PdfInteger(1))])])),
                Entry("Mixed", new PdfArray([Name("DeviceN"), new PdfArray([Name("Cyan"), Name("CustomMagenta")]),
                    Name("DeviceCMYK"), transform]))]))]);
        var entries = page.Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources));
        if (native) entries = entries.Append(Entry("Group", new PdfDictionary([
            Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))])));
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(entries));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions(1, 1) { CacheResult = false, MaximumParallelism = 1 });
    }
}
