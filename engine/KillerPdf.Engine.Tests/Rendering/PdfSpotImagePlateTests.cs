using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// An image whose colors carry a spot paints that spot's plate, as a vector fill of the same color
// does. The image plane holds ink only, so several image kinds lost their spot on the way and
// knocked out where they should overprint (rounds J and K, A3 open item).
public sealed class PdfSpotImagePlateTests
{
    // A DeviceN image with a named spot: process plus spot, two spots with and without colorant
    // definitions, and a spot among None. Drawn at its own size, then reduced fourfold into the
    // averaged plane. The left half holds every colorant at full tint, the right half none.
    [Theory]
    [InlineData("Mixed", "CustomMagenta", 1)]
    [InlineData("Mixed", "Cyan", 1)]
    [InlineData("Pair", "MyBlue", 1)]
    [InlineData("Pair", "MyRed", 1)]
    [InlineData("Defined", "MyBlue", 1)]
    [InlineData("Defined", "MyRed", 1)]
    [InlineData("RedNone", "MyRed", 1)]
    [InlineData("Mixed", "CustomMagenta", 4)]
    [InlineData("Pair", "MyRed", 4)]
    [InlineData("Defined", "MyBlue", 4)]
    [InlineData("RedNone", "MyRed", 4)]
    public void ADeviceNImageWithASpotPaintsItsPlates(string space, string plate, int reduction)
    {
        byte[] content = HalfInkedImage(space, reduction, components: 2);
        PdfRenderedPage page = Render(content, plate);
        Assert.InRange(Gray(page, 4, 4), 0, 5);
        Assert.Equal(255, Gray(page, 12, 4));
    }

    // Enlarged twofold, the image is smoothed, and its spot plate ramps across the edge with its
    // ink instead of stepping at the nearest sample.
    [Fact]
    public void ASmoothedDeviceNImageRampsItsSpotPlate()
    {
        PdfRenderedPage page = Render(HalfInkedImage("Mixed", 0.5, components: 2), "CustomMagenta");
        Assert.InRange(Gray(page, 2, 4), 0, 5);
        Assert.InRange(Gray(page, 7, 4), 40, 90);
        Assert.InRange(Gray(page, 8, 4), 165, 215);
        Assert.Equal(255, Gray(page, 13, 4));
    }

    // An Indexed image over two spots paints both plates, at its own size and reduced. A palette
    // over spots defined only by their tint transform used to be stripped to its alternate process
    // colors (altona technical row T); one over defined spots lost its second spot.
    [Theory]
    [InlineData("PairPalette", "MyBlue", 1)]
    [InlineData("PairPalette", "MyRed", 1)]
    [InlineData("DefinedPalette", "MyRed", 1)]
    [InlineData("PairPalette", "MyBlue", 4)]
    [InlineData("PairPalette", "MyRed", 4)]
    [InlineData("DefinedPalette", "MyRed", 4)]
    public void ATwoSpotPaletteImagePaintsBothPlates(string space, string plate, int reduction)
    {
        PdfRenderedPage page = Render(HalfInkedImage(space, reduction, components: 1, paletteIndex: 1), plate);
        Assert.InRange(Gray(page, 4, 4), 0, 5);
        Assert.Equal(255, Gray(page, 12, 4));
    }

    // The composite shows both inks of a two-spot palette entry, the same as a vector fill of that
    // color. With only its first spot carried, the image painted MyBlue alone.
    [Theory]
    [InlineData("PairPalette", "Pair")]
    [InlineData("DefinedPalette", "Defined")]
    public void ATwoSpotPaletteImageComposesLikeItsVectorFill(string palette, string space)
    {
        PdfRenderedPage image = Render(HalfInkedImage(palette, 1, components: 1, paletteIndex: 1), null);
        PdfRenderedPage vector = Render(Encoding.ASCII.GetBytes($"/{space} cs 1 1 scn 0 0 8 8 re f"), null);
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(image.Pixels.Span[(4 * 16 + 4) * 4 + channel] - vector.Pixels.Span[(4 * 16 + 4) * 4 + channel], -2, 2);
    }

    // A reduced Separation image that is not opaque paints its plate. Only factor 1 or an opaque
    // image used to name its spot, so a soft-masked or color-keyed scan reduced onto the page left
    // its plate blank. The soft mask leaves the bottom quarter half transparent, where the plate
    // takes half the tint over the white page.
    [Theory]
    [InlineData("/SMask", 4)]
    [InlineData("/SMask", 1)]
    [InlineData("/Mask", 4)]
    public void ASeparationImageThatIsNotOpaquePaintsItsPlate(string mask, int reduction)
    {
        int width = 16 * reduction, height = 8 * reduction;
        var samples = new byte[width * height];
        var alpha = new byte[width * height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                samples[y * width + x] = x < width / 2 ? (byte)255 : (byte)0;
                alpha[y * width + x] = y < height * 3 / 4 ? (byte)255 : (byte)128;
            }
        PdfRenderedPage page = Render(Encoding.ASCII.GetBytes("q 16 0 0 8 0 0 cm /Im Do Q"), "MyRed",
            xObjects: update =>
            {
                List<KeyValuePair<PdfName, PdfObject>> image =
                [
                    Entry("Type", Name("XObject")), Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(width)),
                    Entry("Height", new PdfInteger(height)), Entry("BitsPerComponent", new PdfInteger(8)),
                    Entry("ColorSpace", Name("Red"))
                ];
                if (mask == "/Mask") image.Add(Entry("Mask", Numbers(0, 0)));
                else image.Add(Entry("SMask", update.AddObject(new PdfStream(new PdfDictionary([
                    Entry("Type", Name("XObject")), Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(width)),
                    Entry("Height", new PdfInteger(height)), Entry("BitsPerComponent", new PdfInteger(8)),
                    Entry("ColorSpace", Name("DeviceGray"))]), alpha))));
                return [Entry("Im", update.AddObject(new PdfStream(new PdfDictionary(image), samples)))];
            });
        Assert.InRange(Gray(page, 4, 2), 0, 5);
        Assert.Equal(255, Gray(page, 12, 2));
        if (mask == "/SMask") Assert.InRange(Gray(page, 4, 7), 120, 136);
    }

    // An image mask filled with a spot color paints that spot's plate, as a vector fill of the same
    // color does. On CMYK pages its color lost the spot when converted to ink, so the mask put the
    // alternate's yellow on the Yellow plate and left its own plate blank (round J, JF-002). The
    // third mask is reduced, so its edges are partly covered.
    [Theory]
    [InlineData("0 0 16 8 re f")]
    [InlineData("BI /W 16 /H 8 /IM true /BPC 1 ID ")]
    [InlineData("BI /W 64 /H 32 /IM true /BPC 1 ID ")]
    public void ASpotFilledStencilPaintsItsSpotPlate(string paint)
    {
        byte[] content = Stencil(paint, new byte[paint.Contains("/W 64") ? 8 * 32 : 2 * 8]);
        Assert.InRange(Gray(Render(content, "MyRed", 24, 16), 10, 8), 0, 5);
        Assert.Equal(255, Gray(Render(content, "Yellow", 24, 16), 10, 8));
        Assert.Equal(255, Gray(Render(content, "MyRed", 24, 16), 1, 1));
    }

    // A reduced spot stencil whose ink ends partway through a pixel puts that share of the spot on
    // its plate there.
    [Fact]
    public void APartlyCoveredSpotStencilPixelGetsItsShareOfThePlate()
    {
        PdfRenderedPage plate = Render(Stencil("BI /W 64 /H 32 /IM true /BPC 1 ID ", InkedColumns(31)), "MyRed", 24, 16);
        Assert.InRange(Gray(plate, 10, 8), 0, 5);
        Assert.InRange(Gray(plate, 11, 8), 255 - 191 - 8, 255 - 191 + 8);
        Assert.Equal(255, Gray(plate, 12, 8));
    }

    // A spot stencil's partly covered pixels inside a transparency group keep the group's alpha
    // and shape bookkeeping. A first repair painted them straight onto the plates and skipped it,
    // so the group composite dropped them (round K, KF-001). Both groups sit on opaque paper. In the
    // non-isolated group drawn with Multiply, which composites by group alpha, partly covered spot
    // pixels blend their plates. The knockout group at half opacity composites the pixels its
    // objects touched, and there they paint their converted ink. Pixel 11 is three quarters covered.
    [Theory]
    [InlineData("/Multiply gs /Fm Do", false)]
    [InlineData("/Half gs /Fm Do", true)]
    public void ASpotStencilInsideAGroupKeepsItsPartlyCoveredPixels(string draw, bool knockout)
    {
        byte[] paper = Encoding.ASCII.GetBytes("0 0 0 0 k 0 0 24 16 re f ");
        byte[] stencil = Stencil("BI /W 64 /H 32 /IM true /BPC 1 ID ", InkedColumns(31));
        PdfRenderedPage grouped = Render([.. paper, .. Encoding.ASCII.GetBytes(draw)], null, 24, 16, multiply: true, xObjects: update =>
        [
            Entry("Fm", update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Type", Name("XObject")), Entry("Subtype", Name("Form")), Entry("BBox", Numbers(0, 0, 24, 16)),
                Entry("Group", new PdfDictionary([Entry("S", Name("Transparency")), Entry("K", new PdfBoolean(knockout))]))]),
                [.. stencil, .. Encoding.ASCII.GetBytes(" 0 0 0 0 re f")])))
        ]);
        int Green(PdfRenderedPage page, int x) => page.Pixels.Span[(8 * 24 + x) * 4 + 1];
        Assert.True(Green(grouped, 10) < Green(grouped, 11) && Green(grouped, 11) < Green(grouped, 12) - 40,
            $"Pixels 10 to 12 read {Green(grouped, 10)}, {Green(grouped, 11)}, {Green(grouped, 12)}.");
        if (!knockout)
        {
            // Multiply over white paper leaves the stencil as drawn without the group.
            PdfRenderedPage expected = Render([.. paper, .. stencil], null, 24, 16, multiply: true);
            for (int x = 9; x <= 12; x++)
                for (int channel = 0; channel < 3; channel++)
                    Assert.InRange(grouped.Pixels.Span[(8 * 24 + x) * 4 + channel] - expected.Pixels.Span[(8 * 24 + x) * 4 + channel], -3, 3);
        }
    }

    // A spot stencil painted at an opacity below 1 knocks the plates under it out by that opacity
    // and keeps them, as before round J. A first repair gave it its spot there, which took it off
    // that path and cleared the MyBlue plate under it (round K, KF-002).
    [Fact]
    public void ASpotStencilBelowFullOpacityKeepsThePlatesUnderIt()
    {
        byte[] content = [.. Encoding.ASCII.GetBytes("/Blue cs 1 scn 0 0 24 16 re f /Half gs "),
            .. Stencil("BI /W 16 /H 8 /IM true /BPC 1 ID ", new byte[16])];
        Assert.InRange(Gray(Render(content, "MyBlue", 24, 16), 10, 8), 120, 136);
    }

    // A spot image whose samples pass 256 MiB still paints its plate. Its spot samples were
    // addressed by an int bit offset, which overflows past 2^31 bits, and the exception failed the
    // whole page where the color plane, addressed by byte, had rendered (round L, BF-001). This one
    // is 16384 by 8200 two-component 8-bit samples, 268.7 MB, compressed to a small stream. Its
    // color key, which no sample matches, keeps the plane nearest-sampled, so only one sample per
    // plane cell is read and the test stays quick; averaged planes read through the same address.
    [Fact]
    public void ASpotImageLargerThan256MiBPaintsItsPlate()
    {
        const int width = 16384, height = 8200;
        var samples = new byte[(long)width * height * 2];
        Array.Fill(samples, (byte)255);
        using var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(samples);
        samples = [];
        PdfRenderedPage page = Render(Encoding.ASCII.GetBytes("q 16 0 0 8 0 0 cm /Im Do Q"), "CustomMagenta",
            xObjects: update => [Entry("Im", update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Type", Name("XObject")), Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(width)),
                Entry("Height", new PdfInteger(height)), Entry("BitsPerComponent", new PdfInteger(8)),
                Entry("ColorSpace", Name("Mixed")), Entry("Mask", Numbers(0, 0, 0, 0)),
                Entry("Filter", Name("FlateDecode"))]), compressed.ToArray())))]);
        Assert.InRange(Gray(page, 4, 7), 0, 5);
    }

    // A spot image painted at an opacity below 1 follows the same rule as a spot stencil: it knocks
    // the plates under it out by that opacity and keeps them. Its palette entries or samples named
    // their spot at any opacity, which sent each pixel to the path that clears every plate, so the
    // MyBlue plate under a half-opaque image went blank (round L, BF-002; a two-spot palette defined
    // by its tint transform only reached that path once its spots were kept).
    [Theory]
    [InlineData("PairPalette", 1)]
    [InlineData("Red", 255)]
    public void ASpotImageBelowFullOpacityKeepsThePlatesUnderIt(string space, byte sample)
    {
        byte[] content = [.. Encoding.ASCII.GetBytes("/Blue cs 1 scn 0 0 16 8 re f /Half gs "),
            .. HalfInkedImage(space, 1, components: 1, paletteIndex: sample)];
        PdfRenderedPage plate = Render(content, "MyBlue");
        Assert.InRange(Gray(plate, 4, 4), 120, 136);
        Assert.InRange(Gray(plate, 12, 4), 120, 136);
    }

    // A spot stencil large enough to paint from parallel rows fills its plate through the prepared
    // plate write, which takes no lock per pixel (round K, KF-003). The plate values are what the
    // locked write would produce too, so this checks the prepared write's output under parallel
    // rows, not that it took no lock.
    [Fact]
    public void ALargeSpotStencilFillsItsPlateFromParallelRows()
    {
        const int size = 1536;
        var samples = new byte[size / 8 * size];
        for (int y = 0; y < size; y++)
            for (int x = size / 2; x < size; x++) samples[y * (size / 8) + x / 8] |= (byte)(0x80 >> (x % 8));
        byte[] content = [.. Encoding.ASCII.GetBytes($"/Red cs 1 scn q 512 0 0 512 0 0 cm BI /W {size} /H {size} /IM true /BPC 1 ID "),
            .. samples, .. Encoding.ASCII.GetBytes("\nEI Q")];
        PdfRenderedPage plate = Render(content, "MyRed", 512, 512, parallelism: 4);
        for (int y = 0; y < 512; y += 37)
        {
            Assert.True(Gray(plate, 100, y) < 5, $"Row {y} has no spot ink on the left half.");
            Assert.Equal(255, Gray(plate, 400, y));
        }
    }

    // On a plain RGB page that simulates overprint, a spot-colored Type 3 bitmap glyph updates the
    // spot shadow, so a spot overprinted on it later shows both inks, as over a vector fill of the
    // same color. Its covered pixels were written straight to RGB and left the shadow holding the
    // paper, so the overprint painted over the glyph as if it were not there (round K, KF-004).
    [Fact]
    public void ASpotGlyphOnAnRgbPageShowsUnderALaterSpotOverprint()
    {
        const string overprint = " /On gs /Blue cs 1 scn 0 0 8 8 re f";
        PdfRenderedPage glyph = Render(Encoding.ASCII.GetBytes("/Red cs 1 scn BT /F1 8 Tf 0 0 Td (A) Tj ET" + overprint),
            null, 8, 8, cmykGroup: false, type3: true);
        PdfRenderedPage vector = Render(Encoding.ASCII.GetBytes("/Red cs 1 scn 0 0 8 8 re f" + overprint),
            null, 8, 8, cmykGroup: false, type3: true);
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(glyph.Pixels.Span[(4 * 8 + 4) * 4 + channel] - vector.Pixels.Span[(4 * 8 + 4) * 4 + channel], -3, 3);
        Assert.True(vector.Pixels.Span[(4 * 8 + 4) * 4 + 1] < 100, "The vector reference shows no overprint.");
    }

    // A MyRed image mask drawn 16 by 8 at 4,4 with the given image header and samples.
    private static byte[] Stencil(string paint, byte[] samples) => paint.StartsWith("BI")
        ? [.. Encoding.ASCII.GetBytes($"/Red cs 1 scn q 16 0 0 8 4 4 cm {paint}"), .. samples, .. Encoding.ASCII.GetBytes("\nEI Q")]
        : Encoding.ASCII.GetBytes($"/Red cs 1 scn q 1 0 0 1 4 4 cm {paint} Q");

    // 64 by 32 mask samples inked in columns 0 up to the given one, which ends that share of the way
    // through a pixel when drawn 16 pixels wide.
    private static byte[] InkedColumns(int end)
    {
        var samples = new byte[8 * 32];
        for (int y = 0; y < 32; y++)
            for (int x = end; x < 64; x++) samples[y * 8 + x / 8] |= (byte)(0x80 >> (x % 8));
        return samples;
    }

    // An inline image covering the page, 16 by 8 samples per reduction step: the left half at full
    // tint in every component (or the given palette index), the right half blank.
    private static byte[] HalfInkedImage(string space, double reduction, int components, byte paletteIndex = 255)
    {
        int width = (int)(16 * reduction), height = (int)(8 * reduction);
        var samples = new byte[width * height * components];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width / 2; x++)
                for (int component = 0; component < components; component++)
                    samples[(y * width + x) * components + component] = paletteIndex;
        return [.. Encoding.ASCII.GetBytes($"q 16 0 0 8 0 0 cm BI /W {width} /H {height} /BPC 8 /CS /{space} ID "),
            .. samples, .. Encoding.ASCII.GetBytes("\nEI Q")];
    }

    private static int Gray(PdfRenderedPage page, int x, int y) => page.Pixels.Span[(y * page.Width + x) * 4];

    // One plate (or the composite, when plate is null) of a 16 by 8 page that blends in a DeviceCMYK
    // group. Its color spaces:
    // Mixed: DeviceN Cyan and CustomMagenta, whose alternate is the same amount of magenta.
    // Pair: DeviceN MyBlue (alternate cyan) and MyRed (alternate magenta and yellow), defined only
    // by their tint transform. Defined: the same pair with colorant definitions.
    // Red and Blue: Separations of MyRed and MyBlue. RedNone: DeviceN MyRed and None.
    // PairPalette and DefinedPalette: Indexed over Pair and Defined, entry 0 blank, entry 1 both
    // spots at full tint.
    // Graphics states: On overprints, Half fills at half opacity and, when asked for, Multiply
    // blends with Multiply (which keeps a plain RGB page from simulating overprint).
    // Fonts, when asked for: F1, a Type 3 font whose glyph A is an 8 by 8 image mask filling its
    // 1000-unit cell.
    private static PdfRenderedPage Render(byte[] content, string? plate, int width = 16, int height = 8,
        Func<PdfIncrementalUpdateBuilder, IEnumerable<KeyValuePair<PdfName, PdfObject>>>? xObjects = null,
        int parallelism = 1, bool cmykGroup = true, bool type3 = false, bool multiply = false)
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(width, height, content).Build());
        PdfPageTreeEntry entry = PdfPageTree.Read(document).Pages[0];
        var original = Assert.IsType<PdfDictionary>(document.Resolve(entry.Reference));
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference Function(string program, int inputs) => update.AddObject(new PdfStream(
            new PdfDictionary([
                Entry("FunctionType", new PdfInteger(4)),
                Entry("Domain", Numbers(inputs == 1 ? [0, 1] : [0, 1, 0, 1])),
                Entry("Range", Numbers(0, 1, 0, 1, 0, 1, 0, 1))
            ]), Encoding.ASCII.GetBytes(program)));
        var red = new PdfArray([Name("Separation"), Name("MyRed"), Name("DeviceCMYK"), Function("{ 0 exch dup 0 }", 1)]);
        var blue = new PdfArray([Name("Separation"), Name("MyBlue"), Name("DeviceCMYK"), Function("{ 0 0 0 }", 1)]);
        var pair = new PdfArray([Name("DeviceN"), new PdfArray([Name("MyBlue"), Name("MyRed")]),
            Name("DeviceCMYK"), Function("{ dup 0 }", 2)]);
        var defined = new PdfArray([Name("DeviceN"), new PdfArray([Name("MyBlue"), Name("MyRed")]),
            Name("DeviceCMYK"), Function("{ dup 0 }", 2),
            new PdfDictionary([Entry("Colorants", new PdfDictionary([Entry("MyBlue", blue), Entry("MyRed", red)]))])]);
        PdfString BothSpots() => new(new byte[] { 0, 0, 255, 255 }, PdfStringForm.Hexadecimal);
        PdfDictionary resources = new([
            Entry("ColorSpace", new PdfDictionary([
                Entry("Mixed", new PdfArray([Name("DeviceN"), new PdfArray([Name("Cyan"), Name("CustomMagenta")]),
                    Name("DeviceCMYK"), Function("{ 0 0 }", 2)])),
                Entry("Pair", pair),
                Entry("Defined", defined),
                Entry("Red", red),
                Entry("Blue", blue),
                Entry("RedNone", new PdfArray([Name("DeviceN"), new PdfArray([Name("MyRed"), Name("None")]),
                    Name("DeviceCMYK"), Function("{ pop 0 exch dup 0 }", 2)])),
                Entry("PairPalette", new PdfArray([Name("Indexed"), pair, new PdfInteger(1), BothSpots()])),
                Entry("DefinedPalette", new PdfArray([Name("Indexed"), defined, new PdfInteger(1), BothSpots()]))
            ])),
            Entry("ExtGState", new PdfDictionary([
                Entry("On", new PdfDictionary([Entry("OP", new PdfBoolean(true)), Entry("op", new PdfBoolean(true)),
                    Entry("OPM", new PdfInteger(1))])),
                Entry("Half", new PdfDictionary([Entry("ca", new PdfReal(0.5))])),
                .. multiply ? [Entry("Multiply", new PdfDictionary([Entry("BM", Name("Multiply"))]))]
                    : Array.Empty<KeyValuePair<PdfName, PdfObject>>()
            ])),
            Entry("XObject", new PdfDictionary(xObjects?.Invoke(update) ?? []))
        ]);
        if (type3)
        {
            PdfIndirectReference glyph = update.AddObject(new PdfStream(new PdfDictionary([]), [
                .. Encoding.ASCII.GetBytes("1000 0 0 0 1000 1000 d1 q 1000 0 0 1000 0 0 cm BI /W 8 /H 8 /IM true /BPC 1 ID "),
                .. new byte[8], .. Encoding.ASCII.GetBytes("\nEI Q")]));
            resources = new PdfDictionary(resources.Append(Entry("Font", new PdfDictionary([Entry("F1", new PdfDictionary([
                Entry("Type", Name("Font")), Entry("Subtype", Name("Type3")), Entry("FontBBox", Numbers(0, 0, 1000, 1000)),
                Entry("FontMatrix", new PdfArray([new PdfReal(0.001), new PdfInteger(0), new PdfInteger(0),
                    new PdfReal(0.001), new PdfInteger(0), new PdfInteger(0)])),
                Entry("CharProcs", new PdfDictionary([Entry("A", glyph)])),
                Entry("Encoding", new PdfDictionary([Entry("Differences", new PdfArray([new PdfInteger(65), Name("A")]))])),
                Entry("FirstChar", new PdfInteger(65)), Entry("LastChar", new PdfInteger(65)),
                Entry("Widths", Numbers(1000)), Entry("Resources", new PdfDictionary([]))]))]))));
        }
        IEnumerable<KeyValuePair<PdfName, PdfObject>> page = original
            .Where(item => !item.Key.Equals(Name("Resources")) && !item.Key.Equals(Name("Group")))
            .Append(Entry("Resources", resources));
        if (cmykGroup)
            page = page.Append(Entry("Group", new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))])));
        update.ReplaceObject(entry.Reference.ObjectNumber, new PdfDictionary(page));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions(width, height, includeAnnotations: false, includeFormFields: false)
            {
                SeparationPlate = plate, MaximumParallelism = parallelism
            });
    }

    private static PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));

    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);

    private static PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
}
