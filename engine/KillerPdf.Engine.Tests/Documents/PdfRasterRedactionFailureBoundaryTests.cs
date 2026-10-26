using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfRasterRedactionFailureBoundaryTests
{
    [Theory]
    [InlineData("(A) Tj")]
    [InlineData("[(A)] TJ")]
    [InlineData("(A) '")]
    [InlineData("0 0 (A) \"")]
    public void RasterRedactionRefusesUnreadableType3SearchLayerBeforeReturning(string show)
    {
        byte[] source = Pdf(show, malformedMap: true, drawGlyph: true);
        byte[] original = source.ToArray();
        PdfDocument document = PdfDocument.Open(source);
        PdfRenderedPage visible = Render(document);
        PdfRenderedPage absent = Render(PdfDocument.Open(Pdf(show, malformedMap: true, drawGlyph: false)));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(visible, 12, 39));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(absent, 12, 39));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(visible, 44, 19));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(absent, 44, 19));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(6, 18, 22, 36));
        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(document, [region]);
        Assert.Equal(new[] { 0 }, plan.RasterPages);
        Assert.Contains("UnreadableFont", Assert.Single(plan.Pages).RasterReasons);
        PdfContentRedactionResult? result = null;
        var error = Assert.Throws<NotSupportedException>(() => result = PdfContentRedaction.Apply(document,
            [region], new PdfContentRedactionOptions { ApprovedRasterPages = [0], RasterDpi = 72 }));
        Assert.Contains("NoSuchUnicodeMap", error.Message);
        Assert.Null(result);
        Assert.Equal(original, source);
    }

    [Fact]
    public void ValidType3RedactionKeepsUncoveredPageArtwork()
    {
        PdfDocument document = PdfDocument.Open(Pdf("(A) Tj", malformedMap: false, drawGlyph: true));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(6, 18, 22, 36));
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);
        Assert.True(result.Verification.Succeeded);
        PdfDocument reopened = PdfDocument.Open(result.Document);
        Assert.DoesNotContain("A", new PdfPageContentReader(reopened).Read(0).Text);
        PdfRenderedPage page = Render(reopened);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(page, 12, 39));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(page, 44, 19));
    }

    [Theory]
    [InlineData(33, false)]
    [InlineData(1, true)]
    public void RasterRedactionRefusesDepthLimitedOrCyclicFormsBeforeReturning(int depth, bool cycle)
    {
        byte[] source = Forms(depth, cycle);
        byte[] original = source.ToArray();
        PdfDocument document = PdfDocument.Open(source);
        var region = new PdfRedactionRegion(0, new PdfContentBounds(6, 18, 22, 36));
        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(document, [region]);
        Assert.Equal(new[] { 0 }, plan.RasterPages);
        Assert.Contains("FormNesting", Assert.Single(plan.Pages).RasterReasons);
        PdfContentRedactionResult? result = null;
        Exception? error = Record.Exception(() => result = PdfContentRedaction.Apply(document,
            [region], new PdfContentRedactionOptions { ApprovedRasterPages = [0], RasterDpi = 72 }));
        Assert.True(error is FormatException or InsufficientExecutionStackException,
            $"Expected a managed nesting refusal, got {error?.GetType().Name ?? "a returned result"}.");
        if (error is FormatException) Assert.Contains("Form", error.Message, StringComparison.OrdinalIgnoreCase);
        else Assert.Contains("execution stack", error!.Message);
        Assert.Null(result);
        Assert.Equal(original, source);
    }

    [Fact]
    public void ThirtyTwoFormLevelsAreRejectedByStrictReadback()
    {
        PdfDocument document = PdfDocument.Open(Forms(32, false));
        var error = Assert.Throws<FormatException>(() => new PdfPageContentReader(document).Read(0));
        Assert.Contains("Form nesting", error.Message);
    }

    [Fact]
    public void OrdinaryNestedFormRedactionKeepsUncoveredPageArtwork()
    {
        PdfDocument document = PdfDocument.Open(Forms(2, false));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(6, 18, 22, 36));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(Render(document), 12, 39));
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);
        Assert.True(result.Verification.Succeeded);
        PdfRenderedPage page = Render(PdfDocument.Open(result.Document));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(page, 12, 39));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(page, 44, 19));
    }

    private static byte[] Forms(int depth, bool cycle)
    {
        string Stream(string content, string extra = "")
            => $"<< /Length {content.Length} {extra} >>\nstream\n{content}\nendstream";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 64 64] /Resources << /XObject << /Fm 5 0 R >> >> /Contents 4 0 R >>",
            Stream("/Fm Do\n0 1 0 rg 40 42 8 6 re f")
        };
        for (int level = 0; level < depth; level++)
        {
            bool leaf = level + 1 == depth && !cycle;
            int next = level + 1 == depth ? 5 : level + 6;
            objects.Add(Stream(leaf ? "0 0 1 rg 8 20 12 14 re f" : "/Next Do",
                "/Type /XObject /Subtype /Form /BBox [0 0 64 64] /Resources << "
                + (leaf ? "" : $"/XObject << /Next {next} 0 R >> ") + ">>"));
        }
        return Build(objects);
    }

    private static PdfRenderedPage Render(PdfDocument document) => new PdfPageRenderer(document)
        .Render(0, new PdfRenderOptions(64, 64) { CacheResult = false });
    private static byte[] Pixel(PdfRenderedPage page, int x, int y)
        => page.Pixels.Span.Slice((y * 64 + x) * 4, 4).ToArray();

    private static byte[] Pdf(string show, bool malformedMap, bool drawGlyph)
    {
        string Stream(string content, string extra = "")
            => $"<< /Length {content.Length} {extra} >>\nstream\n{content}\nendstream";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 64 64] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 600 700] /FontMatrix [.001 0 0 .001 0 0] "
                + "/Encoding << /Type /Encoding /Differences [65 /A] >> /FirstChar 65 /LastChar 65 /Widths [600] "
                + "/CharProcs << /A 6 0 R >> /Resources << >> " + (malformedMap ? "/ToUnicode 7 0 R " : "") + ">>",
            Stream($"BT /F1 20 Tf 1 0 0 1 8 20 Tm {show} ET\n0 1 0 rg 40 42 8 6 re f"),
            Stream("600 0 d0\n" + (drawGlyph ? "0 0 1 rg 0 0 600 700 re f" : "")),
            Stream("", "/UseCMap /NoSuchUnicodeMap")
        ];
        return Build(objects);
    }

    private static byte[] Build(IReadOnlyList<string> objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string value in objects)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{offsets.Count} 0 obj\n{value}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
