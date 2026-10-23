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
    public void BracketsInsideRightToLeftTextKeepTheTypedCharacter()
    {
        Assert.Equal([new BidiRun(Shalom + " (" + Olam, true), new BidiRun(")", false)],
            BidiLine.VisualRuns(Shalom + " (" + Olam + ")"));
        Assert.Equal(")", BidiLine.Mirrored("("));
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

    // A wrapped line takes its direction from the paragraph: numbers that start the second line of
    // a Hebrew sentence stay with it instead of turning left-to-right.
    [Fact]
    public void WrappedLineKeepsTheDirectionOfItsParagraph()
    {
        string paragraph = "משלוח תוך 5 - 10 ימים";
        int start = paragraph.IndexOf('5');
        Assert.Equal([new BidiRun(" ימים", true), new BidiRun("10", false),
                new BidiRun(" - ", true), new BidiRun("5", false)],
            BidiLine.VisualRuns(paragraph, start, paragraph.Length - start));
    }

    [Fact]
    public void WrappedLineWithoutHebrewStaysLeftToRight()
    {
        string paragraph = Shalom + " 03-1234567 / 054-7654321";
        int start = paragraph.IndexOf('0');
        Assert.Equal([new BidiRun("03-1234567 / 054-7654321", false)],
            BidiLine.VisualRuns(paragraph, start, paragraph.Length - start));
    }

    // Fractions and circled numbers are neutral, not left-to-right letters.
    [Theory]
    [InlineData("½")]
    [InlineData("①")]
    public void NeutralNumberSignsStayInsideHebrew(string sign)
    {
        string line = "הוסיפו " + sign + " כוס";
        Assert.Equal([new BidiRun(line, true)], BidiLine.VisualRuns(line));
    }

    [Fact]
    public void ArabicIndicDigitsKeepTheirOrderInsideHebrew()
    {
        Assert.Equal([new BidiRun("١٢", false), new BidiRun(Shalom + " ", true)],
            BidiLine.VisualRuns(Shalom + " ١٢"));
    }

    [Fact]
    public void ComparisonSignsInsideHebrewAreMirrored()
    {
        Assert.Equal([new BidiRun("18", false), new BidiRun("גיל ≥ ", true)],
            BidiLine.VisualRuns("גיל ≥ 18"));
        Assert.Equal("≤", BidiLine.Mirrored("≥"));
        Assert.Equal("）", BidiLine.Mirrored("（"));
        Assert.Equal("א", BidiLine.Mirrored("א"));
    }

    // A lone surrogate is read as U+FFFD instead of failing the save.
    [Theory]
    [InlineData("abc\uD800 def")]
    [InlineData("ש\uDC00ל")]
    public void LoneSurrogateDoesNotThrow(string line)
    {
        Assert.Equal(line.Length, BidiLine.VisualRuns(line).Sum(run => run.Text.Length));
    }
}
