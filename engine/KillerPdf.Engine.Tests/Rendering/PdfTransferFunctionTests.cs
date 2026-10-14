using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfTransferFunctionTests
{
    [Fact]
    public void SingleTransferFunctionMapsEveryFillComponent()
    {
        byte[] pixel = RenderPixel("/T gs 1 0.25 0 rg 0 0 10 10 re f", State("TR", Invert()));
        Assert.Equal(new byte[] { 255, 191, 0, 255 }, pixel);
    }

    [Fact]
    public void TransferArrayMapsRedGreenAndBlueSeparately()
    {
        var functions = new PdfArray([Invert(), Name("Identity"), Invert(), Name("Identity")]);
        byte[] pixel = RenderPixel("/T gs 1 0.25 0 rg 0 0 10 10 re f", State("TR", functions));
        Assert.Equal(new byte[] { 255, 64, 0, 255 }, pixel);
    }

    [Fact]
    public void TransferSecondEntryOverridesTheFirst()
    {
        var state = new PdfDictionary([Entry("TR", Invert()), Entry("TR2", Name("Default"))]);
        byte[] pixel = RenderPixel("/T gs 1 0.25 0 rg 0 0 10 10 re f", state);
        Assert.Equal(new byte[] { 0, 64, 255, 255 }, pixel);
    }

    [Fact]
    public void TransferFunctionAppliesToImageSamples()
    {
        var image = new PdfStream(new PdfDictionary([
            Entry("Type", Name("XObject")), Entry("Subtype", Name("Image")),
            Entry("Width", new PdfInteger(1)), Entry("Height", new PdfInteger(1)),
            Entry("ColorSpace", Name("DeviceRGB")), Entry("BitsPerComponent", new PdfInteger(8))]),
            [255, 64, 0]);
        byte[] pixel = RenderPixel("/T gs q 10 0 0 10 0 0 cm /Im Do Q", State("TR", Invert()), image);
        Assert.Equal(new byte[] { 255, 191, 0, 255 }, pixel);
    }

    [Fact]
    public void LaterIdentityTransferRestoresColors()
    {
        var resources = new PdfDictionary([Entry("T", State("TR", Invert())), Entry("I", State("TR", Name("Identity")))]);
        byte[] pixel = RenderPixel("/T gs /I gs 1 0.25 0 rg 0 0 10 10 re f", null, null, resources);
        Assert.Equal(new byte[] { 0, 64, 255, 255 }, pixel);
    }

    private static byte[] RenderPixel(string content, PdfDictionary? state, PdfStream? image = null,
        PdfDictionary? states = null)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10,
            Encoding.ASCII.GetBytes(content)).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var entries = new List<KeyValuePair<PdfName, PdfObject>>
        {
            Entry("ExtGState", states ?? new PdfDictionary([Entry("T", state!)]))
        };
        if (image is not null)
            entries.Add(Entry("XObject", new PdfDictionary([Entry("Im", update.AddObject(image))])));
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page
            .Where(pair => !pair.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", new PdfDictionary(entries)))));
        var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));
        Assert.Empty(rendered.Diagnostics);
        return rendered.Pixels.Slice((5 * 10 + 5) * 4, 4).ToArray();
    }

    // Exponential function mapping x to 1 - x.
    private static PdfDictionary Invert() => new([
        Entry("FunctionType", new PdfInteger(2)),
        Entry("Domain", new PdfArray([new PdfInteger(0), new PdfInteger(1)])),
        Entry("C0", new PdfArray([new PdfInteger(1)])),
        Entry("C1", new PdfArray([new PdfInteger(0)])),
        Entry("N", new PdfInteger(1))]);

    private static PdfDictionary State(string key, PdfObject value) => new([Entry(key, value)]);
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
