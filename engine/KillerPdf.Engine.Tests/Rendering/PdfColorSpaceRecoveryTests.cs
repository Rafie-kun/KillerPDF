using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using System.Text;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfColorSpaceRecoveryTests
{
    [Fact]
    public void Render_RecoversMissingNamedFillColorSpaceAndContinuesPainting()
    {
        byte[] content = Encoding.ASCII.GetBytes(
            "0 1 0 rg 0 0 5 10 re f /CS0 cs 0.5 sc 5 0 5 10 re f");
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(
            PdfDocumentWriter.Write(AddPageColorSpaceResources(source)));

        PdfRenderedPage page = new PdfPageRenderer(recovered).Render(0,
            new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(page, 2, 5));
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, Pixel(page, 7, 5));
        Assert.Contains(page.Diagnostics, diagnostic => diagnostic.Contains("color space",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Render_RecoversMalformedNamedStrokeColorSpace()
    {
        byte[] content = Encoding.ASCII.GetBytes(
            "4 w /CS0 CS 0.5 SC 1 1 8 8 re S");
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());
        PdfDocument malformed = AddPageColorSpaceResources(source,
            Entry("CS0", new PdfArray([Name("UnknownSpace"), Name("DeviceGray")])));
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(
            PdfDocumentWriter.Write(malformed));

        PdfRenderedPage page = new PdfPageRenderer(recovered).Render(0,
            new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false));

        Assert.Equal(new byte[] { 128, 128, 128, 255 }, Pixel(page, 1, 5));
        Assert.Contains(page.Diagnostics, diagnostic => diagnostic.Contains("color space",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Render_StrictModeRejectsMissingNamedColorSpace()
    {
        byte[] content = Encoding.ASCII.GetBytes(
            "/CS0 cs 0.5 sc 0 0 10 10 re f");
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(10, 10, content).Build());
        PdfDocument malformed = AddPageColorSpaceResources(source);

        Assert.Throws<NotSupportedException>(() => new PdfPageRenderer(malformed).Render(0,
            new PdfRenderOptions(10, 10, includeAnnotations: false, includeFormFields: false)));
    }

    private static PdfDocument AddPageColorSpaceResources(PdfDocument source,
        params KeyValuePair<PdfName, PdfObject>[] spaces)
    {
        PdfDictionary catalog = ResolveDictionary(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(source, catalog[Name("Pages")]);
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = ResolveDictionary(source, pageReference);
        PdfDictionary resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var colorSpaces = new PdfDictionary(spaces);
        var updatedResources = new PdfDictionary(resources.Append(Entry("ColorSpace", colorSpaces)));
        var updatedPage = new PdfDictionary(page
            .Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", updatedResources)));
        return PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(pageReference.ObjectNumber, updatedPage).Build());
    }

    private static PdfDictionary ResolveDictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(value is PdfIndirectReference reference
            ? document.Resolve(reference) : value);

    private static byte[] Pixel(PdfRenderedPage page, int x, int y) =>
        page.Pixels.Span.Slice((y * page.Width + x) * 4, 4).ToArray();

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) =>
        new(Name(name), value);
}
