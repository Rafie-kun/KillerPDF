using System.Windows;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// Rotating pages remaps their pending annotations through the page's annotation space. Pages not drawn
// since the last reload have no recorded render size, and used to be skipped, so a second rotation in
// Single or Grid view left their annotations a quarter turn off when the burn placed them.
public sealed class AnnotationSpaceTests
{
    [Fact]
    public void ADrawnPageUsesItsRecordedRenderSize()
    {
        var dims = new Dictionary<int, (int w, int h)> { [3] = (1400, 2048) };

        Assert.Equal((1400, 2048), PdfEngineBurn.AnnotationSpace(dims, 3, 612, 792));
    }

    [Theory]
    [InlineData(612, 792, 1583, 2048)]
    [InlineData(792, 612, 2048, 1583)]
    [InlineData(300, 300, 2048, 2048)]
    public void AnUndrawnPageUsesTheViewersCanonicalLayoutSize(double width, double height, int expectedWidth, int expectedHeight)
    {
        var dims = new Dictionary<int, (int w, int h)> { [0] = (0, 0) };

        Assert.Equal((expectedWidth, expectedHeight), PdfEngineBurn.AnnotationSpace(dims, 0, width, height));
        Assert.Equal((expectedWidth, expectedHeight), PdfEngineBurn.AnnotationSpace(dims, 7, width, height));
    }

    // Two quarter turns of an undrawn portrait page, each remapped in the space the burn reads for that
    // orientation, put a mark where one half turn puts it.
    [Fact]
    public void TwoTurnsOfAnUndrawnPageMatchOneHalfTurn()
    {
        var empty = new Dictionary<int, (int w, int h)>();
        (int w0, int h0) = PdfEngineBurn.AnnotationSpace(empty, 0, 612, 792);
        (int w1, int h1) = PdfEngineBurn.AnnotationSpace(empty, 0, 792, 612);
        var twice = new HighlightAnnotation { Bounds = new Rect(10, 20, 100, 40) };
        var once = new HighlightAnnotation { Bounds = new Rect(10, 20, 100, 40) };

        AnnotationRotate.Remap([twice], 90, w0, h0);
        AnnotationRotate.Remap([twice], 90, w1, h1);
        AnnotationRotate.Remap([once], 180, w0, h0);

        Assert.InRange(Math.Abs(twice.Bounds.X - once.Bounds.X), 0, 1.5);
        Assert.InRange(Math.Abs(twice.Bounds.Y - once.Bounds.Y), 0, 1.5);
        Assert.InRange(Math.Abs(twice.Bounds.Width - once.Bounds.Width), 0, 1.5);
        Assert.InRange(Math.Abs(twice.Bounds.Height - once.Bounds.Height), 0, 1.5);
    }
}
