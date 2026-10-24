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

    // The plane that smooths images on CMYK pages holds at most four million samples; a larger
    // image is averaged down to fit before it is read. An enlarged large scan there must keep
    // every sample, as it did before smoothing, not be drawn from a half-resolution average
    // (round H, HF-001). Alternate black and white columns enlarged twice: full resolution draws
    // two-pixel stripes, a half-resolution average draws gray.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnlargedImageTooLargeForThePlaneKeepsEverySample(bool interpolate)
    {
        const int width = 2050, height = 2000;
        byte[] row = [.. Enumerable.Range(0, width).Select(x => x % 2 == 0 ? (byte)0 : (byte)255)];
        byte[] samples = [.. Enumerable.Repeat(row, height).SelectMany(bytes => bytes)];
        string hint = interpolate ? " /I true" : "";
        byte[] content = [.. Encoding.ASCII.GetBytes($"q {width} 0 0 {height} 0 0 cm BI /W {width} /H {height} /BPC 8 /CS /G{hint} ID "),
            .. samples, .. Encoding.ASCII.GetBytes("\nEI Q")];
        PdfDocument document = CmykPage(PdfDocument.Open(new PdfDocumentBuilder().AddPage(width, height, content).Build()));
        PdfRenderedPage region = new PdfPageRenderer(document).RenderRegion(0,
            new PdfRenderOptions(2 * width, 2 * height, includeAnnotations: false, includeFormFields: false), 1000, 1000, 40, 4);
        int dark = 0, light = 0;
        for (int index = 0; index < region.Pixels.Length; index += 4)
        {
            int value = region.Pixels.Span[index];
            Assert.True(value <= 60 || value >= 215, $"Pixel {index / 4} is {value}: drawn from an averaged plane.");
            if (value <= 60) dark++; else light++;
        }
        Assert.True(dark > 0 && light > 0);
    }

    // An image too large for the plane that has a soft mask cannot take the full-resolution
    // copies, and the sampled copies read only every second sample, like the plane. It must be
    // smoothed from that plane, not drawn from those samples unsmoothed (round I, IF-001). A 4 by 4
    // mask, opaque except its far corner, keeps the interpolating device path from taking it on an
    // RGB page.
    [Fact]
    public void LargeMaskedImageIsStillSmoothedFromTheReducedPlane()
    {
        const int width = 2050, height = 2000;
        // Bands four samples wide survive the twofold plane; smoothing ramps between them.
        byte[] row = [.. Enumerable.Range(0, width).Select(x => x / 4 % 2 == 0 ? (byte)0 : (byte)255)];
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(width, height, Encoding.ASCII.GetBytes($"q {width} 0 0 {height} 0 0 cm /Im Do Q")).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(document).Pages[0];
        var original = Assert.IsType<PdfDictionary>(document.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(document);
        var mask = update.AddObject(new PdfStream(new PdfDictionary([
            new(Name("Subtype"), Name("Image")), new(Name("Width"), new PdfInteger(4)), new(Name("Height"), new PdfInteger(4)),
            new(Name("BitsPerComponent"), new PdfInteger(8)), new(Name("ColorSpace"), Name("DeviceGray"))]),
            [.. Enumerable.Repeat((byte)255, 15), 0]));
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            new(Name("Subtype"), Name("Image")), new(Name("Width"), new PdfInteger(width)), new(Name("Height"), new PdfInteger(height)),
            new(Name("BitsPerComponent"), new PdfInteger(8)), new(Name("ColorSpace"), Name("DeviceGray")), new(Name("SMask"), mask)]),
            Enumerable.Repeat(row, height).SelectMany(bytes => bytes).ToArray()));
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(original
            .Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(new(Name("Resources"), new PdfDictionary([new(Name("XObject"), new PdfDictionary([new(Name("Im"), image)]))])))));
        PdfRenderedPage region = new PdfPageRenderer(PdfDocument.Open(update.Build())).RenderRegion(0,
            new PdfRenderOptions(2 * width, 2 * height, includeAnnotations: false, includeFormFields: false), 1000, 1000, 40, 4);
        int ramp = 0;
        for (int index = 0; index < region.Pixels.Length; index += 4)
            if (region.Pixels.Span[index] is > 40 and < 215) ramp++;
        Assert.True(ramp >= 16, $"Only {ramp} of 160 pixels lie between the bands: the image was not smoothed.");
    }

    // A smoothed Separation image on a CMYK page must ramp its spot plate with its ink. The plate
    // took its tint from the nearest sample, so a pixel whose nearest sample was blank cleared it
    // and the ramp was cut on that side (round H, HF-004).
    [Fact]
    public void SmoothedSeparationImageRampsItsSpotPlate()
    {
        byte[] samples = [.. Enumerable.Repeat(Halves([0], [255]), 5).SelectMany(row => row)];
        byte[] content = [.. Encoding.ASCII.GetBytes("q 20 0 0 10 0 0 cm BI /W 8 /H 5 /BPC 8 /CS /Spot ID "),
            .. samples, .. Encoding.ASCII.GetBytes("\nEI Q")];
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(20, 10, content).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(document).Pages[0];
        var original = Assert.IsType<PdfDictionary>(document.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(document);
        static PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
        var separation = new PdfArray([Name("Separation"), Name("Spot"), Name("DeviceCMYK"), new PdfDictionary([
            new(Name("FunctionType"), new PdfInteger(2)), new(Name("Domain"), Numbers(0, 1)),
            new(Name("C0"), Numbers(0, 0, 0, 0)), new(Name("C1"), Numbers(0, 1, 0, 0)), new(Name("N"), new PdfInteger(1))])]);
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(original
            .Where(entry => !entry.Key.Equals(Name("Group")) && !entry.Key.Equals(Name("Resources")))
            .Append(new(Name("Resources"), new PdfDictionary([new(Name("ColorSpace"), new PdfDictionary([new(Name("Spot"), separation)]))])))
            .Append(new(Name("Group"), new PdfDictionary([
                new(Name("S"), Name("Transparency")), new(Name("CS"), Name("DeviceCMYK"))])))));
        PdfRenderedPage plate = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions(20, 10, includeAnnotations: false, includeFormFields: false) { SeparationPlate = "Spot" });

        double full = 255 - plate.Pixels.Span[(5 * 20 + 19) * 4];
        Assert.True(full > 200, $"The spot plate shows no ink ({full}).");
        double[] row = [0, 0, 0, 0, 1, 1, 1, 1];
        for (int x = 0; x < 20; x++)
        {
            double sampleX = Math.Clamp((x + 0.5) / 20 * 8 - 0.5, 0, 7);
            int x0 = (int)sampleX, x1 = Math.Min(x0 + 1, 7);
            double expected = row[x0] + (row[x1] - row[x0]) * (sampleX - x0);
            double actual = (255 - plate.Pixels.Span[(5 * 20 + x) * 4]) / full;
            Assert.True(Math.Abs(actual - expected) <= 0.04, $"Pixel {x} has tint {actual:F2}, expected {expected:F2}.");
        }
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

    private static PdfRenderedPage Render(PdfDocument document, bool cmykPage) =>
        new PdfPageRenderer(cmykPage ? CmykPage(document) : document)
            .Render(0, new PdfRenderOptions(20, 10, includeAnnotations: false, includeFormFields: false));

    // The first page gets a CMYK page group, so the page is drawn in ink.
    private static PdfDocument CmykPage(PdfDocument document)
    {
        PdfPageTreeEntry page = PdfPageTree.Read(document).Pages[0];
        var original = Assert.IsType<PdfDictionary>(document.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(document);
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(original
            .Where(entry => !entry.Key.Equals(Name("Group")))
            .Append(new(Name("Group"), new PdfDictionary([
                new(Name("S"), Name("Transparency")), new(Name("CS"), Name("DeviceCMYK"))])))));
        return PdfDocument.Open(update.Build());
    }

    private static PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
}
