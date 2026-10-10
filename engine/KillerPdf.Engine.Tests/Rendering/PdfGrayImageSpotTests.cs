using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfGrayImageSpotTests
{
    public static TheoryData<string, int, int, bool, byte, int> Cases()
    {
        var data = new TheoryData<string, int, int, bool, byte, int>();
        foreach (int mode in new[] { 0, 1 })
        foreach (int bits in new[] { 1, 8 })
        foreach (bool interpolate in new[] { false, true })
        foreach (byte gray in bits == 1 ? (byte[])[0, 255] : [0, 64, 128, 254, 255])
        foreach (int sourceSize in new[] { 1, 64 })
            data.Add("DeviceCMYK", mode, bits, interpolate, gray, sourceSize);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void GrayImage_OverprintsNamedSpotAndReplacesProcessInk(
        string destination, int mode, int bits, bool interpolate, byte gray, int sourceSize)
    {
        var background = Render(destination, mode, bits, interpolate, true, "background", gray, sourceSize);
        var vector = Render(destination, mode, bits, interpolate, true, "vector", gray, sourceSize);
        var image = Render(destination, mode, bits, interpolate, true, "image", gray, sourceSize);
        var knockout = Render(destination, mode, bits, interpolate, false, "image", gray, sourceSize);
        const int spot = (32 * 128 + 32) * 4;
        const int process = (32 * 128 + 96) * 4;
        static byte[] Pixel(PdfRenderedPage page, int offset) => page.Pixels.Span[offset..(offset + 4)].ToArray();
        if (gray == 255)
        {
            Assert.Equal(Pixel(background, spot), Pixel(vector, spot));
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(vector, process));
        }
        Assert.Equal(Pixel(vector, spot), Pixel(image, spot));
        Assert.Equal(Pixel(vector, process), Pixel(image, process));
        Assert.Equal(vector.Pixels.ToArray(), image.Pixels.ToArray());
        Assert.Equal(Pixel(vector, process), Pixel(knockout, spot));
        Assert.Equal(Pixel(vector, process), Pixel(knockout, process));
        Assert.Empty(image.Diagnostics);
        Assert.Empty(knockout.Diagnostics);
    }

    public static TheoryData<string, int, byte> ManagedCases()
    {
        var data = new TheoryData<string, int, byte>();
        foreach (string space in new[] { "CalGray", "ICCBased" })
        foreach (int mode in new[] { 0, 1 })
        foreach (byte gray in new byte[] { 0, 128, 255 })
            data.Add(space, mode, gray);
        return data;
    }

    [Theory]
    [MemberData(nameof(ManagedCases))]
    public void ManagedGrayImage_MatchesVectorOverprint(string space, int mode, byte gray)
    {
        var vector = Render("DeviceCMYK", mode, 8, false, true, "vector", gray, 1, space);
        var image = Render("DeviceCMYK", mode, 8, false, true, "image", gray, 1, space);
        Assert.Equal(vector.Pixels.ToArray(), image.Pixels.ToArray());
        Assert.Empty(vector.Diagnostics);
        Assert.Empty(image.Diagnostics);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 128)]
    [InlineData(0, 255)]
    [InlineData(1, 0)]
    [InlineData(1, 128)]
    [InlineData(1, 255)]
    public void ClippedGrayImage_MatchesVectorOverprint(int mode, byte gray)
    {
        var vector = Render("DeviceCMYK", mode, 8, false, true, "vector", gray, 1, clipped: true);
        var image = Render("DeviceCMYK", mode, 8, false, true, "image", gray, 1, clipped: true);
        Assert.Equal(vector.Pixels.ToArray(), image.Pixels.ToArray());
        Assert.Empty(image.Diagnostics);
    }

    private static PdfRenderedPage Render(string destination, int mode, int bits,
        bool interpolate, bool overprint, string foreground, byte gray, int sourceSize,
        string graySpace = "DeviceGray", bool clipped = false)
    {
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
        string grayNumber = (gray / 255d).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        string paint = foreground switch
        {
            "image" => "q 2 0 0 2 1 1 cm /Im Do Q q 2 0 0 2 5 1 cm /Im Do Q",
            "vector" => "/Gray cs " + grayNumber + " scn 1 1 2 2 re f 5 1 2 2 re f",
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
        PdfObject grayDefinition = graySpace switch
        {
            "CalGray" => new PdfArray([Name("CalGray"), new PdfDictionary([
                Entry("WhitePoint", new PdfArray([new PdfReal(.9642), new PdfInteger(1), new PdfReal(.8249)])),
                Entry("Gamma", new PdfReal(2.2))])]),
            "ICCBased" => new PdfArray([Name("ICCBased"), update.AddObject(new PdfStream(
                new PdfDictionary([Entry("N", new PdfInteger(1))]), GrayProfile()))]),
            _ => Name("DeviceGray")
        };
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Type", Name("XObject")), Entry("Subtype", Name("Image")),
            Entry("Width", new PdfInteger(sourceSize)), Entry("Height", new PdfInteger(sourceSize)),
            Entry("BitsPerComponent", new PdfInteger(bits)), Entry("ColorSpace", Name("Gray")),
            Entry("Interpolate", new PdfBoolean(interpolate))]),
            Enumerable.Repeat(bits == 1 ? gray == 0 ? (byte)0 : (byte)255 : gray,
                (sourceSize * bits + 7) / 8 * sourceSize).ToArray()));
        var resources = new PdfDictionary([
            Entry("ColorSpace", new PdfDictionary([Entry("Gray", grayDefinition), Entry("Spot", new PdfArray([
                Name("Separation"), Name("Custom"), Name("DeviceCMYK"), new PdfDictionary([
                    Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
                    Entry("C0", Numbers(0, 0, 0, 0)), Entry("C1", Numbers(0, 1, 0, 0)),
                    Entry("N", new PdfInteger(1))])]))])),
            Entry("ExtGState", new PdfDictionary([Entry("O", new PdfDictionary([
                Entry("op", new PdfBoolean(overprint)), Entry("OP", new PdfBoolean(overprint)),
                Entry("OPM", new PdfInteger(mode))]))])),
            Entry("XObject", new PdfDictionary([Entry("Im", image)]))]);
        IEnumerable<KeyValuePair<PdfName, PdfObject>> entries = page
            .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources));
        if (destination != "None")
            entries = entries.Append(Entry("Group", new PdfDictionary([
                Entry("S", Name("Transparency")), Entry("CS", Name(destination))])));
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(entries));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(128, 64));
    }

    internal static byte[] GrayProfile()
    {
        byte[] profile = new byte[192];
        BinaryPrimitives.WriteUInt32BigEndian(profile, (uint)profile.Length);
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(8), 0x02100000);
        "mntrGRAYXYZ "u8.CopyTo(profile.AsSpan(12));
        "acsp"u8.CopyTo(profile.AsSpan(36));
        double[] white = [.9642, 1, .8249];
        for (int index = 0; index < 3; index++)
        {
            int component = (int)Math.Round(white[index] * 65536);
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(68 + index * 4), component);
            BinaryPrimitives.WriteInt32BigEndian(profile.AsSpan(180 + index * 4), component);
        }
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(128), 2);
        WriteTag("kTRC", 132, 156, 14);
        WriteTag("wtpt", 144, 172, 20);
        "curv"u8.CopyTo(profile.AsSpan(156));
        BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(164), 1);
        BinaryPrimitives.WriteUInt16BigEndian(profile.AsSpan(168), (ushort)Math.Round(2.2 * 256));
        "XYZ "u8.CopyTo(profile.AsSpan(172));
        return profile;

        void WriteTag(string signature, int entry, uint offset, uint length)
        {
            Encoding.ASCII.GetBytes(signature).CopyTo(profile, entry);
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(entry + 4), offset);
            BinaryPrimitives.WriteUInt32BigEndian(profile.AsSpan(entry + 8), length);
        }
    }
}
