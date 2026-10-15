using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class SvgPathDataTests
{
    [Theory]
    [InlineData("m10 10 h 20 v 10 z", "M 10 10 L 30 10 L 30 20 Z")]
    [InlineData("M1.5.5L-2e1-3", "M 1.5 0.5 L -20 -3")]
    [InlineData("M0 0 10 10 20 0", "M 0 0 L 10 10 L 20 0")]
    [InlineData("M0 0 Q 10 10 20 0", "M 0 0 C 6.667 6.667 13.333 6.667 20 0")]
    [InlineData("M0 0 C 0 10 10 10 10 0 S 20 -10 20 0", "M 0 0 C 0 10 10 10 10 0 C 10 -10 20 -10 20 0")]
    [InlineData("M0 0 L 10 0 Z l 0 10", "M 0 0 L 10 0 Z M 0 0 L 0 10")]
    public void Parse_NormalizesToAbsoluteMovesLinesAndCubics(string data, string expected) =>
        Assert.Equal(expected, SvgPathData.Format(SvgPathData.Parse(data)));

    [Fact]
    public void Parse_TurnsArcsIntoQuarterTurnCubics()
    {
        List<VectorSegment> arc = SvgPathData.Parse("M0 0 A10 10 0 0 1 20 0");
        Assert.Equal(3, arc.Count);
        Assert.Equal(VectorVerb.Cubic, arc[1].Verb);
        Assert.Equal(10, arc[1].X, 6);
        Assert.Equal(-10, arc[1].Y, 6);
        Assert.Equal(20, arc[2].X, 6);
        Assert.Equal(0, arc[2].Y, 6);

        VectorSegment end = SvgPathData.Parse("M0 0a5 5 0 0110 0")[^1];
        Assert.Equal(10, end.X, 6);
        Assert.Equal(0, end.Y, 6);
    }

    [Theory]
    [InlineData("M 1")]
    [InlineData("M 0 0 A 5 5 0 2 0 10 0")]
    [InlineData("M 0 0 Z 5")]
    public void Parse_RejectsMalformedData(string data) =>
        Assert.Throws<FormatException>(() => SvgPathData.Parse(data));

    [Fact]
    public void Format_RoundTripsThroughParse()
    {
        string data = SvgPathData.Format(SvgPathData.Parse("M 1 2 C 3 4 5 6 7 8 L 9 10 Z"));
        Assert.Equal(data, SvgPathData.Format(SvgPathData.Parse(data)));
    }
}
