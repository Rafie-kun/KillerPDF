using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfTextReflowTests
{
    [Fact]
    public void WrapUsesFontMetricsAndRetainsParagraphBreaks()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false));
        double width = Assert.Single(PdfTextReflow.Wrap("A", font, 10, 100)).Width;

        IReadOnlyList<PdfReflowLine> lines = PdfTextReflow.Wrap(
            "AAA A\r\n\r\nA", font, 10, width * 1.1);

        Assert.Equal(["A", "A", "A", "A", "", "A"],
            lines.Select(line => line.Text));
        Assert.All(lines.Where(line => line.Text.Length > 0),
            line => Assert.Equal(width, line.Width));
    }

    [Fact]
    public void AFirstLineIndentShortensOnlyTheFirstLine()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false));
        double width = Assert.Single(PdfTextReflow.Wrap("A", font, 10, 100)).Width;
        double space = Assert.Single(PdfTextReflow.Wrap("A A", font, 10, 100)).Width - width * 2;

        IReadOnlyList<PdfReflowLine> lines = PdfTextReflow.Wrap("A A A A A", font, 10,
            width * 3 + space * 2, width + space);

        Assert.Equal(["A A", "A A A"], lines.Select(line => line.Text));
    }

    [Fact]
    public void AFirstLineIndentOnlyAppliesOnceAndBreaksALongFirstWordFromTheShorterWidth()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false));
        double width = Assert.Single(PdfTextReflow.Wrap("A", font, 10, 100)).Width;

        IReadOnlyList<PdfReflowLine> lines = PdfTextReflow.Wrap("AAAAA\nAAA", font, 10, width * 3.1, width);

        Assert.Equal(["AA", "AAA", "AAA"], lines.Select(line => line.Text));
    }

    [Fact]
    public void AFirstLineIndentMustLeaveRoomOnTheLine()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false));

        Assert.Throws<ArgumentOutOfRangeException>(() => PdfTextReflow.Wrap("A", font, 10, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfTextReflow.Wrap("A", font, 10, 10, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfTextReflow.Wrap("A", font, 10, 10, double.NaN));
    }

    [Fact]
    public void CreateUnicodeContentEmbedsFontAndWritesWrappedLines()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false));
        double width = Assert.Single(PdfTextReflow.Wrap("A", font, 10, 100)).Width;
        PdfContentStreamBuilder content = PdfTextReflow.CreateUnicodeContent(
            "AA", font, 10, width * 1.1, 20, 80, 12);

        byte[] bytes = new PdfDocumentBuilder().AddPage(100, 100, content).Build();
        PdfPageContent page = new PdfPageContentReader(PdfDocument.Open(bytes)).Read(0);

        Assert.Equal(["A", "A"], page.Lines.Select(line => line.Text));
        Assert.Contains("/FontFile2", Encoding.Latin1.GetString(bytes),
            StringComparison.Ordinal);
    }

    [Fact]
    public void WrapRejectsInvalidGeometryAndOversizedInput()
    {
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfTextReflow.Wrap("A", font, 0, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfTextReflow.Wrap("A", font, 10, double.PositiveInfinity));
        Assert.Throws<ArgumentException>(() =>
            PdfTextReflow.Wrap(new string('A', 1_000_001), font, 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfTextReflow.CreateUnicodeContent("A", font, 10, 10, 0, 0, 0));
    }

    [Fact]
    public void ReplaceTextObjectRemovesOriginalAndSavesEmbeddedReflowedText()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 10)
                .MoveText(10, 80).ShowLatin1Text("OLD").EndText())
            .Build());
        TrueTypeFont font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false));
        double width = Assert.Single(PdfTextReflow.Wrap("A", font, 10, 100)).Width;

        byte[] bytes = PdfTextReflow.ReplaceTextObject(
            original, 0, 0, "AA", font, 10, width * 1.1, 10, 80, 12);
        PdfDocument replaced = PdfDocument.Open(bytes);
        PdfPageContent page = new PdfPageContentReader(replaced).Read(0);

        Assert.Equal(["A", "A"], page.Lines.Select(line => line.Text));
        Assert.DoesNotContain("OLD", page.Text, StringComparison.Ordinal);
        Assert.Contains("/FontFile2", Encoding.Latin1.GetString(bytes),
            StringComparison.Ordinal);
        Assert.True(PdfDocumentInspector.Inspect(bytes).IsStructurallyValid);
    }
}
