using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererImageLayoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedRawImagePreservesLaterPageContent(bool recovery)
    {
        AssertLaterContent(Create(recovery));
    }

    [Theory]
    [InlineData(89_478_486, 1, 8, null)]
    [InlineData(1, 715_827_883, 8, null)]
    [InlineData(int.MaxValue, int.MaxValue, 16, null)]
    [InlineData(int.MaxValue, 1, 16, "SMask")]
    [InlineData(1, int.MaxValue, 16, "SMask")]
    [InlineData(int.MaxValue, 1, 1, "Mask")]
    [InlineData(16, int.MaxValue, 1, "Mask")]
    public void OversizedRawLayoutsAndMasksPreserveLaterPageContent(int width, int height,
        int bits, string? mask)
    {
        foreach (bool recovery in new[] { false, true })
            AssertLaterContent(Create(recovery, width, height, bits, mask));
    }

    private static void AssertLaterContent(PdfDocument document)
    {
        PdfRenderedPage rendered = new PdfPageRenderer(document)
            .Render(0, new PdfRenderOptions(20, 10));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rendered.Pixels.Slice((5 * 20 + 5) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, rendered.Pixels.Slice((5 * 20 + 15) * 4, 4).ToArray());
        Assert.Contains("An image larger than the supported size was skipped.", rendered.Diagnostics);
    }

    private static PdfDocument Create(bool recovery, int width = 1_000_000_000,
        int height = 8, int bits = 8, string? mask = null)
    {
        byte[] content = Encoding.ASCII.GetBytes(
            "q 10 0 0 10 0 0 cm /Bad Do Q 0 0 1 rg 0 0 10 10 re f 0 1 0 rg 10 0 10 10 re f");
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(20, 10, content).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfDictionary invalid = D(("Subtype", N("Image")), ("Width", new PdfInteger(width)),
            ("Height", new PdfInteger(height)), ("BitsPerComponent", new PdfInteger(bits)),
            mask == "Mask" ? ("ImageMask", new PdfBoolean(true))
                : ("ColorSpace", N(mask is null ? "DeviceRGB" : "DeviceGray")));
        PdfIndirectReference image = update.AddObject(new PdfStream(invalid, new byte[] { 0 }));
        if (mask is not null)
            image = update.AddObject(new PdfStream(D(("Subtype", N("Image")),
                ("Width", new PdfInteger(1)), ("Height", new PdfInteger(1)),
                ("BitsPerComponent", new PdfInteger(8)), ("ColorSpace", N("DeviceRGB")),
                (mask, image)), new byte[] { 255, 0, 0 }));
        var changed = new PdfDictionary(page.Dictionary.Where(entry => !entry.Key.Equals(N("Resources")))
            .Append(new KeyValuePair<PdfName, PdfObject>(N("Resources"), D(("XObject", D(("Bad", image)))))));
        byte[] bytes = update.ReplaceObject(page.Reference.ObjectNumber, changed).Build();
        return recovery ? PdfDocument.OpenWithCompatibilityRecovery(bytes) : PdfDocument.Open(bytes);
    }

    private static PdfName N(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfDictionary D(params (string Name, PdfObject Value)[] values) =>
        new(values.Select(value => new KeyValuePair<PdfName, PdfObject>(N(value.Name), value.Value)));
}
