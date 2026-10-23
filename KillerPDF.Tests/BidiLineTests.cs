using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class BidiLineTests
{
    private const string Shalom = "שלום";
    private const string Olam = "עולם";

    [Fact]
    public void LatinLineStaysOneLeftToRightRun()
    {
        Assert.Equal([new BidiRun("Invoice 2026, page 3", false)], BidiLine.VisualRuns("Invoice 2026, page 3"));
    }

    [Fact]
    public void HebrewLineIsOneRightToLeftRun()
    {
        Assert.Equal([new BidiRun(Shalom, true)], BidiLine.VisualRuns(Shalom));
    }

    // #435: numbers and Latin inside Hebrew keep their own reading order, and the runs land where a
    // left-to-right text box shows them.
    [Fact]
    public void NumbersAndLatinAfterHebrewKeepTheirReadingOrder()
    {
        Assert.Equal([new BidiRun("123", false), new BidiRun(Shalom + " ", true), new BidiRun(" abc", false)],
            BidiLine.VisualRuns(Shalom + " 123 abc"));
    }

    [Fact]
    public void NumberAfterHebrewSitsLeftOfItWhenLatinLeads()
    {
        Assert.Equal([new BidiRun("abc ", false), new BidiRun("123", false), new BidiRun(Shalom + " ", true)],
            BidiLine.VisualRuns("abc " + Shalom + " 123"));
    }

    [Fact]
    public void SeparatorsInsideANumberStayWithIt()
    {
        Assert.Equal([new BidiRun(" ש", true), new BidiRun("1,000.50", false), new BidiRun(Shalom + " ", true)],
            BidiLine.VisualRuns(Shalom + " 1,000.50 ש"));
    }

    [Fact]
    public void CurrencySignTouchingANumberStaysWithIt()
    {
        Assert.Equal([new BidiRun("50₪", false), new BidiRun(Shalom + " ", true)],
            BidiLine.VisualRuns(Shalom + " 50₪"));
    }

    [Fact]
    public void BracketsInsideRightToLeftTextAreMirrored()
    {
        Assert.Equal([new BidiRun(Shalom + " )" + Olam, true), new BidiRun(")", false)],
            BidiLine.VisualRuns(Shalom + " (" + Olam + ")"));
    }

    [Fact]
    public void PointedHebrewKeepsEachMarkOnItsLetter()
    {
        string pointed = "שָׁלוֹם";
        Assert.Equal([new BidiRun("12", false), new BidiRun(pointed + " ", true)], BidiLine.VisualRuns(pointed + " 12"));
    }

    [Fact]
    public void TrailingSpaceAfterHebrewStaysAtTheLineEnd()
    {
        Assert.Equal([new BidiRun(Shalom, true), new BidiRun(" ", false)], BidiLine.VisualRuns(Shalom + " "));
    }
}
