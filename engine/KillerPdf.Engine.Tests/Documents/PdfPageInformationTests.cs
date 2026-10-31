using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPageInformationTests
{
    [Theory]
    [InlineData(0.5)]
    [InlineData(1)]
    [InlineData(2)]
    public void RedactionKeepsItsRawCoordinatesWhenOutputUsesPhysicalPixels(double unit)
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(120, 240, System.Text.Encoding.ASCII.GetBytes("1 0 0 rg 10 20 100 200 re f"))
            .SetPageBox(0, PdfPageBox.Crop, 10, 20, 100, 200).SetPageUserUnit(0, unit).Build());
        var result = PdfContentRedaction.Apply(document, [new PdfRedactionRegion(0, new PdfContentBounds(20, 30, 40, 50))]);
        Assert.True(result.Verification.Succeeded);
        int width = (int)(100 * unit), height = (int)(200 * unit);
        var rendered = new KillerPdf.Engine.Rendering.PdfPageRenderer(PdfDocument.Open(result.Document))
            .Render(0, new KillerPdf.Engine.Rendering.PdfRenderOptions(width, height));
        int inside = ((int)(160 * unit) * width + (int)(30 * unit)) * 4;
        int outside = ((int)(100 * unit) * width + (int)(60 * unit)) * 4;
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, rendered.Pixels.Slice(inside, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rendered.Pixels.Slice(outside, 4).ToArray());
    }

    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(0.5, 90)]
    [InlineData(1, 90)]
    [InlineData(2, 90)]
    public void UserUnitSeparatesPhysicalSizeFromRawCropAndContentCoordinates(double unit, int rotation)
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(120, 240)
            .SetPageBox(0, PdfPageBox.Crop, 10, 20, 100, 200)
            .SetPageUserUnit(0, unit).SetPageRotation(0, rotation).Build());
        PdfPageInformation page = Assert.Single(PdfPageInformation.Read(document));
        Assert.Equal(10, page.Left);
        Assert.Equal(20, page.Bottom);
        Assert.Equal(100, page.Width);
        Assert.Equal(200, page.Height);
        Assert.Equal(unit, page.UserUnit);
        Assert.Equal(100 * unit, page.PhysicalWidth);
        Assert.Equal(200 * unit, page.PhysicalHeight);
        Assert.Equal(rotation, page.Rotation);
        PdfPageContent content = new PdfPageContentReader(document).Read(0);
        Assert.Equal(100, content.Width);
        Assert.Equal(200, content.Height);
        Assert.Equal(unit, content.UserUnit);
        PdfPartialRasterizationPlan plan = PdfPartialRasterization.Plan(content, new PdfContentBounds(0, 0, 20, 30), 144);
        Assert.Equal((int)(40 * unit), plan.PixelWidth);
        Assert.Equal((int)(60 * unit), plan.PixelHeight);
    }

    [Fact]
    public void Read_ReturnsEffectiveCropGeometryAndNormalizedRotation()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddPage(200, 300, ReadOnlyMemory<byte>.Empty)
            .AddPage(400, 500, ReadOnlyMemory<byte>.Empty)
            .Build();
        byte[] edited = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetCropBox(0, 10, 20, 100, 150)
            .SetRotation(0, 270)
            .Build();

        IReadOnlyList<PdfPageInformation> pages =
            PdfPageInformation.Read(PdfDocument.Open(edited));

        Assert.Equal(2, pages.Count);
        Assert.Equal(10, pages[0].Left);
        Assert.Equal(20, pages[0].Bottom);
        Assert.Equal(100, pages[0].Width);
        Assert.Equal(150, pages[0].Height);
        Assert.Equal(270, pages[0].Rotation);
        Assert.Equal(400, pages[1].Width);
        Assert.Equal(500, pages[1].Height);
        Assert.Equal(0, pages[1].Rotation);
    }

    [Fact]
    public void Read_CompatibilityRecoveryUsesLetterPageForMissingMediaBox()
    {
        PdfDocument strict = MissingMediaBoxDocument();

        Assert.Throws<InvalidOperationException>(() => PdfPageInformation.Read(strict));

        IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(
            PdfDocument.OpenWithCompatibilityRecovery(strict.Source));

        PdfPageInformation page = Assert.Single(pages);
        Assert.Equal(0, page.Left);
        Assert.Equal(0, page.Bottom);
        Assert.Equal(612, page.Width);
        Assert.Equal(792, page.Height);
    }

    [Fact]
    public void Read_CompatibilityRecoveryUsesMediaBoxForDegenerateCropBox()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(800, 600).Build());
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(source.Trailer[new PdfName("Root"u8)])));
        PdfDictionary pages = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[new PdfName("Pages"u8)])));
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[new PdfName("Kids"u8)])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary replacement = new(page.Append(
            new KeyValuePair<PdfName, PdfObject>(new PdfName("CropBox"u8),
                new PdfArray([new PdfInteger(0), new PdfInteger(0),
                    new PdfInteger(0), new PdfInteger(0)]))));
        PdfDocument strict = PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(pageReference.ObjectNumber, replacement).Build());

        Assert.Throws<InvalidOperationException>(() => PdfPageInformation.Read(strict));
        PdfPageInformation recovered = Assert.Single(PdfPageInformation.Read(
            PdfDocument.OpenWithCompatibilityRecovery(strict.Source)));

        Assert.Equal(0, recovered.Left);
        Assert.Equal(0, recovered.Bottom);
        Assert.Equal(800, recovered.Width);
        Assert.Equal(600, recovered.Height);
    }

    private static PdfDocument MissingMediaBoxDocument()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(200, 300).Build());
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(source.Trailer[new PdfName("Root"u8)])));
        PdfDictionary pages = Assert.IsType<PdfDictionary>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[new PdfName("Pages"u8)])));
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[new PdfName("Kids"u8)])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(source.Resolve(pageReference));
        PdfDictionary withoutMediaBox = new(page.Where(entry =>
            !entry.Key.Equals(new PdfName("MediaBox"u8))));

        return PdfDocument.Open(new PdfIncrementalUpdateBuilder(source)
            .ReplaceObject(pageReference.ObjectNumber, withoutMediaBox).Build());
    }
}
