using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfRgbProcessSpotTests
{
    [Fact]
    public void BackgroundSpotImage_MatchesIndependentNativePlates()
    {
        var expected = Render("background",0,true,true,false,false,true);
        var actual = Render("background",0,true,true,false,false,false);
        Assert.Empty(expected.Diagnostics);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(),actual.Pixels.ToArray());
        Assert.Equal(PdfDeviceCmykTests.RenderInk(255,255,0,0),actual.Pixels.Span[..4].ToArray());
    }

    public static TheoryData<string, int, bool, bool, bool> Cases()
    {
        var data = new TheoryData<string, int, bool, bool, bool>();
        foreach (string foreground in new[] { "path", "stroke", "image" })
        foreach (int mode in new[] { 0, 1 })
        foreach (bool white in new[] { false, true })
        foreach (bool clipped in new[] { false, true })
        foreach (bool followingSpot in new[] { false, true })
            data.Add(foreground, mode, white, clipped, followingSpot);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RgbProcessOverSpot_MatchesIndependentNativePlates(string foreground, int mode,
        bool white, bool clipped, bool followingSpot)
    {
        var expected = Render(foreground, mode, true, white, clipped, followingSpot, nativeCmyk: true);
        var actual = Render(foreground, mode, true, white, clipped, followingSpot, nativeCmyk: false);
        Assert.Empty(expected.Diagnostics);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Theory]
    [InlineData("path", 0)]
    [InlineData("path", 1)]
    [InlineData("stroke", 0)]
    [InlineData("stroke", 1)]
    [InlineData("image", 0)]
    [InlineData("image", 1)]
    public void WhiteWithoutOverprint_ClearsProcessAndSpot(string foreground, int mode)
    {
        var page = Render(foreground, mode, false, true, false, false, false);
        const int offset = (32 * 128 + 32) * 4;
        Assert.Empty(page.Diagnostics);
        Assert.Equal(new byte[] {255,255,255,255}, page.Pixels.Span[offset..(offset + 4)].ToArray());
    }

    [Theory]
    [InlineData("path", 0)]
    [InlineData("path", 1)]
    [InlineData("stroke", 0)]
    [InlineData("stroke", 1)]
    [InlineData("image", 0)]
    [InlineData("image", 1)]
    public void WhiteOverprint_PreservesNamedPlateAndUsesProcessMode(string foreground, int mode)
    {
        var page = Render(foreground, mode, true, true, false, false, false);
        const int offset = (32 * 128 + 32) * 4;
        byte cyan = mode == 1 && foreground != "image" ? (byte)255 : (byte)0;
        Assert.Empty(page.Diagnostics);
        Assert.Equal(PdfDeviceCmykTests.RenderInk(cyan,255,0,0),
            page.Pixels.Span[offset..(offset + 4)].ToArray());
    }

    public static TheoryData<string, int, int, bool, int, bool, bool> ImageCases()
    {
        var data = new TheoryData<string, int, int, bool, int, bool, bool>();
        foreach (string space in new[] { "DeviceCMYK", "IndexedCMYK", "Cyan", "IndexedCyan" })
        foreach (int bits in space.StartsWith("Indexed", StringComparison.Ordinal) ? (int[])[8] : [8, 16])
        foreach (int side in new[] { 1, 64 })
        foreach (bool interpolate in new[] { false, true })
        foreach (int mode in new[] { 0, 1 })
        foreach (bool clipped in new[] { false, true })
        foreach (bool overprint in new[] { false, true })
            data.Add(space, bits, side, interpolate, mode, clipped, overprint);
        return data;
    }

    [Theory]
    [MemberData(nameof(ImageCases))]
    public void SampledProcessAndPlainSpotImages_MatchIndependentNativePlates(string space,
        int bits, int side, bool interpolate, int mode, bool clipped, bool overprint)
    {
        var expected = Render("image", mode, overprint, false, clipped, true, true,
            indexedSpots: false, imageSpace: space, imageBits: bits, imageSide: side,
            interpolate: interpolate);
        var actual = Render("image", mode, overprint, false, clipped, true, false,
            indexedSpots: false, imageSpace: space, imageBits: bits, imageSide: side,
            interpolate: interpolate);
        Assert.Empty(expected.Diagnostics);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Theory]
    [InlineData(8, 1, false)]
    [InlineData(8, 1, true)]
    [InlineData(8, 64, false)]
    [InlineData(8, 64, true)]
    [InlineData(16, 1, false)]
    [InlineData(16, 1, true)]
    [InlineData(16, 64, false)]
    [InlineData(16, 64, true)]
    public void PlainSpotBackground_MatchesIndependentNativeInk(int bits, int side, bool interpolate)
    {
        var expected = Render("background", 0, true, true, false, false, true,
            indexedSpots: false, imageBits: bits, imageSide: side, interpolate: interpolate);
        var actual = Render("background", 0, true, true, false, false, false,
            indexedSpots: false, imageBits: bits, imageSide: side, interpolate: interpolate);
        Assert.Empty(expected.Diagnostics);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        Assert.Equal(PdfDeviceCmykTests.RenderInk(255,255,0,0), actual.Pixels.Span[..4].ToArray());
    }

    [Theory]
    [InlineData("path", 0, false)]
    [InlineData("path", 1, false)]
    [InlineData("stroke", 0, false)]
    [InlineData("stroke", 1, false)]
    [InlineData("image", 0, false)]
    [InlineData("image", 1, false)]
    [InlineData("path", 0, true)]
    [InlineData("path", 1, true)]
    [InlineData("stroke", 0, true)]
    [InlineData("stroke", 1, true)]
    [InlineData("image", 0, true)]
    [InlineData("image", 1, true)]
    public void InvalidRgbPaint_DoesNotResurrectEarlierNativeInk(string foreground, int mode,
        bool followingSpot)
    {
        var actual = Render(foreground, mode, true, true, false, followingSpot, false,
            invalidRgbPaint: true);
        const int offset = (32 * 128 + 32) * 4;
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(followingSpot ? PdfDeviceCmykTests.RenderInk(0,0,255,0)
            : [255,255,255,255], actual.Pixels.Span[offset..(offset + 4)].ToArray());
    }

    public static TheoryData<int, int, bool, int, bool> MixedImageCases()
    {
        var data = new TheoryData<int, int, bool, int, bool>();
        foreach (int bits in new[] { 8, 16 })
        foreach (int side in new[] { 1, 64 })
        foreach (bool interpolate in new[] { false, true })
        foreach (int mode in new[] { 0, 1 })
        foreach (bool clipped in new[] { false, true })
            data.Add(bits, side, interpolate, mode, clipped);
        return data;
    }

    [Theory]
    [MemberData(nameof(MixedImageCases))]
    public void MixedProcessSpotImageEdges_MatchIndependentNativePlates(int bits, int side,
        bool interpolate, int mode, bool clipped)
    {
        var expected = Render("image", mode, true, false, clipped, true, true,
            indexedSpots: false, imageBits: bits, imageSide: side, interpolate: interpolate,
            mixedBackground: true);
        var actual = Render("image", mode, true, false, clipped, true, false,
            indexedSpots: false, imageBits: bits, imageSide: side, interpolate: interpolate,
            mixedBackground: true);
        Assert.Empty(expected.Diagnostics);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Theory]
    [InlineData(false, false, 8)]
    [InlineData(false, true, 8)]
    [InlineData(true, false, 8)]
    [InlineData(true, true, 8)]
    [InlineData(false, false, 16)]
    [InlineData(false, true, 16)]
    [InlineData(true, false, 16)]
    [InlineData(true, true, 16)]
    public void FractionalProcessEdgesBeforeSpotImage_RetainNativeShadow(bool indexed,
        bool clipped, int bits)
    {
        var expected = Render("background", 0, true, true, false, false, true,
            indexedSpots: indexed, imageBits: bits, processEdgeBeforeSpot: true,
            clipProcessEdge: clipped);
        var actual = Render("background", 0, true, true, false, false, false,
            indexedSpots: indexed, imageBits: bits, processEdgeBeforeSpot: true,
            clipProcessEdge: clipped);
        Assert.Empty(expected.Diagnostics);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 8)]
    [InlineData(false, 16)]
    [InlineData(true, 16)]
    public void FractionalGrayEdgesBeforeSpotImage_KeepRgbFallback(bool clipped, int bits)
    {
        var expected = Render("background", 0, true, true, false, false, false,
            imageBits: bits, processEdgeBeforeSpot: true, clipProcessEdge: clipped,
            processEdgeSpace: "DeviceRGB");
        var actual = Render("background", 0, true, true, false, false, false,
            imageBits: bits, processEdgeBeforeSpot: true, clipProcessEdge: clipped,
            processEdgeSpace: "DeviceGray");
        Assert.Empty(expected.Diagnostics);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    private static PdfRenderedPage Render(string foreground, int mode, bool overprint,
        bool white, bool clipped, bool followingSpot, bool nativeCmyk,
        bool indexedSpots = true, string imageSpace = "DeviceCMYK", int imageBits = 8,
        int imageSide = 1, bool interpolate = false, bool invalidRgbPaint = false,
        bool mixedBackground = false, bool processEdgeBeforeSpot = false,
        bool clipProcessEdge = false, string processEdgeSpace = "DeviceCMYK")
    {
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key),value);
        PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
        byte[] pixel = white ? [0,0,0,0] : [0,51,102,0];
        string operands = white ? "0 0 0 0" : "0 0.2 0.4 0";
        string paint = foreground switch
        {
            "path" => operands + " k 1 1 2 2 re f",
            "stroke" => operands + " K 2 w 2 1 m 2 3 l S",
            "image" => "q 2 0 0 2 1 1 cm /ProcessImage Do Q",
            "background" => "",
            _ => throw new ArgumentOutOfRangeException(nameof(foreground))
        };
        if (clipped) paint = "q 0.25 0.25 m 3.75 3.75 l 3.75 0.25 l h W n " + paint + " Q";
        if (followingSpot) paint += " /SpotOverprint gs q 2 0 0 2 1 1 cm /YellowImage Do Q";
        if (invalidRgbPaint) paint = "/Knockout gs 1 1 1 rg 1 1 2 2 re f /ProcessOverprint gs " + paint;
        string edgeColor = processEdgeSpace switch
        {
            "DeviceCMYK" => "1 0 1 0.5 k",
            "DeviceGray" => "0.5 g",
            "DeviceRGB" => "0.5 0.5 0.5 rg",
            _ => throw new ArgumentOutOfRangeException(nameof(processEdgeSpace))
        };
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(8,4,
            Encoding.ASCII.GetBytes("1 0 0 0 k 0 0 8 4 re f "
                + (processEdgeBeforeSpot ? "q "
                    + (clipProcessEdge ? "0.25 0.25 m 3.75 3.75 l 3.75 0.25 l h W n " : "")
                    + edgeColor + " 0.33 0.17 2.17 3.63 re f Q " : "")
                + "/SpotOverprint gs "
                + (mixedBackground ? "q 0.25 0.25 m 7.75 3.75 l 7.75 0.25 l h W n " : "")
                + "q 8 0 0 4 0 0 cm /MagentaImage Do Q "
                + (mixedBackground ? "Q " : "") + "/ProcessOverprint gs " + paint)).Build());
        var root = (PdfDictionary)document.Resolve((PdfIndirectReference)document.Trailer[Name("Root")]);
        var pages = (PdfDictionary)document.Resolve((PdfIndirectReference)root[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)document.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfObject Separation(string name, params int[] ink) => new PdfArray([Name("Separation"),
            Name(name),Name("DeviceCMYK"),new PdfDictionary([
                Entry("FunctionType",new PdfInteger(2)),Entry("Domain",Numbers(0,1)),
                Entry("C0",Numbers(0,0,0,0)),Entry("C1",Numbers(ink)),Entry("N",new PdfInteger(1))])]);
        PdfObject Image(string space, byte[] samples)
        {
            bool process = space == "Process";
            bool mixed = mixedBackground && space == "MagentaSpot";
            string sourceSpace = process ? imageSpace : mixed ? "MixedSpot" : space;
            bool indexed = process ? sourceSpace.StartsWith("Indexed", StringComparison.Ordinal)
                : indexedSpots;
            string baseSpace = sourceSpace switch
            {
                "IndexedCMYK" => "DeviceCMYK",
                "IndexedCyan" or "Cyan" => "CyanProcess",
                _ => sourceSpace
            };
            int side = process || !indexedSpots ? imageSide : 1;
            int bits = indexed ? 8 : imageBits;
            byte[] first = mixed ? [51,255] : process && baseSpace == "CyanProcess" ? [51] : samples;
            byte[] second = process && baseSpace == "CyanProcess" ? [153]
                : mixed ? [153,102] : process ? [102,0,0,51] : first;
            PdfObject definition = indexed
                ? new PdfArray([Name("Indexed"),Name(baseSpace),new PdfInteger(1),
                    new PdfString([..first, ..second],PdfStringForm.Hexadecimal)])
                : Name(baseSpace);
            var encoded = new List<byte>();
            for (int y = 0; y < side; y++)
                for (int x = 0; x < side; x++)
                {
                    bool alternate = (process || mixed) && (x + y) % 3 == 1;
                    if (indexed) encoded.Add(alternate ? (byte)1 : (byte)0);
                    else foreach (byte sample in alternate ? second : first)
                    {
                        encoded.Add(sample);
                        if (bits == 16) encoded.Add(sample);
                    }
                }
            return update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Type",Name("XObject")),Entry("Subtype",Name("Image")),
                Entry("Width",new PdfInteger(side)),Entry("Height",new PdfInteger(side)),
                Entry("BitsPerComponent",new PdfInteger(bits)),Entry("ColorSpace",definition),
                Entry("Interpolate",new PdfBoolean(interpolate))]), encoded.ToArray()));
        }
        PdfDictionary Policy(bool enabled, int policyMode) => new([
            Entry("op",new PdfBoolean(enabled)),Entry("OP",new PdfBoolean(enabled)),
            Entry("OPM",new PdfInteger(policyMode))]);
        var mixedFunction = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("FunctionType",new PdfInteger(4)),Entry("Domain",Numbers(0,1,0,1)),
            Entry("Range",Numbers(0,1,0,1,0,1,0,1))]), Encoding.ASCII.GetBytes("{ 0 exch 0 4 -1 roll }")));
        var resources = new PdfDictionary([
            Entry("ColorSpace",new PdfDictionary([Entry("MagentaSpot",Separation("CustomMagenta",0,1,0,0)),
                Entry("YellowSpot",Separation("CustomYellow",0,0,1,0)),
                Entry("CyanProcess",Separation("Cyan",1,0,0,0)),
                Entry("MixedSpot",new PdfArray([Name("DeviceN"),
                    new PdfArray([Name("Black"),Name("CustomMagenta")]),Name("DeviceCMYK"),mixedFunction]))])),
            Entry("XObject",new PdfDictionary([
                Entry("MagentaImage",Image("MagentaSpot",[255])),
                Entry("YellowImage",Image("YellowSpot",[255])),
                Entry("ProcessImage",Image("Process",pixel))])),
            Entry("ExtGState",new PdfDictionary([
                Entry("SpotOverprint",Policy(true,0)),Entry("ProcessOverprint",Policy(overprint,mode)),
                Entry("Knockout",Policy(false,0))]))]);
        IEnumerable<KeyValuePair<PdfName, PdfObject>> entries = page.Where(pair => !pair.Key.Equals(Name("Resources")))
            .Append(Entry("Resources",resources));
        if (nativeCmyk) entries = entries.Append(Entry("Group",new PdfDictionary([
            Entry("S",Name("Transparency")),Entry("CS",Name("DeviceCMYK"))])));
        update.ReplaceObject(reference.ObjectNumber,new PdfDictionary(entries));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,new PdfRenderOptions(128,64));
    }
}
