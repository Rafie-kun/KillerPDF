using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfBlackPointDeviceIntentTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Render_DevicePaintCacheHonorsIntentChanges(bool rgb)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(3, 1,
            Encoding.ASCII.GetBytes("/F Do")).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var original = (PdfDictionary)source.Resolve(page.Reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
        string paint = rgb ? "0.25 0.25 0.25 rg" : "0.25 g";
        string content = "/On gs " + paint + " 0 0 1 1 re f /AbsoluteColorimetric ri "
            + paint + " 1 0 1 1 re f /RelativeColorimetric ri " + paint + " 2 0 1 1 re f";
        var profile = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(1))]),
            RaisedBlackGrayProfile()));
        var form = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Form")), Entry("BBox", Numbers(0, 0, 3, 1)),
            Entry("Resources", new PdfDictionary([Entry("ExtGState", new PdfDictionary([
                Entry("On", new PdfDictionary([Entry("UseBlackPtComp", Name("ON"))]))]))])),
            Entry("Group", new PdfDictionary([Entry("S", Name("Transparency")),
                Entry("I", new PdfBoolean(true)), Entry("CS", new PdfArray([Name("ICCBased"), profile]))]))]),
            Encoding.ASCII.GetBytes(content)));
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(original
            .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", new PdfDictionary([
                Entry("XObject", new PdfDictionary([Entry("F", form)]))])))));
        var result = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(24, 8));
        Assert.Empty(result.Diagnostics);
        byte[] pixels = result.Pixels.ToArray();
        for (int column = 0; column < 3; column++)
        {
            // Both unprofiled device spaces use the display's sRGB encoding.
            double input = Math.Pow((0.25 + 0.055) / 1.055, 2.4);
            double native = column == 1 ? Math.Clamp((input - 0.15) / 0.85, 0, 1) : input;
            double quantized = Math.Round(native * 255) / 255;
            double linear = 0.15 + 0.85 * quantized;
            int expected = (int)Math.Round(255 * (1.055 * Math.Pow(linear, 1 / 2.4) - 0.055));
            int offset = (4 * 24 + column * 8 + 4) * 4;
            for (int channel = 0; channel < 3; channel++)
                Assert.InRange((int)pixels[offset + channel], expected - 1, expected + 1);
        }
    }

    private static byte[] RaisedBlackGrayProfile()
    {
        byte[] bytes = new byte[216];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 0x02100000);
        "mntrGRAYXYZ "u8.CopyTo(bytes.AsSpan(12));
        "acsp"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(128), 2);
        WriteTag("kTRC", 132, 156, 40);
        WriteTag("wtpt", 144, 196, 20);
        "para"u8.CopyTo(bytes.AsSpan(156));
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(164), 4);
        double[] parameters = [1, 0.85, 0.15, 0.85, 0, 0, 0.15];
        for (int index = 0; index < parameters.Length; index++)
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(168 + index * 4), (int)Math.Round(parameters[index] * 65536));
        "XYZ "u8.CopyTo(bytes.AsSpan(196));
        double[] white = [0.9642, 1, 0.8249];
        for (int index = 0; index < white.Length; index++)
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(204 + index * 4), (int)Math.Round(white[index] * 65536));
        return bytes;

        void WriteTag(string name, int entry, int offset, int length)
        {
            Encoding.ASCII.GetBytes(name).CopyTo(bytes, entry);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 8), (uint)length);
        }
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
