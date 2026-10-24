using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// Owner decision, 2026-10-23: enlarged continuous-tone images are smoothed, as 1.8 and most
// viewers show them, once they are enlarged about one and a half times; 1-bit images and image
// masks stay crisp because their edges are the content (text scans, barcodes). An explicit
// /Interpolate true is honored at any enlargement. Like 1.8 and Poppler, smoothing stops once an
// image covers about eight pixels per sample (swatches, QR codes, pixel art).
public sealed class PdfImageSmoothingTests
{
    // Eight by five samples, dark on the left half, drawn 2.5 times wider and twice as tall:
    // smoothing ramps across the middle.
    [Theory]
    [InlineData("/BPC 8 /CS /G", new byte[] { 0x00 }, new byte[] { 0xFF }, false)]
    [InlineData("/BPC 8 /CS /G", new byte[] { 0x00 }, new byte[] { 0xFF }, true)]
    [InlineData("/BPC 8 /CS /RGB", new byte[] { 0, 0, 0 }, new byte[] { 255, 255, 255 }, false)]
    [InlineData("/BPC 8 /CS /RGB", new byte[] { 0, 0, 0 }, new byte[] { 255, 255, 255 }, true)]
    [InlineData("/BPC 8 /CS [/I /RGB 1 <000000FFFFFF>]", new byte[] { 0x00 }, new byte[] { 0x01 }, false)]
    [InlineData("/BPC 8 /CS /CMYK", new byte[] { 0, 0, 0, 255 }, new byte[] { 0, 0, 0, 0 }, false)]
    [InlineData("/BPC 8 /CS /CMYK", new byte[] { 0, 0, 0, 255 }, new byte[] { 0, 0, 0, 0 }, true)]
    public void EnlargedContinuousToneImagesAreSmoothed(string header, byte[] dark, byte[] light, bool cmykPage)
    {
        byte[] samples = [.. Enumerable.Repeat(Halves(dark, light), 5).SelectMany(row => row)];
        Assert.True(Ramp(Render($"q 20 0 0 10 0 0 cm BI /W 8 /H 5 {header} ID ", samples, cmykPage)) >= 2);
    }

    [Theory]
    [InlineData("/BPC 1 /CS /G")]
    [InlineData("/IM true")]
    public void EnlargedOneBitImagesAndMasksStayCrisp(string header)
    {
        // Bits 0 then 1 on every row: black then white for the gray image, painted then clear
        // for the mask.
        Assert.Equal(0, Ramp(Render($"0 g q 20 0 0 10 0 0 cm BI /W 8 /H 5 {header} ID ",
            [0x0F, 0x0F, 0x0F, 0x0F, 0x0F], false)));
    }

    // Ten alternating samples drawn twelve pixels wide are enlarged only 1.2 times.
    [Fact]
    public void SlightEnlargementStaysCrispUnlessInterpolationIsRequested()
    {
        byte[] stripes = [.. Enumerable.Repeat<byte[]>([0, 255, 0, 255, 0, 255, 0, 255, 0, 255], 8)
            .SelectMany(row => row)];
        Assert.Equal(0, Ramp(Render("q 12 0 0 10 4 0 cm BI /W 10 /H 8 /BPC 8 /CS /G ID ", stripes, false)));
        Assert.True(Ramp(Render("q 12 0 0 10 4 0 cm BI /W 10 /H 8 /BPC 8 /CS /G /I true ID ", stripes, false)) >= 4);
    }

    // Two samples drawn 20 by 10 pixels cover a hundred pixels each, far past 1.8's limit.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HugelyEnlargedImageStaysCrispUnlessInterpolationIsRequested(bool cmykPage)
    {
        Assert.Equal(0, Ramp(Render("q 20 0 0 10 0 0 cm BI /W 2 /H 1 /BPC 8 /CS /G ID ", [0x00, 0xFF], cmykPage)));
        Assert.True(Ramp(Render("q 20 0 0 10 0 0 cm BI /W 2 /H 1 /BPC 8 /CS /G /I true ID ",
            [0x00, 0xFF], cmykPage)) >= 6);
    }

    // A soft mask hides the black right half. Smoothing must fade the visible gray into the page,
    // never blend in the hidden black, which drew a dark outline around masked images (GWG169).
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmoothedMaskedImageDoesNotShowItsHiddenColor(bool cmykPage)
    {
        byte[] rgba = [.. Enumerable.Repeat(Halves([128, 128, 128, 255], [0, 0, 0, 0]), 5).SelectMany(row => row)];
        PdfImage image = PdfImage.FromRgba(8, 5, rgba);
        PdfRenderedPage rendered = Render(PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(20, 10, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 20, 10)).Build()), cmykPage);
        int gray = rendered.Pixels.Span[(5 * 20 + 1) * 4];
        Assert.InRange(gray, 100, 160);
        for (int x = 0; x < 20; x++)
            Assert.True(rendered.Pixels.Span[(5 * 20 + x) * 4] >= gray - 3,
                $"Pixel {x} is darker than the visible gray: the hidden black bled in.");
    }

    // One image row: four dark samples, then four light ones.
    private static byte[] Halves(byte[] dark, byte[] light) =>
        [.. Enumerable.Repeat(dark, 4).SelectMany(sample => sample), .. Enumerable.Repeat(light, 4).SelectMany(sample => sample)];

    // Pixels on the middle row strictly between that row's darkest and lightest values, so a
    // CMYK black that converts to a dark gray still counts as the dark end.
    private static int Ramp(PdfRenderedPage page)
    {
        int darkest = 255, lightest = 0;
        for (int x = 0; x < page.Width; x++)
        {
            int value = page.Pixels.Span[(5 * page.Width + x) * 4];
            darkest = Math.Min(darkest, value);
            lightest = Math.Max(lightest, value);
        }
        int count = 0;
        for (int x = 0; x < page.Width; x++)
        {
            int value = page.Pixels.Span[(5 * page.Width + x) * 4];
            if (value > darkest + 20 && value < lightest - 20) count++;
        }
        return count;
    }

    private static PdfRenderedPage Render(string prefix, byte[] samples, bool cmykPage)
    {
        byte[] content = [.. Encoding.ASCII.GetBytes(prefix), .. samples, .. Encoding.ASCII.GetBytes("\nEI Q")];
        return Render(PdfDocument.Open(new PdfDocumentBuilder().AddPage(20, 10, content).Build()), cmykPage);
    }

    private static PdfRenderedPage Render(PdfDocument document, bool cmykPage)
    {
        if (cmykPage)
        {
            PdfPageTreeEntry page = PdfPageTree.Read(document).Pages[0];
            var original = Assert.IsType<PdfDictionary>(document.Resolve(page.Reference));
            var update = new PdfIncrementalUpdateBuilder(document);
            update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(original
                .Where(entry => !entry.Key.Equals(Name("Group")))
                .Append(new(Name("Group"), new PdfDictionary([
                    new(Name("S"), Name("Transparency")), new(Name("CS"), Name("DeviceCMYK"))])))));
            document = PdfDocument.Open(update.Build());
        }
        return new PdfPageRenderer(document)
            .Render(0, new PdfRenderOptions(20, 10, includeAnnotations: false, includeFormFields: false));
    }

    private static PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
}
