using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfMalformedOperatorRecoveryTests
{
    [Theory]
    [InlineData("3 J")]
    [InlineData("4 j")]
    [InlineData("0.5 M")]
    [InlineData("[-1 3] 0 d")]
    [InlineData("/Name 0 0 1 0 0 cm")]
    [InlineData("/F2 /bad Tf")]
    [InlineData("1 0 0 /bad 20 20 Tm")]
    public void RecoveryPreservesStateAndRendersContentAfterInvalidOperator(string operation)
    {
        byte[] source = CreatePdf(operation);
        var options = new PdfRenderOptions(100, 100);
        PdfRenderedPage expected = new PdfPageRenderer(PdfDocument.Open(CreatePdf("")))
            .Render(0, options);
        PdfRenderedPage actual = new PdfPageRenderer(PdfDocument.OpenWithCompatibilityRecovery(source))
            .Render(0, options);

        Assert.NotEmpty(actual.Diagnostics);
        Assert.Empty(expected.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        Assert.Contains(Pixels(actual.Pixels.ToArray()), pixel => pixel.SequenceEqual(new byte[] { 255, 0, 0, 255 }));
        Assert.Contains(Pixels(actual.Pixels.ToArray()), pixel => pixel.SequenceEqual(new byte[] { 0, 0, 255, 255 }));
        Assert.Contains(Pixels(actual.Pixels.ToArray()), pixel => pixel[0] < 128
            && pixel[0] == pixel[1] && pixel[1] == pixel[2] && pixel[3] == 255);
        Assert.Throws<FormatException>(() => new PdfPageRenderer(PdfDocument.Open(source)).Render(0, options));
    }

    private static byte[] CreatePdf(string operation)
    {
        string content = "2 J 0 j 8 M [6 3] 1 d 1 0 0 1 5 7 cm "
            + "BT /F1 14 Tf 1 0 0 1 10 45 Tm " + operation + "\n"
            + "(K) Tj ET 1 0 0 RG 4 w 10 10 m 35 10 l 15 18 l S "
            + "0 0 1 rg 65 65 10 10 re f";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>"
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
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static IEnumerable<byte[]> Pixels(byte[] pixels)
    {
        for (int offset = 0; offset < pixels.Length; offset += 4)
            yield return pixels.AsSpan(offset, 4).ToArray();
    }
}
