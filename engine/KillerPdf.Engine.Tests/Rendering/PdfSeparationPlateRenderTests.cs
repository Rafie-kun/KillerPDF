using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfSeparationPlateRenderTests
{
    // Three one-point cells: 50% cyan, a 60% spot whose alternate is 50% cyan and full yellow,
    // and RGB red, which prints as magenta and yellow.
    private const string Content =
        ".5 0 0 0 k 0 0 1 1 re f /Green cs .6 scn 1 0 1 1 re f 1 0 0 rg 2 0 1 1 re f";

    [Fact]
    public void ProcessPlates_ShowEachChannelAsGray()
    {
        PdfPageRenderer renderer = Renderer();
        PdfRenderedPage cyan = Render(renderer, "Cyan");
        PdfRenderedPage magenta = Render(renderer, "Magenta");

        Assert.InRange(Gray(cyan, 0), 125, 129);
        Assert.Equal(255, Gray(cyan, 2));
        Assert.Equal(255, Gray(magenta, 0));
        Assert.InRange(Gray(magenta, 2), 0, 2);
        Assert.True(cyan.Pixels.Span[1] == cyan.Pixels.Span[0] && cyan.Pixels.Span[2] == cyan.Pixels.Span[0]);
    }

    [Fact]
    public void SpotPlate_ShowsTheColorantTint()
    {
        PdfPageRenderer renderer = Renderer();
        PdfRenderedPage spot = Render(renderer, "GWG Green");

        Assert.Equal(255, Gray(spot, 0));
        Assert.InRange(Gray(spot, 1), 99, 105);
        Assert.Equal(255, Gray(spot, 2));
        Assert.Equal(255, Gray(Render(renderer, "Not On This Page"), 1));
    }

    [Fact]
    public void PlateRenders_DoNotReplaceTheCachedComposite()
    {
        PdfPageRenderer renderer = Renderer();
        var options = new PdfRenderOptions(3, 1, includeAnnotations: false, includeFormFields: false);
        PdfRenderedPage before = renderer.Render(0, options);
        Render(renderer, "Cyan");
        PdfRenderedPage after = renderer.Render(0, options);

        Assert.Equal(before.Pixels.ToArray(), after.Pixels.ToArray());
        Assert.NotEqual(Gray(before, 2), Blue(before, 2));
    }

    // An image mask filled with a spot color paints that spot's plate, as a vector fill of the
    // same color does. On CMYK pages its color used to lose the spot when converted to ink, so
    // the mask put the alternate's yellow on the Yellow plate and left its own plate blank
    // (round J, JF-002). The second mask is reduced, so its edges are partly covered.
    [Theory]
    [InlineData("/Green cs 1 scn 4 4 16 8 re f")]
    [InlineData("/Green cs 1 scn q 16 0 0 8 4 4 cm BI /W 16 /H 8 /IM true /BPC 1 ID ")]
    [InlineData("/Green cs 1 scn q 16 0 0 8 4 4 cm BI /W 64 /H 32 /IM true /BPC 1 ID ")]
    public void SpotFilledStencil_PaintsItsSpotPlate(string prefix)
    {
        bool image = prefix.Contains("BI");
        int bytes = prefix.Contains("/W 64") ? 8 * 32 : 2 * 8;
        byte[] content = image
            ? [.. Encoding.ASCII.GetBytes(prefix), .. new byte[bytes], .. Encoding.ASCII.GetBytes("\nEI Q")]
            : Encoding.ASCII.GetBytes(prefix);
        PdfPageRenderer renderer = Renderer(content, 24, 16);
        PdfRenderedPage spot = RenderPlate(renderer, "GWG Green", 24, 16);
        PdfRenderedPage yellow = RenderPlate(renderer, "Yellow", 24, 16);
        int Plate(PdfRenderedPage page, int x, int y) => page.Pixels.Span[(y * 24 + x) * 4 + 2];
        Assert.InRange(Plate(spot, 10, 8), 0, 5);
        Assert.Equal(255, Plate(yellow, 10, 8));
        Assert.Equal(255, Plate(spot, 1, 1));
    }

    // A reduced spot stencil whose ink ends partway through a pixel puts that share of the spot on
    // the plate there.
    [Fact]
    public void SpotFilledStencil_PartlyCoveredPixelGetsItsShareOfThePlate()
    {
        // 64 by 32 samples drawn 16 by 8: ink in columns 0 to 30, which end three quarters of the
        // way through pixel 11.
        var samples = new byte[8 * 32];
        for (int y = 0; y < 32; y++)
            for (int x = 31; x < 64; x++) samples[y * 8 + x / 8] |= (byte)(0x80 >> (x % 8));
        byte[] content = [.. Encoding.ASCII.GetBytes("/Green cs 1 scn q 16 0 0 8 4 4 cm BI /W 64 /H 32 /IM true /BPC 1 ID "),
            .. samples, .. Encoding.ASCII.GetBytes("\nEI Q")];
        PdfRenderedPage spot = RenderPlate(Renderer(content, 24, 16), "GWG Green", 24, 16);
        int Plate(int x) => spot.Pixels.Span[(8 * 24 + x) * 4 + 2];
        Assert.InRange(Plate(10), 0, 5);
        Assert.InRange(Plate(11), 255 - 191 - 8, 255 - 191 + 8);
        Assert.Equal(255, Plate(12));
    }

    private static PdfRenderedPage RenderPlate(PdfPageRenderer renderer, string plate, int width, int height) =>
        renderer.Render(0, new PdfRenderOptions(width, height, includeAnnotations: false, includeFormFields: false)
        {
            SeparationPlate = plate
        });

    private static PdfRenderedPage Render(PdfPageRenderer renderer, string plate) =>
        renderer.Render(0, new PdfRenderOptions(3, 1, includeAnnotations: false, includeFormFields: false)
        {
            SeparationPlate = plate
        });

    private static int Gray(PdfRenderedPage page, int x) => page.Pixels.Span[x * 4 + 2];

    private static int Blue(PdfRenderedPage page, int x) => page.Pixels.Span[x * 4];

    private static PdfPageRenderer Renderer() => Renderer(Encoding.ASCII.GetBytes(Content), 3, 1);

    private static PdfPageRenderer Renderer(byte[] content, int width, int height)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(width, height, content).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        PdfDictionary originalPage = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfDictionary tint = new([
            Entry("FunctionType", new PdfInteger(2)), Entry("Domain", new PdfArray([new PdfInteger(0), new PdfInteger(1)])),
            Entry("C0", new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0)])),
            Entry("C1", new PdfArray([new PdfReal(0.5), new PdfInteger(0), new PdfInteger(1), new PdfInteger(0)])),
            Entry("N", new PdfInteger(1))
        ]);
        PdfDictionary resources = new([
            Entry("ColorSpace", new PdfDictionary([
                Entry("Green", new PdfArray([Name("Separation"), Name("GWG Green"), Name("DeviceCMYK"), tint]))
            ]))
        ]);
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(originalPage
            .Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", resources))));
        return new PdfPageRenderer(PdfDocument.Open(update.Build()));
    }

    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);

    private static PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
}
