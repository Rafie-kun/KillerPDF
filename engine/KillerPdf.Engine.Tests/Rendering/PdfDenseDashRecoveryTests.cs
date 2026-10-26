using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfDenseDashRecoveryTests
{
    private const string Stroke = "1 0 0 RG 2 w 0 2 m 8 2 l S ";
    private const string LaterContent = "0 1 0 rg 2 5 4 2 re f";
    private const string RecoveryDiagnostic =
        "A line-dash pattern exceeded the expansion limit and was rendered as a solid stroke.";

    [Theory]
    [InlineData("[0.0000000000001 0.0000000000001]")]
    [InlineData("[0 0.00001599999]")]
    public void CompatibilityRenderingRecoversDenseDashesAndPreservesLaterContent(string pattern)
    {
        var actual = Render(pattern + " 0 d " + Stroke + LaterContent);
        var solid = Render(Stroke + LaterContent);
        var withoutStroke = Render(LaterContent);
        var withoutLaterContent = Render(Stroke);

        Assert.Equal(solid.Pixels.ToArray(), actual.Pixels.ToArray());
        Assert.False(actual.Pixels.Span.SequenceEqual(withoutStroke.Pixels.Span));
        Assert.False(actual.Pixels.Span.SequenceEqual(withoutLaterContent.Pixels.Span));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(actual, 4, 6));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(actual, 4, 1));
        Assert.Equal(new[] { RecoveryDiagnostic }, actual.Diagnostics);
        Assert.Empty(solid.Diagnostics);
    }

    private static PdfRenderedPage Render(string content)
    {
        var document = PdfDocument.OpenWithCompatibilityRecovery(new PdfDocumentBuilder()
            .AddPage(8, 8, Encoding.ASCII.GetBytes(content)).Build());
        return new PdfPageRenderer(document).Render(0, new PdfRenderOptions(8, 8));
    }

    private static byte[] Pixel(PdfRenderedPage page, int x, int y) =>
        page.Pixels.Slice((y * page.Width + x) * 4, 4).ToArray();
}
