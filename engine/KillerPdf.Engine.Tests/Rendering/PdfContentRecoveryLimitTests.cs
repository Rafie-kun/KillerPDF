using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Syntax;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfContentRecoveryLimitTests
{
    private const string Diagnostic = "Page content after 10,000 malformed tokens was ignored.";
    private const string Prefix = "1 0 0 rg 4 6 12 18 re f\n";
    private const string Suffix = "0 0 1 rg 42 38 16 10 re f\n";

    [Fact]
    public void RecoveryBudgetKeepsTheRenderedPrefixAndReportsTheIgnoredSuffix()
    {
        PdfRenderedPage page = Render(Prefix + new string(')', 10_001) + "\n" + Suffix);

        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(page, 10, 50));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(page, 50, 22));
        Assert.Contains(Diagnostic, page.Diagnostics);
    }

    [Fact]
    public void PixelDetectorsDistinguishRemovedPrefixAndPresentSuffix()
    {
        PdfRenderedPage prefix = Render(Prefix);
        PdfRenderedPage removed = Render(new string(')', 10_001) + "\n" + Suffix);
        PdfRenderedPage suffix = Render(Suffix);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(prefix, 10, 50));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(removed, 10, 50));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(suffix, 50, 22));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(prefix, 50, 22));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(removed, 50, 22));
        Assert.Contains(Diagnostic, removed.Diagnostics);
    }

    [Theory]
    [InlineData(10_000, false)]
    [InlineData(10_001, true)]
    public void CachedAndStreamedRenderingUseTheSameRecoveryBoundary(int malformed, bool truncated)
    {
        // Balanced states exceed the cached instruction count without painting anything.
        string padding = string.Concat(Enumerable.Repeat("q Q\n", 500_001));
        PdfRenderedPage page = Render(padding + Prefix + new string(')', malformed) + "\n" + Suffix);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(page, 10, 50));
        Assert.Equal(truncated ? new byte[] { 255, 255, 255, 255 } : new byte[] { 255, 0, 0, 255 },
            Pixel(page, 50, 22));
        Assert.Equal(truncated, page.Diagnostics.Contains(Diagnostic));
    }

    [Fact]
    public void ExactlyTenThousandMalformedTokensStillAllowTheSuffix()
    {
        PdfRenderedPage page = Render(Prefix + new string(')', 10_000) + "\n" + Suffix);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(page, 50, 22));
        Assert.DoesNotContain(Diagnostic, page.Diagnostics);
    }

    [Fact]
    public void RichReaderReportsTruncationAndKeepsOnlyActualPrefixInstructions()
    {
        PdfPageContent content = new PdfPageContentReader(PdfDocument.OpenWithCompatibilityRecovery(
            Pdf(Prefix + new string(')', 10_001) + "\n" + Suffix))).Read(0);
        Assert.Contains(Diagnostic, content.Diagnostics);
        Assert.Single(content.Paths);
        Assert.Single(content.Instructions, instruction => instruction.Operator == "re");
        Assert.Empty(content.Letters);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstructionOnlyReadersRejectIncompleteContent(bool recovery)
    {
        string source = Prefix + new string(')', 10_001) + "\n" + Suffix;
        Assert.Throws<PdfSyntaxException>(() => PdfContentStreamReader.Read(
            Encoding.ASCII.GetBytes(source), compatibilityRecovery: recovery));
        PdfDocument document = recovery ? PdfDocument.OpenWithCompatibilityRecovery(Pdf(source))
            : PdfDocument.Open(Pdf(source));
        Assert.Throws<PdfSyntaxException>(() => new PdfPageContentReader(document).ReadInstructions(0));
    }

    [Fact]
    public void StrictRichReaderAndRendererStillRejectMalformedContent()
    {
        PdfDocument document = PdfDocument.Open(Pdf(Prefix + new string(')', 10_001) + "\n" + Suffix));
        Assert.Throws<PdfSyntaxException>(() => new PdfPageContentReader(document).Read(0));
        Assert.Throws<PdfSyntaxException>(() => new PdfPageRenderer(document)
            .Render(0, new PdfRenderOptions(64, 64)));
    }

    [Fact]
    public void CancellationAndOperandLimitsRemainFailuresDuringRecovery()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new PdfPageRenderer(
            PdfDocument.OpenWithCompatibilityRecovery(Pdf(Prefix + new string(')', 10_001))))
            .Render(0, new PdfRenderOptions(64, 64), canceled.Token));
        PdfDocument excessiveOperands = PdfDocument.OpenWithCompatibilityRecovery(
            Pdf(string.Concat(Enumerable.Repeat("1 ", 4097)) + "m"));
        Assert.Throws<PdfSyntaxException>(() => new PdfPageContentReader(excessiveOperands).Read(0));
    }

    [Fact]
    public void RedactionRefusesBudgetExhaustedContentWithoutRasterApproval()
    {
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(
            Pdf(Prefix + new string(')', 10_001) + "\n" + Suffix));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(42, 38, 58, 48));
        var error = Assert.Throws<PdfSyntaxException>(() => PdfContentRedaction.Apply(document, [region]));
        Assert.Contains("Content recovery limit exceeded", error.Message);
    }

    private static byte[] Pixel(PdfRenderedPage page, int x, int y)
        => page.Pixels.Span.Slice((y * 64 + x) * 4, 4).ToArray();

    private static PdfRenderedPage Render(string content)
        => new PdfPageRenderer(PdfDocument.OpenWithCompatibilityRecovery(Pdf(content)))
            .Render(0, new PdfRenderOptions(64, 64) { CacheResult = false });

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
