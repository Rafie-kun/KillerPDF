using System.Windows;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class TextReplacePlannerTests
{
    private static ReplaceWord Word(string text, double left, double top, double width = 40, double height = 12)
        => new(text, new Rect(left, top, width, height));

    [Fact]
    public void FindAll_HonorsMatchCaseAndWholeWord()
    {
        const string text = "Cat catalog cat CAT";
        Assert.Equal([0, 4, 12, 16], TextReplacePlanner.FindAll(text, "cat", matchCase: false, wholeWord: false));
        Assert.Equal([4, 12], TextReplacePlanner.FindAll(text, "cat", matchCase: true, wholeWord: false));
        Assert.Equal([0, 12, 16], TextReplacePlanner.FindAll(text, "cat", matchCase: false, wholeWord: true));
        Assert.Equal([12], TextReplacePlanner.FindAll(text, "cat", matchCase: true, wholeWord: true));
    }

    [Fact]
    public void FindAll_DoesNotOverlap()
    {
        Assert.Equal([0, 2], TextReplacePlanner.FindAll("aaaa", "aa", matchCase: true, wholeWord: false));
    }

    [Fact]
    public void Plan_RewritesEachLineThatContainsAMatch()
    {
        var words = new[]
        {
            Word("Old", 10, 10), Word("Company", 60, 10), Word("Name", 110, 10),
            Word("Nothing", 10, 40), Word("here", 60, 40),
            Word("old", 10, 70), Word("company", 60, 70), Word("name", 110, 70)
        };

        var plans = TextReplacePlanner.Plan(words, "Old Company Name", "New Company Name",
            matchCase: false, wholeWord: false);

        Assert.Equal(2, plans.Count);
        Assert.All(plans, plan => Assert.Equal("New Company Name", plan.NewText));
        Assert.Equal([0, 1, 2], plans[0].WordIndices);
        Assert.Equal(new Rect(10, 10, 140, 12), plans[0].Bounds);
        Assert.Single(plans[0].MatchBounds);
    }

    [Fact]
    public void Plan_KeepsTheRestOfTheLine()
    {
        var words = new[] { Word("Pay", 10, 10), Word("ACME", 60, 10), Word("today.", 110, 10) };

        ReplaceLine plan = Assert.Single(TextReplacePlanner.Plan(words, "ACME", "Killer Inc",
            matchCase: true, wholeWord: true));

        Assert.Equal("Pay ACME today.", plan.OriginalText);
        Assert.Equal("Pay Killer Inc today.", plan.NewText);
        Assert.Equal(new Rect(60, 10, 40, 12), Assert.Single(plan.MatchBounds));
    }

    [Fact]
    public void Plan_SplitsColumnsAtAWideGap()
    {
        var words = new[] { Word("left", 10, 10), Word("value", 400, 10) };

        var plans = TextReplacePlanner.Plan(words, "value", "x", matchCase: false, wholeWord: false);

        ReplaceLine plan = Assert.Single(plans);
        Assert.Equal([1], plan.WordIndices);
        Assert.Equal("x", plan.NewText);
    }

    [Fact]
    public void Plan_IncludeMatchLimitsTheOccurrencesReplaced()
    {
        var words = new[] { Word("red", 10, 10), Word("and", 60, 10), Word("red", 110, 10) };

        ReplaceLine plan = Assert.Single(TextReplacePlanner.Plan(words, "red", "blue",
            matchCase: false, wholeWord: true, includeMatch: bounds => bounds.Left > 100));

        Assert.Equal("red and blue", plan.NewText);
    }

    [Fact]
    public void Plan_ReturnsNothingWithoutAMatch()
    {
        var words = new[] { Word("alpha", 10, 10) };
        Assert.Empty(TextReplacePlanner.Plan(words, "beta", "gamma", matchCase: false, wholeWord: false));
        Assert.Empty(TextReplacePlanner.Plan(words, "", "gamma", matchCase: false, wholeWord: false));
    }
}
