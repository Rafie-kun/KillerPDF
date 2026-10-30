using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfTilingPatternLimitTests
{
    [Theory]
    [InlineData("0.000000001", "0.000000001")]
    [InlineData("1", "0.000000001")]
    public void ACellCountBeyondTheIntegerRangeIsTheOrdinaryTooManyCellsRefusal(string xStep, string yStep)
    {
        PdfDocument document = PdfDocument.Open(Pdf(xStep, yStep));
        FormatException error = Assert.Throws<FormatException>(() =>
            new PdfPageRenderer(document).Render(0, new PdfRenderOptions(10, 10)));
        Assert.Contains("too many cells", error.Message, StringComparison.Ordinal);
    }

    // An empty axis used to slip past the product check and leave a loop that never ended.
    [Fact]
    public async Task AnInvertedBoxWithAHugeCellRangeFinishes()
    {
        PdfDocument document = PdfDocument.Open(Pdf("1", "0.000000000001", "100 0 0 0"));
        await Task.Run(() => new PdfPageRenderer(document).Render(0, new PdfRenderOptions(10, 10)))
            .WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static byte[] Pdf(string xStep, string yStep, string box = "0 0 1 1")
    {
        const string content = "/Pattern cs /P1 scn 0 0 10 10 re f";
        const string cell = "1 0 0 rg 0 0 1 1 re f";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents 4 0 R /Resources << /Pattern << /P1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [{box}] /XStep {xStep} /YStep {yStep} " +
                $"/Resources << >> /Length {cell.Length} >>\nstream\n{cell}\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
