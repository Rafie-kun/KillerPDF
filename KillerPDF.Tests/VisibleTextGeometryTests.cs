using System.Windows;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class VisibleTextGeometryTests
{
    // A 200 by 300 point page drawn on its 1365 by 2048 canvas at 200% zoom: 800 screen pixels
    // tall, so one canvas unit is about 0.39 of a pixel.
    private const double CanvasWidth = 1365, CanvasHeight = 2048, Scale = 800.0 / 2048;
    private static readonly Point PageOrigin = new(235, 101);

    // Visual.PointToScreen returns whole device pixels.
    private static Point ToScreen(Point canvas) =>
        new(Math.Round(PageOrigin.X + canvas.X * Scale), Math.Round(PageOrigin.Y + canvas.Y * Scale));

    // "ORIGINAL BODY TEXT" without its spaces, on one line near the top of the page.
    private static PageTextRuns BodyLine()
    {
        var runs = new PageTextRuns { PdfWidth = 200, PdfHeight = 300 };
        runs.Lines.Add(new RunLine { Start = 0, Count = 16, Top = 258.844, Bottom = 249.772, Left = 30.468, Right = 120 });
        for (int index = 0; index < 16; index++)
            runs.Chars.Add(new RunChar("X", 30.468 + index * 5.6, 30.468 + index * 5.6 + 5, index / 8, 0));
        return runs;
    }

    [Fact]
    public void TextInsideTheViewportIsVisibleWhenACanvasUnitIsUnderHalfAPixel()
    {
        var carets = VisibleTextGeometry.VisibleCarets(BodyLine(), CanvasWidth, CanvasHeight, 0, ToScreen, new Rect(33, 89, 938, 358));
        Assert.Equal(new[] { (0, 16) }, carets);
    }

    [Fact]
    public void TextOutsideTheViewportIsNotVisible()
    {
        // The viewport scrolled below the line.
        var carets = VisibleTextGeometry.VisibleCarets(BodyLine(), CanvasWidth, CanvasHeight, 0, ToScreen, new Rect(33, 400, 938, 358));
        Assert.Empty(carets);
    }

    [Fact]
    public void AViewportEdgeSplitsTheLine()
    {
        // Characters start at canvas x 208 (screen 316) and step 38 canvas units (15 pixels).
        var carets = VisibleTextGeometry.VisibleCarets(BodyLine(), CanvasWidth, CanvasHeight, 0, ToScreen, new Rect(33, 89, 330, 358));
        Assert.Single(carets);
        Assert.Equal(0, carets[0].Start);
        Assert.InRange(carets[0].End, 1, 15);
    }
}
