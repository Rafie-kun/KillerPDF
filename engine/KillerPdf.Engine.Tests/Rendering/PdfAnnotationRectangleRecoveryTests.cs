using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfAnnotationRectangleRecoveryTests
{
    [Theory]
    [InlineData(80, 20, 20, 60)]
    [InlineData(20, 60, 80, 20)]
    [InlineData(80, 60, 20, 20)]
    public void Render_NormalizesReversedAnnotationRectangleCoordinates(
        double left, double bottom, double right, double top)
    {
        byte[] normal = Render([20, 20, 80, 60]);
        Assert.Contains(normal, value => value != 255);
        Assert.Contains(EnumeratePixels(normal), pixel => pixel.SequenceEqual(new byte[] { 255, 0, 0, 255 }));
        Assert.Contains(EnumeratePixels(normal), pixel => pixel.SequenceEqual(new byte[] { 0, 0, 255, 255 }));

        Assert.Equal(normal, Render([left, bottom, right, top]));
    }

    private static byte[] Render(double[] rectangle)
    {
        string rect = string.Join(' ', rectangle.Select(value => value.ToString("0", System.Globalization.CultureInfo.InvariantCulture)));
        string appearance = "1 0 0 rg 0 0 24 30 re f 0 0 1 rg 36 10 24 30 re f";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [4 0 R] >>",
            $"<< /Type /Annot /Subtype /Square /Rect [{rect}] /AP << /N 5 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 60 40] /Resources << >> /Length {Encoding.ASCII.GetByteCount(appearance)} >>\nstream\n{appearance}\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (int index in Enumerable.Range(0, objects.Length))
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        PdfDocument document = PdfDocument.Open(Encoding.ASCII.GetBytes(pdf.ToString()));
        return new PdfPageRenderer(document).Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray();
    }

    private static IEnumerable<byte[]> EnumeratePixels(byte[] pixels)
    {
        for (int offset = 0; offset < pixels.Length; offset += 4)
            yield return pixels.AsSpan(offset, 4).ToArray();
    }
}
