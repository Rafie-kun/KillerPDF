using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// A reduced image is averaged into a plane and read back per pixel. The plane used to take its
// size from the outward-rounded bounds, a column more than the pixels reading it when the image
// sat a hair off the pixel grid, so the lookup skipped a cell: a full-page scan lost a column of
// content and shifted a pixel from there on.
public sealed class PdfReducedImagePlaneTests
{
    [Theory]
    [InlineData("10 0 0 10 0.0001 0.0001")]
    [InlineData("10 0 0 10 4.6 3.3")]
    [InlineData("-10 0 0 10 12.4 2")]
    public void EveryColumnOfAReducedScanSurvives(string matrix)
    {
        for (int column = 0; column < 30; column++)
        {
            PdfRenderedPage page = Render(ScanWithColumn(column), matrix);
            int darkest = 255;
            for (int index = 0; index < page.Pixels.Length; index += 4)
                darkest = Math.Min(darkest, (int)page.Pixels.Span[index]);
            Assert.True(darkest <= 230, $"Column {column} vanished (darkest {darkest}).");
        }
    }

    // Each cell must average the source under its own pixel. Dividing the source evenly among the
    // cells assumed the image starts on a pixel edge; this one starts half a pixel into a column
    // and a quarter into a row, and the even split moved edges by up to half a pixel (scan strips
    // in PDF-HUL-164, a placed mask in PDF-HUL-85).
    [Theory]
    [InlineData("/BPC 1 /CS /G", "20 0 0 8 4.5 5.25")]
    [InlineData("/BPC 1 /CS /G", "-20 0 0 8 24.5 5.25")]
    [InlineData("/BPC 1 /CS /G", "20 0 0 -8 4.5 13.25")]
    [InlineData("/BPC 8 /CS [/I /G 1 <FF00>]", "20 0 0 8 4.5 5.25")]
    [InlineData("/BPC 8 /CS [/I /G 1 <FF00>]", "-20 0 0 8 24.5 5.25")]
    [InlineData("/IM true", "20 0 0 8 4.5 5.25")]
    [InlineData("/IM true", "20 0 0 -8 4.5 13.25")]
    public void AReducedImageKeepsItsPhaseOffThePixelGrid(string header, string matrix)
    {
        // 64 by 32 samples, black in columns 16 to 31 and rows 8 to 15, drawn 20 by 8 pixels.
        bool indexed = header.Contains("/I ");
        int rowBytes = indexed ? 64 : 8;
        var samples = new byte[rowBytes * 32];
        for (int y = 0; y < 32; y++)
        for (int x = 0; x < 64; x++)
        {
            bool black = x is >= 16 and < 32 && y is >= 8 and < 16;
            if (indexed) samples[y * rowBytes + x] = black ? (byte)1 : (byte)0;
            else if (!black) samples[y * rowBytes + x / 8] |= (byte)(0x80 >> (x % 8));
        }
        byte[] content = [.. Encoding.ASCII.GetBytes($"0 g q {matrix} cm BI /W 64 /H 32 {header} ID "),
            .. samples, .. Encoding.ASCII.GetBytes("\nEI Q")];
        PdfRenderedPage page = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder().AddPage(32, 20, content).Build()))
            .Render(0, new PdfRenderOptions(32, 20, includeAnnotations: false, includeFormFields: false));

        double[] m = [.. matrix.Split(' ').Select(double.Parse)];
        (double left, double right) = Sorted(m[4] + m[0] * 0.25, m[4] + m[0] * 0.5);
        (double top, double bottom) = Sorted(20 - (m[5] + m[3] * 0.75), 20 - (m[5] + m[3] * 0.5));
        for (int y = 0; y < 20; y++)
        for (int x = 0; x < 32; x++)
        {
            double coverage = Overlap(x, left, right) * Overlap(y, top, bottom);
            int expected = (int)Math.Round(255 * (1 - coverage));
            int actual = page.Pixels.Span[(y * 32 + x) * 4];
            Assert.True(Math.Abs(actual - expected) <= 4, $"Pixel {x},{y} is {actual}, expected {expected}.");
        }

        static (double, double) Sorted(double a, double b) => (Math.Min(a, b), Math.Max(a, b));
        static double Overlap(int pixel, double start, double end) =>
            Math.Max(0, Math.Min(pixel + 1, end) - Math.Max(pixel, start));
    }

    // An indexed image over a process-and-spot DeviceN on a CMYK page is averaged into the color
    // plane and, separately, a plane of spot samples (round H, HF-003: both now average the same
    // source under each pixel). The process plate must cover each pixel by the share of the
    // rectangle inside it.
    [Theory]
    [InlineData("20 0 0 8 4.5 5.25")]
    [InlineData("-20 0 0 8 24.5 5.25")]
    public void AReducedSpotPaletteImageKeepsItsPhase(string matrix)
    {
        var samples = new byte[64 * 32];
        for (int y = 8; y < 16; y++)
            for (int x = 16; x < 32; x++) samples[y * 64 + x] = 1;
        byte[] content = [.. Encoding.ASCII.GetBytes($"q {matrix} cm BI /W 64 /H 32 /BPC 8 /CS /Palette ID "),
            .. samples, .. Encoding.ASCII.GetBytes("\nEI Q")];
        double[] m = [.. matrix.Split(' ').Select(double.Parse)];
        (double left, double right) = (Math.Min(m[4] + m[0] * 0.25, m[4] + m[0] * 0.5), Math.Max(m[4] + m[0] * 0.25, m[4] + m[0] * 0.5));
        (double top, double bottom) = (20 - (m[5] + m[3] * 0.75), 20 - (m[5] + m[3] * 0.5));
        // A plate render shows its ink as gray.
        PdfRenderedPage page = RenderSpotPlate(content, "Cyan");
        for (int y = 0; y < 20; y++)
        for (int x = 0; x < 32; x++)
        {
            double expected = Overlap(x, left, right) * Overlap(y, top, bottom);
            double actual = (255 - page.Pixels.Span[(y * 32 + x) * 4]) / 255d;
            Assert.True(Math.Abs(actual - expected) <= 0.04, $"Pixel {x},{y} covers {actual:F2}, expected {expected:F2}.");
        }

        static double Overlap(int pixel, double start, double end) =>
            Math.Max(0, Math.Min(pixel + 1, end) - Math.Max(pixel, start));
    }

    // One plate of a 32 by 20 CMYK-group page whose /Palette indexes Cyan plus a custom spot
    // (whose alternate is the same amount of magenta): entry 0 is no ink, entry 1 full ink of both.
    private static PdfRenderedPage RenderSpotPlate(byte[] content, string plate)
    {
        static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        static PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(32, 20, content).Build());
        PdfPageTreeEntry entry = PdfPageTree.Read(document).Pages[0];
        var original = Assert.IsType<PdfDictionary>(document.Resolve(entry.Reference));
        var update = new PdfIncrementalUpdateBuilder(document);
        var tint = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("FunctionType", new PdfInteger(4)), Entry("Domain", Numbers(0, 1, 0, 1)),
            Entry("Range", Numbers(0, 1, 0, 1, 0, 1, 0, 1))]), Encoding.ASCII.GetBytes("{ 0 0 }")));
        var mixed = new PdfArray([Name("DeviceN"), new PdfArray([Name("Cyan"), Name("CustomMagenta")]), Name("DeviceCMYK"), tint]);
        var palette = new PdfArray([Name("Indexed"), mixed, new PdfInteger(1),
            new PdfString(new byte[] { 0, 0, 255, 255 }, PdfStringForm.Hexadecimal)]);
        update.ReplaceObject(entry.Reference.ObjectNumber, new PdfDictionary(original
            .Where(item => !item.Key.Equals(Name("Resources")) && !item.Key.Equals(Name("Group")))
            .Append(Entry("Resources", new PdfDictionary([Entry("ColorSpace", new PdfDictionary([Entry("Palette", palette)]))])))
            .Append(Entry("Group", new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))])))));
        return new PdfPageRenderer(PdfDocument.Open(update.Build()))
            .Render(0, new PdfRenderOptions(32, 20, includeAnnotations: false, includeFormFields: false) { SeparationPlate = plate });
    }

    // A 1-bit gray image where 1 is white: all white except one black column.
    private static byte[] ScanWithColumn(int column)
    {
        const int rowBytes = 4;
        var bits = new byte[rowBytes * 30];
        Array.Fill(bits, (byte)0xFF);
        for (int y = 0; y < 30; y++) bits[y * rowBytes + column / 8] &= (byte)~(0x80 >> (column % 8));
        return bits;
    }

    private static PdfRenderedPage Render(byte[] image, string matrix)
    {
        byte[] content = [.. Encoding.ASCII.GetBytes("q " + matrix + " cm BI /W 30 /H 30 /BPC 1 /CS /G ID "),
            .. image, .. Encoding.ASCII.GetBytes("\nEI Q")];
        return new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder().AddPage(20, 20, content).Build()))
            .Render(0, new PdfRenderOptions(20, 20, includeAnnotations: false, includeFormFields: false));
    }
}
