using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfIccProcessSpotTests
{
    private static readonly string[] Spaces = ["ICCBased", "IndexedICC"];

    public static TheoryData<string, string, int> Cases()
    {
        var data = new TheoryData<string, string, int>();
        foreach (string space in Spaces)
        foreach (string foreground in new[] { "path", "stroke", "image", "mask" })
        foreach (int mode in new[] { 0, 1 })
            data.Add(space, foreground, mode);
        return data;
    }

    public static TheoryData<string, int> TextCases()
    {
        var data = new TheoryData<string, int>();
        foreach (string space in Spaces)
        foreach (int mode in new[] { 0, 1 })
            data.Add(space, mode);
        return data;
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

    public static TheoryData<string, int, bool> StencilCases()
    {
        var data = new TheoryData<string, int, bool>();
        foreach (string space in Spaces)
        foreach (int mode in new[] { 0, 1 })
        foreach (bool clipped in new[] { false, true })
            data.Add(space, mode, clipped);
        return data;
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
    public void WhiteProcessPaintWithoutOverprint_ClearsBothInks(string space, string foreground, int mode)
    {
        var page = Render(space, foreground, mode, false);
        Assert.Empty(page.Diagnostics);
        foreach (int x in new[] { 32, 96 })
        {
            int offset = (32 * 128 + x) * 4;
            Assert.Equal(new byte[] {255,255,255,255}, page.Pixels.Span[offset..(offset + 4)].ToArray());
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void WhiteProcessPaint_PreservesNamedSpotAndClearsProcess(
        string space, string foreground, int mode)
    {
        var background = Render(space, "background", mode, true);
        var overprint = Render(space, foreground, mode, true);
        var knockout = Render(space, foreground, mode, false);
        Assert.Empty(overprint.Diagnostics);
        Assert.Empty(knockout.Diagnostics);
        const int spot = (32 * 128 + 32) * 4;
        const int process = (32 * 128 + 96) * 4;
        static byte[] Pixel(PdfRenderedPage page, int offset) => page.Pixels.Span[offset..(offset + 4)].ToArray();
        Assert.Equal(Pixel(background, spot), Pixel(overprint, spot));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(overprint, process));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(knockout, spot));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(knockout, process));
        Assert.Empty(overprint.Diagnostics);
        Assert.Empty(knockout.Diagnostics);
    }

    public static TheoryData<string, int, int, bool, int, bool, double> ImageCases()
    {
        var data = new TheoryData<string, int, int, bool, int, bool, double>();
        foreach (string space in Spaces)
        foreach (int mode in new[] { 0, 1 })
        foreach (int bits in new[] { 8, 16 })
        foreach (bool interpolate in new[] { false, true })
        foreach (int sourceSize in new[] { 1, 64 })
        foreach (bool clipped in new[] { false, true })
        foreach (double alpha in new[] { 1d, .5d })
            data.Add(space, mode, bits, interpolate, sourceSize, clipped, alpha);
        return data;
    }

    [Theory]
    [MemberData(nameof(ImageCases))]
    public void ColoredProcessImage_MatchesVectorOverprint(string space, int mode,
        int bits, bool interpolate, int sourceSize, bool clipped, double alpha)
    {
        var vector = Render(space, "path", mode, true, false, bits, interpolate, sourceSize, clipped, alpha);
        var image = Render(space, "image", mode, true, false, bits, interpolate, sourceSize, clipped, alpha);
        var knockout = Render(space, "image", mode, false, false, bits, interpolate, sourceSize, clipped, alpha);
        Assert.Equal(vector.Pixels.ToArray(), image.Pixels.ToArray());
        Assert.Empty(vector.Diagnostics);
        Assert.Empty(image.Diagnostics);
        Assert.Empty(knockout.Diagnostics);
        if (!clipped && alpha == 1)
        {
            const int spot = (32 * 128 + 32) * 4;
            const int process = (32 * 128 + 96) * 4;
            Assert.Equal(knockout.Pixels.Span[process..(process + 4)].ToArray(),
                knockout.Pixels.Span[spot..(spot + 4)].ToArray());
            Assert.NotEqual(knockout.Pixels.Span[spot..(spot + 4)].ToArray(),
                image.Pixels.Span[spot..(spot + 4)].ToArray());
        }
    }

    public static TheoryData<string, int, bool> NativeCases()
    {
        var data = new TheoryData<string, int, bool>();
        foreach (string foreground in new[] { "path", "stroke", "mask", "image", "text" })
        foreach (int mode in new[] { 0, 1 })
        foreach (bool compensate in new[] { false, true })
            data.Add(foreground, mode, compensate);
        return data;
    }

    [Theory]
    [MemberData(nameof(NativeCases))]
    public void MatchingDestinationProfile_UsesNativeCmykOverprint(string foreground, int mode, bool compensate)
    {
        var expected = Render("ICCBased", foreground, mode, true, matchingProfile: true, nativePaint: true);
        var actual = Render("ICCBased", foreground, mode, true, matchingProfile: true, compensate: compensate);
        Assert.Empty(expected.Diagnostics);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        if (foreground != "text")
        {
            var background = Render("ICCBased", "background", mode, true, matchingProfile: true);
            const int spot = (32 * 128 + 32) * 4;
            const int process = (32 * 128 + 96) * 4;
            Assert.Equal(background.Pixels.Span[spot..(spot + 4)].ToArray(),
                actual.Pixels.Span[spot..(spot + 4)].ToArray());
            byte[] processPixel = actual.Pixels.Span[process..(process + 4)].ToArray();
            if (mode == 1 && foreground != "image")
                Assert.Equal(background.Pixels.Span[process..(process + 4)].ToArray(), processPixel);
            else Assert.Equal(new byte[] {255,255,255,255}, processPixel);
        }
    }

    private static PdfRenderedPage Render(string space, string foreground, int mode, bool overprint,
        bool whitePaint = true, int bits = 8, bool interpolate = false, int sourceSize = 1, bool clipped = false,
        double alpha = 1, bool matchingProfile = false, bool nativePaint = false, bool compensate = false)
    {
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
        bool indexed = space.StartsWith("Indexed", StringComparison.Ordinal);
        byte[] pixel = whitePaint ? [0,0,0,0] : [0,51,102,0];
        double[] values = [.. pixel.Select(value => value / 255d)];
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
        string beforePolicy = compensate ? "/Process cs /Process CS " : "";
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(8, 4,
            Encoding.ASCII.GetBytes("/Spot cs 1 scn 0 0 4 4 re f 1 0 0 0 k 4 0 4 4 re f " + beforePolicy + "/O gs " + paint)).Build());
        var root = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)root[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfObject profileDefinition = new PdfArray([Name("ICCBased"), update.AddObject(new PdfStream(
            new PdfDictionary([Entry("N", new PdfInteger(4))]), CmykProfile()))]);
        PdfObject processDefinition = nativePaint ? Name("DeviceCMYK") : profileDefinition;
        if (indexed) processDefinition = new PdfArray([Name("Indexed"), processDefinition,
            new PdfInteger(0), new PdfString(pixel, PdfStringForm.Hexadecimal)]);
        var imageEntries = new List<KeyValuePair<PdfName, PdfObject>>
        {
            Entry("Type", Name("XObject")), Entry("Subtype", Name("Image")),
            Entry("Width", new PdfInteger(sourceSize)), Entry("Height", new PdfInteger(sourceSize)),
            Entry("BitsPerComponent", new PdfInteger(bits)), Entry("ColorSpace", Name("Process")),
            Entry("Interpolate", new PdfBoolean(interpolate))
        };
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
                Entry("op", new PdfBoolean(overprint)), Entry("OP", new PdfBoolean(overprint)), Entry("OPM", new PdfInteger(mode)),
                Entry("UseBlackPtComp", Name(compensate ? "ON" : "OFF")),
                Entry("ca", new PdfReal(alpha)), Entry("CA", new PdfReal(alpha))]))])),
            Entry("XObject", new PdfDictionary([Entry("Im", image)])),
            Entry("Font", new PdfDictionary([Entry("F", new PdfDictionary([
                Entry("Type", Name("Font")), Entry("Subtype", Name("Type1")), Entry("BaseFont", Name("Helvetica"))]))]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", resources)).Append(Entry("Group", new PdfDictionary([
                Entry("S", Name("Transparency")), Entry("CS", matchingProfile ? profileDefinition : Name("DeviceCMYK"))])))));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(128,64));
    }

    private static byte[] CmykProfile()
    {
        const int entries = 256;
        byte[] table = new byte[52 + 2 * (7 * entries + 16 * 3)];
        "mft2"u8.CopyTo(table);
        table[8] = 4;
        table[9] = 3;
        table[10] = 2;
        for (int row = 0; row < 3; row++)
            BinaryPrimitives.WriteInt32BigEndian(table.AsSpan(12 + row * 16), 65536);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(48), entries);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(50), entries);
        int offset = 52;
        void Write(ushort value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(offset), value);
            offset += 2;
        }
        for (int channel = 0; channel < 4; channel++)
            for (int index = 0; index < entries; index++) Write((ushort)(index * 257));
        // Independent ICC v2 CMYK-to-Lab table, with additive neutral ink coverage.
        for (int cell = 0; cell < 16; cell++)
        {
            int inkCount = (cell & 1) + ((cell >> 1) & 1) + ((cell >> 2) & 1) + ((cell >> 3) & 1);
            Write((ushort)(65280 * (4 - inkCount) / 4));
            Write(32768);
            Write(32768);
        }
        for (int channel = 0; channel < 3; channel++)
            for (int index = 0; index < entries; index++) Write((ushort)(index * 257));
        byte[] reverse = new byte[52 + 2 * (7 * entries + 8 * 4)];
        "mft2"u8.CopyTo(reverse);
        reverse[8] = 3;
        reverse[9] = 4;
        reverse[10] = 2;
        for (int row = 0; row < 3; row++)
            BinaryPrimitives.WriteInt32BigEndian(reverse.AsSpan(12 + row * 16), 65536);
        BinaryPrimitives.WriteUInt16BigEndian(reverse.AsSpan(48), entries);
        BinaryPrimitives.WriteUInt16BigEndian(reverse.AsSpan(50), entries);
        int reverseOffset = 52;
        void WriteReverse(ushort value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(reverse.AsSpan(reverseOffset), value);
            reverseOffset += 2;
        }
        for (int channel = 0; channel < 3; channel++)
            for (int index = 0; index < entries; index++) WriteReverse((ushort)(index * 257));
        for (int cell = 0; cell < 8; cell++)
        {
            WriteReverse(0);
            WriteReverse(0);
            WriteReverse(0);
            WriteReverse((ushort)((cell & 4) == 0 ? 65535 : 0));
        }
        for (int channel = 0; channel < 4; channel++)
            for (int index = 0; index < entries; index++) WriteReverse((ushort)(index * 257));
        byte[] whitePoint = new byte[20];
        "XYZ "u8.CopyTo(whitePoint);
        double[] xyz = [.9642,1,.8249];
        for (int index = 0; index < 3; index++)
            BinaryPrimitives.WriteInt32BigEndian(whitePoint.AsSpan(8 + index * 4),
                (int)Math.Round(xyz[index] * 65536));
        (string Name, byte[] Data)[] tags = [("A2B0", table), ("A2B1", table), ("A2B2", table),
            ("B2A0", reverse), ("B2A1", reverse), ("B2A2", reverse), ("wtpt", whitePoint)];
        byte[] profile = new byte[132 + tags.Length * 12 + tags.Sum(tag => (tag.Data.Length + 3) & ~3)];
        BinaryPrimitives.WriteUInt32BigEndian(profile, (uint)profile.Length);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(8), 0x02100000);
        "prtrCMYKLab "u8.CopyTo(profile.AsSpan(12));
        "acsp"u8.CopyTo(profile.AsSpan(36));
        whitePoint.AsSpan(8).CopyTo(profile.AsSpan(68));
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(128), (uint)tags.Length);
        offset = 132 + tags.Length * 12;
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
