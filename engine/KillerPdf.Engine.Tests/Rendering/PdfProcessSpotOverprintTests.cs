using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfProcessSpotOverprintTests
{
    private static readonly string[] Spaces = ["DeviceRGB", "CalRGB", "Lab", "ICCBased",
        "IndexedRGB", "IndexedCalRGB", "IndexedLab", "IndexedICC"];

    public static IEnumerable<object[]> Cases()
    {
        foreach (string space in Spaces)
        foreach (string foreground in new[] { "path", "stroke", "image", "mask" })
        foreach (int mode in new[] { 0, 1 })
            yield return [space, foreground, mode];
    }

    public static IEnumerable<object[]> TextCases()
    {
        foreach (string space in Spaces)
        foreach (int mode in new[] { 0, 1 })
            yield return [space, mode];
    }

    [Theory]
    [MemberData(nameof(TextCases))]
    public void WhiteProcessText_PreservesSpotAndReplacesProcess(string space, int mode)
    {
        var background = Render(space, "background", mode, true);
        var overprint = Render(space, "text", mode, true);
        var knockout = Render(space, "text", mode, false);
        int clearedSpotPixels = 0;
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 128; x++)
        {
            int offset = (y * 128 + x) * 4;
            var expected = x < 64 ? background : knockout;
            Assert.Equal(expected.Pixels.Span[offset..(offset + 4)].ToArray(),
                overprint.Pixels.Span[offset..(offset + 4)].ToArray());
            if (x < 64 && knockout.Pixels.Span[offset..(offset + 4)].SequenceEqual(new byte[] {255,255,255,255}))
                clearedSpotPixels++;
        }
        Assert.True(clearedSpotPixels > 16);
        Assert.Empty(overprint.Diagnostics);
        Assert.Empty(knockout.Diagnostics);
    }

    public static IEnumerable<object[]> StencilCases()
    {
        foreach (string space in Spaces)
        foreach (int mode in new[] { 0, 1 })
        foreach (bool clipped in new[] { false, true })
            yield return [space, mode, clipped];
    }

    [Theory]
    [MemberData(nameof(StencilCases))]
    public void ProcessColoredStencil_MatchesVectorOverprint(string space, int mode, bool clipped)
    {
        var vector = Render(space, "path", mode, true, false, clipped: clipped);
        var stencil = Render(space, "mask", mode, true, false, clipped: clipped);
        Assert.Equal(vector.Pixels.ToArray(), stencil.Pixels.ToArray());
        Assert.Empty(stencil.Diagnostics);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void WhiteProcessPaint_PreservesNamedSpotAndClearsProcess(
        string space, string foreground, int mode)
    {
        var background = Render(space, "background", mode, true);
        var overprint = Render(space, foreground, mode, true);
        var knockout = Render(space, foreground, mode, false);
        const int spot = (32 * 128 + 32) * 4;
        const int process = (32 * 128 + 96) * 4;
        byte[] Pixel(PdfRenderedPage page, int offset) => page.Pixels.Span[offset..(offset + 4)].ToArray();
        Assert.Equal(Pixel(background, spot), Pixel(overprint, spot));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(overprint, process));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(knockout, spot));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(knockout, process));
        Assert.Empty(overprint.Diagnostics);
        Assert.Empty(knockout.Diagnostics);
    }

    public static IEnumerable<object[]> ImageCases()
    {
        foreach (string space in Spaces)
        foreach (int mode in new[] { 0, 1 })
        foreach (int bits in new[] { 8, 16 })
        foreach (bool interpolate in new[] { false, true })
        foreach (int sourceSize in new[] { 1, 64 })
        foreach (bool clipped in new[] { false, true })
            yield return [space, mode, bits, interpolate, sourceSize, clipped];
    }

    [Theory]
    [MemberData(nameof(ImageCases))]
    public void ColoredProcessImage_MatchesVectorOverprint(string space, int mode,
        int bits, bool interpolate, int sourceSize, bool clipped)
    {
        var vector = Render(space, "path", mode, true, false, bits, interpolate, sourceSize, clipped);
        var image = Render(space, "image", mode, true, false, bits, interpolate, sourceSize, clipped);
        var knockout = Render(space, "image", mode, false, false, bits, interpolate, sourceSize, clipped);
        Assert.Equal(vector.Pixels.ToArray(), image.Pixels.ToArray());
        Assert.Empty(vector.Diagnostics);
        Assert.Empty(image.Diagnostics);
        Assert.Empty(knockout.Diagnostics);
        if (!clipped)
        {
            const int spot = (32 * 128 + 32) * 4;
            const int process = (32 * 128 + 96) * 4;
            Assert.Equal(knockout.Pixels.Span[process..(process + 4)].ToArray(),
                knockout.Pixels.Span[spot..(spot + 4)].ToArray());
            Assert.NotEqual(knockout.Pixels.Span[spot..(spot + 4)].ToArray(),
                image.Pixels.Span[spot..(spot + 4)].ToArray());
        }
    }

    private static PdfRenderedPage Render(string space, string foreground, int mode, bool overprint,
        bool whitePaint = true, int bits = 8, bool interpolate = false, int sourceSize = 1, bool clipped = false)
    {
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
        PdfArray Reals(params double[] values) => new(values.Select(value => (PdfObject)new PdfReal(value)));
        bool indexed = space.StartsWith("Indexed", StringComparison.Ordinal);
        string baseSpace = space switch
        {
            "IndexedRGB" => "DeviceRGB", "IndexedCalRGB" => "CalRGB",
            "IndexedLab" => "Lab", "IndexedICC" => "ICCBased", _ => space
        };
        byte[] pixel = baseSpace == "Lab" ? whitePaint ? [255,128,128] : [204,150,150]
            : whitePaint ? [255,255,255] : [217,217,128];
        double[] values = baseSpace == "Lab" ? [pixel[0] / 255d * 100, pixel[1] - 128, pixel[2] - 128]
            : pixel.Select(value => value / 255d).ToArray();
        string operands = indexed ? "0" : string.Join(" ", values.Select(value => value.ToString("R",
            System.Globalization.CultureInfo.InvariantCulture)));
        string paint = foreground switch
        {
            "path" => "/Process cs " + operands + " scn 1 1 2 2 re f 5 1 2 2 re f",
            "stroke" => "/Process CS " + operands + " SCN 2 w 2 1 m 2 3 l S 6 1 m 6 3 l S",
            "text" => "/Process cs " + operands + " scn BT /F 2 Tf 1 1 Td (H) Tj ET BT /F 2 Tf 5 1 Td (H) Tj ET",
            "image" => "q 2 0 0 2 1 1 cm /Im Do Q q 2 0 0 2 5 1 cm /Im Do Q",
            "mask" => "/Process cs " + operands + " scn q 2 0 0 2 1 1 cm /Im Do Q q 2 0 0 2 5 1 cm /Im Do Q",
            _ => ""
        };
        if (clipped) paint = "q 0.25 0.25 m 7.75 3.75 l 7.75 0.25 l h W n " + paint + " Q";
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(8, 4,
            Encoding.ASCII.GetBytes("/Spot cs 1 scn 0 0 4 4 re f 1 0 0 0 k 4 0 4 4 re f /O gs " + paint)).Build());
        var root = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)root[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfObject processDefinition = baseSpace switch
        {
            "CalRGB" => new PdfArray([Name("CalRGB"), new PdfDictionary([
                Entry("WhitePoint", Reals(.9642, 1, .8249)),
                Entry("Matrix", Reals(.4360747,.2225045,.0139322,.3850649,.7168786,.0971045,.1430604,.0606169,.7138633))])]),
            "Lab" => new PdfArray([Name("Lab"), new PdfDictionary([
                Entry("WhitePoint", Reals(.9642, 1, .8249)), Entry("Range", Numbers(-128,127,-128,127))])]),
            "ICCBased" => new PdfArray([Name("ICCBased"), update.AddObject(new PdfStream(
                new PdfDictionary([Entry("N", new PdfInteger(3))]), RgbProfile()))]),
            _ => Name("DeviceRGB")
        };
        if (indexed) processDefinition = new PdfArray([Name("Indexed"), processDefinition,
            new PdfInteger(0), new PdfString(pixel, PdfStringForm.Hexadecimal)]);
        var imageEntries = new List<KeyValuePair<PdfName, PdfObject>>
        {
            Entry("Type", Name("XObject")), Entry("Subtype", Name("Image")),
            Entry("Width", new PdfInteger(sourceSize)), Entry("Height", new PdfInteger(sourceSize)),
            Entry("BitsPerComponent", new PdfInteger(bits)), Entry("ColorSpace", Name("Process")),
            Entry("Interpolate", new PdfBoolean(interpolate))
        };
        if (baseSpace == "Lab" && !indexed) imageEntries.Add(Entry("Decode", Numbers(0,100,-128,127,-128,127)));
        if (foreground == "mask")
        {
            imageEntries.RemoveAll(pair => pair.Key.Equals(Name("ColorSpace"))
                || pair.Key.Equals(Name("Decode")) || pair.Key.Equals(Name("BitsPerComponent")));
            imageEntries.Add(Entry("BitsPerComponent", new PdfInteger(1)));
            imageEntries.Add(Entry("ImageMask", new PdfBoolean(true)));
        }
        byte[] samples = new byte[sourceSize * sourceSize * pixel.Length * (bits / 8)];
        for (int index = 0; index < samples.Length; index++)
            samples[index] = pixel[index / (bits / 8) % pixel.Length];
        if (indexed) samples = new byte[sourceSize * sourceSize * (bits / 8)];
        if (foreground == "mask") samples = new byte[(sourceSize + 7) / 8 * sourceSize];
        var image = update.AddObject(new PdfStream(new PdfDictionary(imageEntries), samples));
        var resources = new PdfDictionary([
            Entry("ColorSpace", new PdfDictionary([Entry("Process", processDefinition), Entry("Spot", new PdfArray([
                Name("Separation"), Name("Custom"), Name("DeviceCMYK"), new PdfDictionary([
                    Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0,1)),
                    Entry("C0", Numbers(0,0,0,0)), Entry("C1", Numbers(0,1,0,0)), Entry("N", new PdfInteger(1))])]))])),
            Entry("ExtGState", new PdfDictionary([Entry("O", new PdfDictionary([
                Entry("op", new PdfBoolean(overprint)), Entry("OP", new PdfBoolean(overprint)), Entry("OPM", new PdfInteger(mode))]))])),
            Entry("XObject", new PdfDictionary([Entry("Im", image)])),
            Entry("Font", new PdfDictionary([Entry("F", new PdfDictionary([
                Entry("Type", Name("Font")), Entry("Subtype", Name("Type1")), Entry("BaseFont", Name("Helvetica"))]))]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", resources)).Append(Entry("Group", new PdfDictionary([
                Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))])))));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(128,64));
    }

    private static byte[] RgbProfile()
    {
        byte[] Xyz(params double[] values)
        {
            byte[] tag = new byte[20];
            "XYZ "u8.CopyTo(tag);
            for (int index = 0; index < 3; index++)
                BinaryPrimitives.WriteInt32BigEndian(tag.AsSpan(8 + index * 4),
                    (int)Math.Round(values[index] * 65536));
            return tag;
        }
        byte[] curve = new byte[14];
        "curv"u8.CopyTo(curve);
        BinaryPrimitives.WriteUInt32BigEndian(curve.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(12), 256);
        (string Name, byte[] Data)[] tags = [
            ("rTRC", curve), ("gTRC", curve), ("bTRC", curve),
            ("rXYZ", Xyz(.4360747,.2225045,.0139322)),
            ("gXYZ", Xyz(.3850649,.7168786,.0971045)),
            ("bXYZ", Xyz(.1430604,.0606169,.7138633)),
            ("wtpt", Xyz(.9642,1,.8249))];
        byte[] profile = new byte[132 + tags.Length * 12 + tags.Sum(tag => (tag.Data.Length + 3) & ~3)];
        BinaryPrimitives.WriteUInt32BigEndian(profile, (uint)profile.Length);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(8), 0x02100000);
        "mntrRGB XYZ "u8.CopyTo(profile.AsSpan(12));
        "acsp"u8.CopyTo(profile.AsSpan(36));
        Xyz(.9642,1,.8249).AsSpan(8).CopyTo(profile.AsSpan(68));
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(128), (uint)tags.Length);
        int offset = 132 + tags.Length * 12;
        for (int index = 0; index < tags.Length; index++)
        {
            int entry = 132 + index * 12;
            Encoding.ASCII.GetBytes(tags[index].Name).CopyTo(profile, entry);
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(entry + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(entry + 8), (uint)tags[index].Data.Length);
            tags[index].Data.CopyTo(profile, offset);
            offset += (tags[index].Data.Length + 3) & ~3;
        }
        return profile;
    }
}
