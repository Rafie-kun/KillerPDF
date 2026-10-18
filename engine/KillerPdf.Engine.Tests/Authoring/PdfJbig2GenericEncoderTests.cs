using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Authoring;

public sealed class PdfJbig2GenericEncoderTests
{
    [Theory]
    [InlineData(1, 1, 0)]
    [InlineData(7, 3, 1)]
    [InlineData(64, 40, 2)]
    [InlineData(301, 97, 3)]
    [InlineData(1728, 120, 4)]
    public void EncodedImagesDecodeToTheSamePixels(int width, int height, int seed)
    {
        byte[] pixels = ScanLike(width, height, seed);

        byte[] encoded = PdfJbig2GenericEncoder.Encode(width, height, pixels);
        byte[] decoded = PdfJbig2Decoder.Decode(encoded, null, _ => [], int.MaxValue, width, height);

        AssertSamePixels(pixels, decoded, width, height);
    }

    [Fact]
    public void BlankAndSolidImagesDecodeToTheSamePixels()
    {
        foreach (byte value in new byte[] { 0, 255 })
        {
            byte[] pixels = Enumerable.Repeat(value, 200 * 50).ToArray();
            byte[] decoded = PdfJbig2Decoder.Decode(PdfJbig2GenericEncoder.Encode(200, 50, pixels), null, _ => [],
                int.MaxValue, 200, 50);
            AssertSamePixels(pixels, decoded, 200, 50);
        }
    }

    [Fact]
    public void FromBitonalWithJbig2KeepsTheSmallestEncodingAndRoundTripsThroughAPage()
    {
        (byte[] pixels, int width, int height) = TextPage();
        PdfImage fallback = PdfImage.FromBitonal(width, height, pixels);
        PdfImage image = PdfImage.FromBitonal(width, height, pixels, allowJbig2: true);

        Assert.Equal("JBIG2Decode", image.Filter);
        Assert.True(image.Data.Length < fallback.Data.Length);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(width, height, new PdfContentStreamBuilder().DrawImage(image, 0, 0, width, height)).Build());
        PdfStream stream = ImageStream(document);
        Assert.Equal("JBIG2Decode", Assert.IsType<PdfName>(stream.Dictionary[Name("Filter")]).ValueAsLatin1());
        AssertSamePixels(pixels, PdfStreamDecoder.Decode(stream, document.Resolve, (width + 7) / 8 * height), width, height);
    }

    [Fact]
    public void FromBitonalLeavesJbig2OutUnlessAllowed()
    {
        byte[] pixels = ScanLike(400, 100, 5);

        Assert.NotEqual("JBIG2Decode", PdfImage.FromBitonal(400, 100, pixels).Filter);
    }

    // Lines of letter-like strokes between blank bands, with repeated rows and a little noise,
    // so typical prediction and the context model both see work.
    private static byte[] ScanLike(int width, int height, int seed)
    {
        var random = new Random(seed);
        byte[] pixels = Enumerable.Repeat((byte)255, width * height).ToArray();
        for (int y = 0; y < height; y++)
        {
            int band = y % 24;
            if (band is >= 4 and < 18)
            {
                for (int x = 0; x < width; x++)
                {
                    int letter = x % 11;
                    bool stroke = letter is 1 or 2 || band is 5 or 11 && letter < 8 || letter == 7 && band > 9;
                    if (stroke && (x / 11 + y / 24) % 5 != 0) pixels[y * width + x] = 0;
                }
            }
            else if (band == 20 && y > 0)
            {
                Array.Copy(pixels, (y - 1) * width, pixels, y * width, width);
            }
            if (random.Next(7) == 0) pixels[y * width + random.Next(width)] ^= 255;
        }
        return pixels;
    }

    // A paragraph of Times text rendered at 300 dpi and thresholded, as a scanned page would be.
    private static (byte[] Pixels, int Width, int Height) TextPage()
    {
        TrueTypeFont font = PdfStandardFontSubstitutes.Find("Times-Roman");
        string[] lines =
        [
            "The quick brown fox jumps over the lazy dog while the five boxing wizards jump quickly.",
            "Pack my box with five dozen liquor jugs, and how vexingly quick daft zebras jump today.",
            "Sphinx of black quartz, judge my vow; jackdaws love my big sphinx of quartz and more.",
            "Bright vixens jump, dozy fowl quack, and quick wafting zephyrs vex bold Jim at noon.",
            "A wizard's job is to vex chumps quickly in fog, said the clerk to the waiting crowd.",
            "Few black taxis drive up major roads on quiet hazy nights, though many people walk.",
        ];
        const double scale = 300.0 / 72, leading = 16;
        double pageWidth = 480, pageHeight = 30 + lines.Length * leading;
        var content = new PdfContentStreamBuilder().BeginText();
        for (int line = 0; line < lines.Length; line++)
            content.SetFont(font, 11).SetTextMatrix(1, 0, 0, 1, 20, pageHeight - 24 - line * leading).ShowUnicodeText(lines[line]);
        content.EndText();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(pageWidth, pageHeight, content).Build());
        int width = (int)(pageWidth * scale), height = (int)Math.Round(pageHeight * scale);
        byte[] bgra = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(width, height)).Pixels.ToArray();
        byte[] pixels = new byte[width * height];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = bgra[i * 4 + 1] < 128 ? (byte)0 : (byte)255;
        return (pixels, width, height);
    }

    // The decoder returns rows of packed bits where 1 is white, as DeviceGray reads them.
    private static void AssertSamePixels(byte[] pixels, byte[] decoded, int width, int height)
    {
        int stride = (width + 7) / 8;
        Assert.Equal(stride * height, decoded.Length);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                Assert.True((pixels[y * width + x] >= 128) == ((decoded[y * stride + x / 8] & 0x80 >> (x & 7)) != 0),
                    $"Pixel {x},{y} differs.");
    }

    private static PdfStream ImageStream(PdfDocument document)
    {
        var catalog = Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(document.Trailer[Name("Root")])));
        var pages = Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(catalog[Name("Pages")])));
        var page = Assert.IsType<PdfDictionary>(document.Resolve(Assert.IsType<PdfIndirectReference>(Assert.IsType<PdfArray>(pages[Name("Kids")])[0])));
        var resources = Assert.IsType<PdfDictionary>(page[Name("Resources")]);
        var xobjects = Assert.IsType<PdfDictionary>(resources[Name("XObject")]);
        return Assert.IsType<PdfStream>(document.Resolve(Assert.IsType<PdfIndirectReference>(xobjects[Name("Im1")])));
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
