using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// A frame drawn with a line back to its start and then closed can end a hair away from the
// start. That leftover segment has no meaningful direction, and a miter join on it drew a spike
// out of the corner (GWG061). It must stroke like the cleanly closed path.
public sealed class PdfStrokeClosingSegmentTests
{
    [Theory]
    [InlineData("10.00001 10")]
    [InlineData("10 10.00001")]
    [InlineData("10 10")]
    public void LineBackToTheStartThenCloseStrokesLikeAClosedPath(string end)
    {
        const string square = "0 j 6 w 10 10 m 90 10 l 90 90 l 10 90 l ";
        PdfRenderedPage expected = Render(square + "h S");
        PdfRenderedPage actual = Render(square + end + " l h S");
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    // With stroke adjustment, the copy of the start point that ends the line back to it snapped
    // only along that line's normal while the start snapped along the first segment's, so the
    // two parted into a short diagonal and its miter spiked (GWG061, which sets /SA true).
    [Fact]
    public void AdjustedLineBackToTheStartThenCloseStrokesLikeAClosedPath()
    {
        const string frame = "/Adjust gs 1 w 1.417 90.547 m 1.417 1.649 l 93.7 1.649 l 93.7 90.547 l ";
        PdfRenderedPage expected = Render(PdfStrokeAdjustmentTests.Document(frame + "h S", new PdfBoolean(true)));
        PdfRenderedPage actual = Render(PdfStrokeAdjustmentTests.Document(
            frame + "1.417 90.547 l h S", new PdfBoolean(true)));
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    private static PdfRenderedPage Render(string content) =>
        Render(PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100, Encoding.ASCII.GetBytes(content)).Build()));

    private static PdfRenderedPage Render(PdfDocument document) => new PdfPageRenderer(document)
        .Render(0, new PdfRenderOptions(100, 100, includeAnnotations: false, includeFormFields: false));
}
