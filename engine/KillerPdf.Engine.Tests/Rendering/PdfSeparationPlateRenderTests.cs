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

    private static PdfRenderedPage Render(PdfPageRenderer renderer, string plate) =>
        renderer.Render(0, new PdfRenderOptions(3, 1, includeAnnotations: false, includeFormFields: false)
        {
            SeparationPlate = plate
        });

    private static int Gray(PdfRenderedPage page, int x) => page.Pixels.Span[x * 4 + 2];

    private static int Blue(PdfRenderedPage page, int x) => page.Pixels.Span[x * 4];

    private static PdfPageRenderer Renderer()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(3, 1, Encoding.ASCII.GetBytes(Content)).Build());
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
