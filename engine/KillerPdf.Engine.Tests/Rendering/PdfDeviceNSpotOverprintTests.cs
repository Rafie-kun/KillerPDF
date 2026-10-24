using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// An overprinting DeviceN color paints only the colorants it names and never paints None, so
// process plates underneath stay (ISO 32000-1 8.6.7). DeviceN colors made only of spot and None
// colorants knocked the process plates out (altona technical rows R and T); each must look like
// the equivalent Separation, which already overprinted correctly.
public sealed class PdfDeviceNSpotOverprintTests
{
    // Overprint is simulated on pages that blend in CMYK: an output intent or a DeviceCMYK page
    // group. Plain RGB pages simulate only the single-spot cases their preflight models, as
    // Acrobat previews overprint by default only for PDF/X files.
    public static TheoryData<string, string, bool> Cases()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (bool native in new[] { true })
        {
            data.Add("/RedNone cs 1 1 scn", "/Red cs 1 scn", native);
            data.Add("/RedNone cs 1 0 scn", "/Red cs 1 scn", native);
            data.Add("/BlueRed cs 1 0 scn", "/Blue cs 1 scn", native);
            // Both spots at full tint paint both inks, like the two Separations overprinted in turn.
            data.Add("/BlueRed cs 1 1 scn", "/Blue cs 1 scn 0 0 1 1 re f /Red cs 1 scn", native);
            data.Add("/BlueRed cs 0.5 1 scn", "/Blue cs 0.5 scn 0 0 1 1 re f /Red cs 1 scn", native);
        }
        return data;
    }

    // Native pages blend in a DeviceCMYK group; the others render RGB with simulated spot plates.
    [Theory]
    [MemberData(nameof(Cases))]
    public void SpotOnlyDeviceNOverprintKeepsProcessPlates(string deviceN, string separation, bool native)
    {
        byte[] expected = Render(separation, native);
        byte[] actual = Render(deviceN, native);
        Assert.Equal(expected, actual);
        // The black backdrop, which no alternate here contains, survives under the spot ink.
        Assert.NotEqual(Render(separation, native, overprint: false), actual);
    }

    private static byte[] Render(string paint, bool native, bool overprint = true)
    {
        string content = "0 0 0 0.5 k 0 0 1 1 re f q /" + (overprint ? "On" : "Off") + " gs " + paint + " 0 0 1 1 re f Q";
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(1, 1, Encoding.ASCII.GetBytes(content)).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        PdfDictionary originalPage = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference Function(string program, int inputs) => update.AddObject(new PdfStream(
            new PdfDictionary([
                Entry("FunctionType", new PdfInteger(4)),
                Entry("Domain", Numbers(inputs == 1 ? [0, 1] : [0, 1, 0, 1])),
                Entry("Range", Numbers(0, 1, 0, 1, 0, 1, 0, 1))
            ]), Encoding.ASCII.GetBytes(program)));
        static PdfDictionary Overprint(bool enabled) => new([
            Entry("OP", new PdfBoolean(enabled)), Entry("op", new PdfBoolean(enabled)),
            Entry("OPM", new PdfInteger(1))
        ]);
        PdfDictionary resources = new([
            Entry("ColorSpace", new PdfDictionary([
                Entry("Red", new PdfArray([Name("Separation"), Name("MyRed"), Name("DeviceCMYK"),
                    Function("{ 0 exch dup 0 }", 1)])),
                Entry("Blue", new PdfArray([Name("Separation"), Name("MyBlue"), Name("DeviceCMYK"),
                    Function("{ 0 0 0 }", 1)])),
                Entry("RedNone", new PdfArray([Name("DeviceN"), new PdfArray([Name("MyRed"), Name("None")]),
                    Name("DeviceCMYK"), Function("{ pop 0 exch dup 0 }", 2)])),
                Entry("BlueRed", new PdfArray([Name("DeviceN"), new PdfArray([Name("MyBlue"), Name("MyRed")]),
                    Name("DeviceCMYK"), Function("{ dup 0 }", 2)]))
            ])),
            Entry("ExtGState", new PdfDictionary([Entry("On", Overprint(true)), Entry("Off", Overprint(false))]))
        ]);
        PdfDictionary group = new([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))]);
        IEnumerable<KeyValuePair<PdfName, PdfObject>> entries = originalPage
            .Where(entry => !entry.Key.Equals(Name("Resources")) && !entry.Key.Equals(Name("Group")))
            .Append(Entry("Resources", resources));
        if (native) entries = entries.Append(Entry("Group", group));
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(entries));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions(1, 1, includeAnnotations: false, includeFormFields: false)).Pixels.ToArray();
    }

    private static PdfArray Numbers(params int[] values) =>
        new(values.Select(value => (PdfObject)new PdfInteger(value)));

    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);

    private static PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
}
