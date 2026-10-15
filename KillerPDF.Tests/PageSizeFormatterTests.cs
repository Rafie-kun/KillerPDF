using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PageSizeFormatterTests
{
    [Theory]
    [InlineData(612, 792, "Letter", false)]
    [InlineData(792, 612, "Letter", false)]
    [InlineData(612, 1008, "Legal", false)]
    [InlineData(792, 1224, "Tabloid", false)]
    [InlineData(1224, 792, "Ledger", false)]
    [InlineData(1584, 2448, "ANSI D", false)]
    [InlineData(595.28, 841.89, "A4", true)]
    [InlineData(841.89, 595.28, "A4", true)]
    [InlineData(2383.94, 3370.39, "A0", true)]
    public void CommonPaperSizesAreNamedInEveryFooterUnit(double width, double height, string name, bool metric)
    {
        var size = PageSizeFormatter.Format(width, height);

        Assert.Equal(metric, size.Metric);
        Assert.StartsWith(name + "  ", size.Label, StringComparison.Ordinal);
        Assert.StartsWith(name + "  ", size.Pixels, StringComparison.Ordinal);
        Assert.StartsWith(name + "  ", size.Inches, StringComparison.Ordinal);
        Assert.StartsWith(name + "  ", size.Millimeters, StringComparison.Ordinal);
        Assert.StartsWith(name + "  ", size.Points, StringComparison.Ordinal);
    }

    [Fact]
    public void OddSizesShowDimensionsWithoutAName()
    {
        var size = PageSizeFormatter.Format(500, 500);

        Assert.EndsWith(" in", size.Label, StringComparison.Ordinal);
        Assert.False(char.IsLetter(size.Label[0]));
        Assert.EndsWith(" pt", size.Points, StringComparison.Ordinal);
    }
}
