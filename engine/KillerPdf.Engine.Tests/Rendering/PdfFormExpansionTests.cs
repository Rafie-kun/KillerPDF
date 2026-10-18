using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfFormExpansionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedAcyclicFormsCannotExpandPastThePageWorkLimit(bool recovery)
    {
        PdfDocument document = SharedForms(25);
        if (recovery) document = PdfDocument.OpenWithCompatibilityRecovery(document.Source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        FormatException error = Assert.Throws<FormatException>(() =>
            new PdfPageRenderer(document).Render(0, new PdfRenderOptions(16, 16), timeout.Token));

        Assert.Contains("work limit exceeded", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedFormsCannotMultiplyLongTextPastThePageWorkLimit()
    {
        PdfDocument document = SharedForms(25, "BT /F1 1 Tf (" + new string('A', 10000) + ") Tj ET");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        FormatException error = Assert.Throws<FormatException>(() =>
            new PdfPageRenderer(document).Render(0, new PdfRenderOptions(16, 16), timeout.Token));
        Assert.Contains("Expanded page rendering work limit exceeded", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepRepeatedFormsReportTheNestedExpansionLimit()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        FormatException error = Assert.Throws<FormatException>(() => new PdfPageRenderer(SharedForms(25))
            .Render(0, new PdfRenderOptions(16, 16), timeout.Token));
        Assert.Contains("Nested Form XObject work limit exceeded", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(12, true)]
    [InlineData(25, false)]
    public void OrdinarySharedFormsStillRenderOnRepeatedUncachedRuns(int depth, bool repeat)
    {
        PdfDocument document = SharedForms(depth, "1 0 0 rg 0 0 16 16 re f", repeat);
        var renderer = new PdfPageRenderer(document);
        var options = new PdfRenderOptions(16, 16) { CacheResult = false };
        for (int run = 0; run < 2; run++)
        {
            PdfRenderedPage page = renderer.Render(0, options);
            Assert.Empty(page.Diagnostics);
            for (int offset = 0; offset < page.Pixels.Length; offset += 4)
                Assert.Equal(new byte[] { 0, 0, 255, 255 }, page.Pixels.Span.Slice(offset, 4).ToArray());
        }
    }

    private static PdfDocument SharedForms(int depth, string leaf = "q Q", bool repeat = true)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 16 16] /Resources << /XObject << /F 5 0 R >> >> /Contents 4 0 R >>",
            Stream("/F Do")
        };
        for (int level = 0; level <= depth; level++)
        {
            string content = level == depth ? leaf : repeat ? "/F Do /F Do" : "/F Do";
            string resources = level == depth ? "" : $"/XObject << /F {level + 6} 0 R >>";
            objects.Add(Stream(content, $"/Type /XObject /Subtype /Form /BBox [0 0 16 16] /Resources << /Font << /F1 {depth + 6} 0 R >> {resources} >>"));
        }
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string value in objects)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append($"{offsets.Count} 0 obj\n{value}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.ASCII.GetBytes(pdf.ToString()));
    }

    private static string Stream(string content, string entries = "") =>
        $"<< {entries} /Length {content.Length} >>\nstream\n{content}\nendstream";
}
