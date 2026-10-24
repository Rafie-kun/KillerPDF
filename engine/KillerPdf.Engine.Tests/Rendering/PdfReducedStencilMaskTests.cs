using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// Type 3 bitmap fonts draw each glyph as a 1-bit image mask, usually reduced by less than half
// on screen. Below twofold reduction masks were sampled at the nearest source row, so a
// one-sample stroke on a skipped row vanished and glyphs lost their tops and joins.
public sealed class PdfReducedStencilMaskTests
{
    [Fact]
    public void OneSampleLineSurvivesALessThanTwofoldReduction()
    {
        // A 30x30 mask painted into 20x20 pixels; nearest sampling reads rows 0, 2, 3, 5 ...
        // and never row 13, where the only painted line is.
        PdfRenderedPage page = Render(MaskWithRow(13));
        int darkest = 255;
        for (int y = 7; y <= 10; y++)
            for (int x = 2; x < 18; x++)
                darkest = Math.Min(darkest, page.Pixels.Span[(y * page.Width + x) * 4]);
        Assert.InRange(darkest, 0, 200);
    }

    // The Ghent Type 3 test font is slanted, so its masks are sheared and cannot use the
    // axis-aligned averaged plane; they are supersampled instead.
    [Fact]
    public void OneSampleLineSurvivesInAShearedReducedMask()
    {
        PdfRenderedPage page = Render(MaskWithRow(13), "20 0 6 20 0 0");
        int darkest = 255;
        for (int y = 7; y <= 10; y++)
            for (int x = 6; x < 18; x++)
                darkest = Math.Min(darkest, page.Pixels.Span[(y * page.Width + x) * 4]);
        Assert.InRange(darkest, 0, 200);
    }

    // At a fourfold reduction under shear the averaged plane took its width from the slanted
    // bounding box, which is wider than the glyph, so the nearest lookup skipped whole plane
    // columns and split strokes. Every single-column stroke must stay visible.
    // At a fractional position the outward-rounded bounds are wider than the image, and a plane
    // sized from them had a column more than the pixels reading it.
    [Theory]
    [InlineData("7 0 2 7 4 4")]
    [InlineData("7.3 0 0 7.3 4.4 4.4")]
    [InlineData("10.6 0 0 10.6 3.7 3.2")]
    public void EveryColumnSurvivesAReducedMask(string matrix)
    {
        for (int column = 0; column < 30; column++)
        {
            PdfRenderedPage page = Render(MaskWithColumn(column), matrix);
            int darkest = 255;
            for (int index = 0; index < page.Pixels.Length; index += 4)
                darkest = Math.Min(darkest, (int)page.Pixels.Span[index]);
            Assert.True(darkest <= 225, $"Column {column} vanished (darkest {darkest}).");
        }
    }

    [Fact]
    public void LineOnASampledRowKeepsItsPlace()
    {
        PdfRenderedPage page = Render(MaskWithRow(15));
        // Row 15 of 30 is device rows 10.0 to 10.67; nothing lands far from it.
        for (int y = 0; y < 20; y++)
        {
            if (y is >= 9 and <= 11) continue;
            for (int x = 0; x < 20; x++)
                Assert.Equal(255, page.Pixels.Span[(y * page.Width + x) * 4]);
        }
    }

    // Image mask bits: 0 paints with the default decode, so every row is 1s except the line.
    private static byte[] MaskWithRow(int row)
    {
        const int rowBytes = 4;
        var bits = new byte[rowBytes * 30];
        Array.Fill(bits, (byte)0xFF);
        for (int x = 0; x < 30; x++) bits[row * rowBytes + x / 8] &= (byte)~(0x80 >> (x % 8));
        return bits;
    }

    private static byte[] MaskWithColumn(int column)
    {
        const int rowBytes = 4;
        var bits = new byte[rowBytes * 30];
        Array.Fill(bits, (byte)0xFF);
        for (int y = 0; y < 30; y++) bits[y * rowBytes + column / 8] &= (byte)~(0x80 >> (column % 8));
        return bits;
    }

    private static PdfRenderedPage Render(byte[] mask, string matrix = "20 0 0 20 0 0")
    {
        byte[] content = [.. Encoding.ASCII.GetBytes("0 g q " + matrix + " cm BI /W 30 /H 30 /BPC 1 /IM true ID "),
            .. mask, .. Encoding.ASCII.GetBytes("\nEI Q")];
        return new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder().AddPage(20, 20, content).Build()))
            .Render(0, new PdfRenderOptions(20, 20, includeAnnotations: false, includeFormFields: false));
    }
}
