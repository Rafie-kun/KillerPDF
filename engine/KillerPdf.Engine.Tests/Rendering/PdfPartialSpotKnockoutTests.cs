using System.Globalization;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPartialSpotKnockoutTests
{
    public static TheoryData<bool, bool, bool, int> Cases()
    {
        var data = new TheoryData<bool, bool, bool, int>();
        foreach (bool native in new[] { false, true })
        foreach (bool opacity in new[] { false, true })
        foreach (bool followingSpot in new[] { false, true })
        foreach (int coverage in new[] { 1, 64, 128, 254, 255 })
            data.Add(native, opacity, followingSpot, coverage);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void PartialProcessKnockoutRetainsNamedInkForFollowingOverprint(
        bool native, bool opacity, bool followingSpot, int coverage)
    {
        string width = Number(coverage / 256d);
        // RGB geometry provides an independent byte-coverage oracle for both surfaces.
        // Native CMYK's RGB conversion cannot be used to measure coverage.
        var edge = Render("1 0 0 rg 0 0 1 1 re f 1 1 1 rg 0 0 " + width + " 1 re f", false);
        Assert.Equal(coverage, edge.Pixels.Span[0]);
        string knockout = opacity ? "/Partial gs 0 0 0 0 k 0 0 1 1 re f"
            : "/Knockout gs 0 0 0 0 k 0 0 " + width + " 1 re f";
        string following = followingSpot ? "/Yellow cs 1 scn" : "0 0 0 0 k";
        var actual = Render("/Spot cs 1 scn 0 0 1 1 re f " + knockout
            + " /Overprint gs " + following + " 0 0 1 1 re f", native, coverage);
        // Independent process swatch represents the named magenta left uncovered by
        // the knockout, plus the disjoint yellow colorant when it is overprinted.
        var expected = Render("0 " + Number((255 - coverage) / 255d) + " "
            + (followingSpot ? "1" : "0") + " 0 k 0 0 1 1 re f", native);
        Assert.Empty(edge.Diagnostics);
        Assert.Empty(actual.Diagnostics);
        Assert.Empty(expected.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoredSpotColorAndOpacityRetainPartialNamedInk(bool native)
    {
        var actual = Render("/Spot cs 1 scn q /Partial gs 0 0 0 0 k Q "
            + "0 0 1 1 re f /Knockout gs 0 0 0 0 k 0 0 0.5 1 re f "
            + "/Overprint gs 0 0 0 0 k 0 0 1 1 re f", native, 128);
        var expected = Render("0 " + Number(127 / 255d) + " 0 0 k 0 0 1 1 re f", native);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpotElsewherePreservesProcessCoverageWithoutNamedInk(bool opacity)
    {
        string process = opacity ? "/Partial gs 0 0 0 0.5 k 0 0 1 1 re f"
            : "0 0 0 0.5 k 0 0 0.5 1 re f";
        var expected = Render(process, false, 128, 2);
        var actual = Render(process + " /Overprint gs /Spot cs 1 scn 1 0 1 1 re f",
            false, 128, 2);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.Span[..4].ToArray(), actual.Pixels.Span[..4].ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void VectorSpotPreflightPreservesUnmodeledProcessOverprint(bool rgb)
    {
        string process = (rgb ? "0.5 0.5 0.5 rg" : "0.5 g") + " 0 0 1 1 re f";
        var expected = Render(process, false, width: 2);
        var actual = Render("/Spot cs 1 scn 1 0 1 1 re f /Overprint gs " + process,
            false, width: 2);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.Span[..4].ToArray(), actual.Pixels.Span[..4].ToArray());
    }

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    [Fact]
    public void RegistrationColorSelectionPreservesSupportedSpotImageOverprint()
    {
        var actual = Render("q /All cs 1 scn Q 0 0 0 0.5 k 0 0 1 1 re f "
            + "/Overprint gs /Im Do", false, image: true);
        var expected = Render("0 1 0 0.5 k 0 0 1 1 re f", false);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    private static PdfRenderedPage Render(string content, bool native, int coverage = 255,
        int width = 1, bool image = false)
    {
        static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        static PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(width, 1, Encoding.ASCII.GetBytes(content)).Build());
        var root = (PdfDictionary)document.Resolve((PdfIndirectReference)document.Trailer[Name("Root")]);
        var pages = (PdfDictionary)document.Resolve((PdfIndirectReference)root[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)document.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfDictionary Policy(bool overprint) => new([
            Entry("op", new PdfBoolean(overprint)), Entry("OP", new PdfBoolean(overprint)),
            Entry("OPM", new PdfInteger(0)), Entry("ca", new PdfInteger(1))]);
        PdfObject Separation(string colorant, params int[] ink) => new PdfArray([
            Name("Separation"), Name(colorant), Name("DeviceCMYK"), new PdfDictionary([
                Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
                Entry("C0", Numbers(0, 0, 0, 0)), Entry("C1", Numbers(ink)), Entry("N", new PdfInteger(1))])]);
        var partial = new PdfDictionary([
            Entry("op", new PdfBoolean(false)), Entry("OP", new PdfBoolean(false)),
            Entry("ca", new PdfReal(coverage / 255d))]);
        KeyValuePair<PdfName, PdfObject>[] additionalStates = image ? [] : [Entry("Partial", partial)];
        KeyValuePair<PdfName, PdfObject>[] imageObjects = image ? [
            Entry("Im", update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(1)),
                Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)),
                Entry("ColorSpace", Name("Spot"))]), [255])))] : [];
        var resources = new PdfDictionary([
            Entry("ColorSpace", new PdfDictionary([
                Entry("Spot", Separation("CustomMagenta", 0, 1, 0, 0)),
                Entry("All", Separation("All", 1, 1, 1, 1)),
                Entry("Yellow", Separation("CustomYellow", 0, 0, 1, 0))])),
            Entry("ExtGState", new PdfDictionary([
                Entry("Knockout", Policy(false)), Entry("Overprint", Policy(true)),
                ..additionalStates])),
            Entry("XObject", new PdfDictionary(imageObjects))]);
        var entries = page.Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources));
        if (native) entries = entries.Append(Entry("Group", new PdfDictionary([
            Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))])));
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(entries));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions(width, 1) { CacheResult = false, MaximumParallelism = 1 });
    }
}
