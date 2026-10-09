using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfOpaquePaintRegionTests
{
    public static TheoryData<int, bool, bool, bool> Cases()
    {
        var data = new TheoryData<int, bool, bool, bool>();
        foreach (int size in new[] { 16, 64, 128 })
        foreach (bool nativeMagenta in new[] { false, true })
        foreach (bool narrowMargin in new[] { false, true })
        foreach (bool clipped in new[] { false, true })
            data.Add(size, nativeMagenta, narrowMargin, clipped);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void EnclosingProcessOverprint_RestoresUniformPhysicalInk(int size, bool nativeMagenta, bool narrowMargin, bool clipped)
    {
        var baseline = Render(size, nativeMagenta, narrowMargin, clipped, removeMagenta: false);
        var actual = Render(size, nativeMagenta, narrowMargin, clipped, removeMagenta: true);
        Assert.Empty(baseline.Diagnostics);
        Assert.Empty(actual.Diagnostics);
        byte[] physical = PdfDeviceCmykTests.RenderInk(128, 128, 128, 0);
        for (int offset = 0; offset < baseline.Pixels.Length; offset += 4)
            Assert.Equal(physical, baseline.Pixels.Span.Slice(offset, 4).ToArray());
        Assert.Equal(baseline.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void SameTintOverUniformBackdrop_PreservesEveryPixel(int size, bool nativeMagenta, bool narrowMargin, bool clipped)
    {
        var actual = Render(size, nativeMagenta, narrowMargin, clipped, removeMagenta: false);
        Assert.Empty(actual.Diagnostics);
        byte[] physical = PdfDeviceCmykTests.RenderInk(128, 128, 128, 0);
        for (int offset = 0; offset < actual.Pixels.Length; offset += 4)
            Assert.Equal(physical, actual.Pixels.Span.Slice(offset, 4).ToArray());
    }

    public static TheoryData<int, bool, bool> KnockoutCases()
    {
        var data = new TheoryData<int, bool, bool>();
        foreach (int size in new[] { 16, 64, 128 })
        foreach (bool narrowMargin in new[] { false, true })
        foreach (bool clipped in new[] { false, true })
            data.Add(size, narrowMargin, clipped);
        return data;
    }

    [Theory]
    [MemberData(nameof(KnockoutCases))]
    public void EnclosingAllChannelReplacement_EqualsSingleFinalPaint(int size, bool narrowMargin, bool clipped)
    {
        var expected = Render(size, false, narrowMargin, clipped, removeMagenta: false, overwriteAll: true);
        var actual = Render(size, false, narrowMargin, clipped, removeMagenta: true, overwriteAll: true);
        Assert.Empty(expected.Diagnostics);
        Assert.Empty(actual.Diagnostics);
        int interior = (size * 3 / 4 * size + size * 3 / 4) * 4;
        Assert.Equal(PdfDeviceCmykTests.RenderInk(0, 128, 0, 0), expected.Pixels.Span.Slice(interior, 4).ToArray());
        Assert.Equal(PdfDeviceCmykTests.RenderInk(128, 128, 128, 0), expected.Pixels.Span[..4].ToArray());
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    private static PdfRenderedPage Render(int size, bool nativeMagenta, bool narrowMargin, bool clipped,
        bool removeMagenta, bool overwriteAll = false)
    {
        static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        static PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
        string inner = narrowMargin ? "1.3 1.3 5.4 5.4 re f " : "1.25 1.25 5.5 5.5 re f ";
        string outer = narrowMargin ? "1.29 1.29 5.42 5.42 re f" : "1.125 1.125 5.75 5.75 re f";
        string content = ".5 .5 .5 0 k 0 0 8 8 re f "
            + (clipped ? "q .33 .27 m 7.71 7.53 l 7.77 .19 l h W n " : "")
            + (removeMagenta ? ".5 0 .5 0 k " + inner : "")
            + "/On gs " + (nativeMagenta ? "/Magenta cs .5 scn " : "0 .5 0 0 k ")
            + outer + (clipped ? " Q" : "");
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(8, 8, Encoding.ASCII.GetBytes(content)).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var dictionary = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        var tint = new PdfDictionary([
            Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
            Entry("C0", Numbers(0, 0, 0, 0)), Entry("C1", Numbers(0, 1, 0, 0)), Entry("N", new PdfInteger(1))]);
        var resources = new PdfDictionary([
            Entry("ColorSpace", new PdfDictionary([Entry("Magenta", new PdfArray([
                Name("Separation"), Name("Magenta"), Name("DeviceCMYK"), tint]))])),
            Entry("ExtGState", new PdfDictionary([Entry("On", new PdfDictionary([
                Entry("OP", new PdfBoolean(true)), Entry("op", new PdfBoolean(true)),
                Entry("OPM", new PdfInteger(nativeMagenta || overwriteAll ? 0 : 1))]))]))]);
        var group = new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))]);
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(dictionary
            .Where(entry => !entry.Key.Equals(Name("Resources")) && !entry.Key.Equals(Name("Group")))
            .Append(Entry("Resources", resources)).Append(Entry("Group", group))));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(size, size));
    }
}
