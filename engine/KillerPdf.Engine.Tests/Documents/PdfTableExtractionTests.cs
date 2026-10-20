using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfTableExtractionTests
{
    [Fact]
    public void ExtractsRuledCellsInReadingOrderAndRetainsOriginalTextWhenCorrected()
    {
        var content = new PdfContentStreamBuilder();
        foreach (double x in new[] { 20d, 120, 220 })
            content.MoveTo(x, 20).LineTo(x, 120).Stroke();
        foreach (double y in new[] { 20d, 70, 120 })
            content.MoveTo(20, y).LineTo(220, y).Stroke();
        Text(content, 25, 100, "0012");
        Text(content, 125, 100, "Name");
        Text(content, 25, 50, "First");
        Text(content, 25, 35, "Second");
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(300, 200, content).Build());

        PdfTableExtractionResult result = PdfTableExtractor.Extract(
            document, 0, new PdfContentBounds(10, 10, 230, 130));

        Assert.True(result.Succeeded);
        PdfExtractedTable table = Assert.IsType<PdfExtractedTable>(result.Table);
        Assert.Equal(2, table.RowCount);
        Assert.Equal(2, table.ColumnCount);
        Assert.Equal(new[] { "0012", "Name", "First\nSecond", "" }, table.Cells.Select(cell => cell.Text));
        Assert.Equal(new PdfContentBounds(20, 70, 120, 120), table.GetCell(0, 0).Bounds);
        PdfExtractedTable corrected = table.WithCellText(0, 0, "0013");
        Assert.Equal("0013", corrected.GetCell(0, 0).Text);
        Assert.Equal("0012", corrected.GetCell(0, 0).OriginalText);
        Assert.Equal("0012", table.GetCell(0, 0).Text);
    }

    [Fact]
    public void RejectsMergedCellsInsteadOfInventingMissingDividers()
    {
        var content = new PdfContentStreamBuilder().Rectangle(20, 20, 200, 100).Stroke()
            .MoveTo(20, 70).LineTo(220, 70).Stroke()
            .MoveTo(120, 20).LineTo(120, 70).Stroke();
        Text(content, 25, 100, "Merged heading");
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(300, 200, content).Build());

        PdfTableExtractionResult result = PdfTableExtractor.Extract(
            document, 0, new PdfContentBounds(10, 10, 230, 130));

        Assert.False(result.Succeeded);
        Assert.Null(result.Table);
        Assert.Equal(PdfTableExtractionFailure.IncompleteGrid, result.Failure);
    }

    [Theory]
    [InlineData("image", PdfTableExtractionFailure.ScannedRegion)]
    [InlineData("rotation", PdfTableExtractionFailure.UnsupportedTextDirection)]
    [InlineData("columns", PdfTableExtractionFailure.LimitExceeded)]
    [InlineData("rows", PdfTableExtractionFailure.LimitExceeded)]
    public void ReportsUnsupportedSelectionsWithoutAnExportableTable(string kind, PdfTableExtractionFailure failure)
    {
        var content = new PdfContentStreamBuilder();
        int columns = kind == "columns" ? 51 : 2;
        int rows = kind == "rows" ? 101 : 2;
        for (int x = 0; x <= columns; x++) content.MoveTo(20 + x * 10, 20).LineTo(20 + x * 10, 20 + rows * 10).Stroke();
        for (int y = 0; y <= rows; y++) content.MoveTo(20, 20 + y * 10).LineTo(20 + columns * 10, 20 + y * 10).Stroke();
        if (kind == "rotation")
            content.BeginText().SetFont(PdfStandardFont.Helvetica, 5)
                .SetTextMatrix(0, 1, -1, 0, 25, 22).ShowLatin1Text("A").EndText();
        if (kind == "image")
            content.DrawImage(PdfImage.FromRgb(1, 1, new byte[] { 0, 0, 0 }), 20, 20, 20, 20);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1100, 1100, content).Build());

        PdfTableExtractionResult result = PdfTableExtractor.Extract(document, 0,
            new PdfContentBounds(10, 10, 1090, 1090));

        Assert.False(result.Succeeded);
        Assert.Null(result.Table);
        Assert.Equal(failure, result.Failure);
    }

    [Theory]
    [InlineData("opacity")]
    [InlineData("clip")]
    [InlineData("white")]
    [InlineData("invisible-text")]
    [InlineData("overpaint")]
    [InlineData("crossing-text")]
    public void RejectsUncertainOrInvisibleTableContent(string kind)
    {
        var content = new PdfContentStreamBuilder();
        if (kind == "opacity") content.SetOpacity(0);
        if (kind == "clip") content.Rectangle(0, 0, 1, 1).Clip().EndPath();
        if (kind == "white") content.SetStrokeGray(1);
        content.Rectangle(20, 20, 200, 100).Stroke()
            .MoveTo(120, 20).LineTo(120, 120).Stroke()
            .MoveTo(20, 70).LineTo(220, 70).Stroke();
        if (kind == "invisible-text")
            content.BeginText().SetFont(PdfStandardFont.Helvetica, 10)
                .SetTextRenderingMode(PdfTextRenderingMode.Invisible).MoveText(25, 100).ShowLatin1Text("Text").EndText();
        else Text(content, kind == "crossing-text" ? 118 : 25, 100, "Text");
        if (kind == "overpaint") content.SetFillGray(1).Rectangle(20, 20, 200, 100).Fill();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(300, 200, content).Build());

        PdfTableExtractionResult result = PdfTableExtractor.Extract(document, 0,
            new PdfContentBounds(10, 10, 230, 130));

        Assert.False(result.Succeeded);
        Assert.Null(result.Table);
        Assert.Equal("UncertainContent", result.Failure.ToString());
    }

    [Fact]
    public void ExtractsThinFilledRectangleRulesInCropRelativeCoordinatesOnARotatedPage()
    {
        var content = new PdfContentStreamBuilder();
        foreach (double x in new[] { 30d, 130, 230 }) content.Rectangle(x - 0.5, 40, 1, 100).Fill();
        foreach (double y in new[] { 40d, 90, 140 }) content.Rectangle(30, y - 0.5, 200, 1).Fill();
        Text(content, 35, 120, "Caf\u00e9");
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(300, 200, content)
            .SetPageBox(0, PdfPageBox.Crop, 10, 20, 250, 160).SetPageRotation(0, 90).Build());

        PdfTableExtractionResult result = PdfTableExtractor.Extract(document, 0,
            new PdfContentBounds(10, 10, 230, 130));

        Assert.True(result.Succeeded);
        PdfExtractedTable table = Assert.IsType<PdfExtractedTable>(result.Table);
        Assert.Equal(new PdfContentBounds(20, 20, 220, 120), table.Bounds);
        Assert.Equal("Caf\u00e9", table.GetCell(0, 0).Text);
        Assert.Equal(4, table.Cells.Count);
        Assert.Equal("Caf\u00e9", string.Concat(table.GetCell(0, 0).Letters.Select(letter => letter.Value)));
    }

    [Fact]
    public void BlankSelectionsDoNotInventTablesAndCancellationDoesNotReturnAPartialTable()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(300, 200).Build());
        var region = new PdfContentBounds(10, 10, 230, 130);

        PdfTableExtractionResult result = PdfTableExtractor.Extract(document, 0, region);

        Assert.Null(result.Table);
        Assert.Equal(PdfTableExtractionFailure.NoRuledTable, result.Failure);
        Assert.Throws<OperationCanceledException>(() =>
            PdfTableExtractor.Extract(document, 0, region, new CancellationToken(true)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfTableExtractor.Extract(document, 0, region with { Left = double.NaN }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsExplicitTextSpacingRatherThanInventingIdentifierCharacters(bool wordSpacing)
    {
        var content = new PdfContentStreamBuilder().Rectangle(20, 20, 200, 100).Stroke()
            .MoveTo(120, 20).LineTo(120, 120).Stroke()
            .MoveTo(20, 70).LineTo(220, 70).Stroke()
            .BeginText().SetFont(PdfStandardFont.Helvetica, 10);
        if (wordSpacing) content.SetWordSpacing(3);
        else content.SetCharacterSpacing(3);
        content.MoveText(25, 100).ShowLatin1Text("0012").EndText();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(300, 200, content).Build());

        PdfTableExtractionResult result = PdfTableExtractor.Extract(document, 0,
            new PdfContentBounds(10, 10, 230, 130));

        Assert.False(result.Succeeded);
        Assert.Null(result.Table);
        Assert.Equal(PdfTableExtractionFailure.UncertainContent, result.Failure);
    }

    [Theory]
    [InlineData(16, 1)]
    [InlineData(100, 1)]
    [InlineData(1, 100)]
    public void RejectsStrokesThatCanConcealTheExtractedText(double lineWidth, double scale)
    {
        var content = new PdfContentStreamBuilder();
        Text(content, 22, 100, "1");
        content.SetLineWidth(lineWidth);
        if (scale != 1) content.Transform(scale, 0, 0, scale, 0, 0);
        content.Rectangle(20 / scale, 20 / scale, 200 / scale, 100 / scale).Stroke()
            .MoveTo(120 / scale, 20 / scale).LineTo(120 / scale, 120 / scale).Stroke()
            .MoveTo(20 / scale, 70 / scale).LineTo(220 / scale, 70 / scale).Stroke();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(300, 200, content).Build());

        PdfTableExtractionResult result = PdfTableExtractor.Extract(document, 0,
            new PdfContentBounds(10, 10, 230, 130));

        Assert.False(result.Succeeded);
        Assert.Null(result.Table);
        Assert.Equal(PdfTableExtractionFailure.UncertainContent, result.Failure);
    }

    private static void Text(PdfContentStreamBuilder content, double x, double y, string text) =>
        content.BeginText().SetFont(PdfStandardFont.Helvetica, 10)
            .MoveText(x, y).ShowLatin1Text(text).EndText();
}
