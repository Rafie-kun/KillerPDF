using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfLargeContentTests
{
    // 400,000 stroked lines are 1,200,000 instructions, past the cached reader's limit, and the
    // red square is the very last thing drawn, so any page content dropped at the end shows.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APageWithMoreThanAMillionInstructionsDrawsItsLastInstruction(bool recovery)
    {
        var content = new StringBuilder("0.1 w\n", 6_000_000);
        for (int i = 0; i < 400_000; i++) content.Append("1 1 m 2 2 l S\n");
        content.Append("1 0 0 rg 24 24 16 16 re f\n");
        byte[] source = Pdf(content.ToString());
        PdfDocument document = recovery
            ? PdfDocument.OpenWithCompatibilityRecovery(source)
            : PdfDocument.Open(source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        PdfRenderedPage page = new PdfPageRenderer(document)
            .Render(0, new PdfRenderOptions(64, 64) { CacheResult = false }, timeout.Token);

        int center = (32 * 64 + 32) * 4;
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, page.Pixels.Span.Slice(center, 4).ToArray());
        Assert.DoesNotContain(page.Diagnostics, message => message.Contains("truncated", StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] Pdf(string content)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 64 64] /Resources << >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        ];
        var pdf = new StringBuilder("%PDF-1.7\n", content.Length + 1024);
        var offsets = new List<int>();
        foreach (string value in objects)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{offsets.Count} 0 obj\n{value}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
