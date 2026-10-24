using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
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
