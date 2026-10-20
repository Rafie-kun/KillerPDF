using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfTableExportTests
{
    [Fact]
    public void XlsxExportsCorrectedUnicodeCellsAsTextWithoutFormulas()
    {
        PdfExtractedTable table = BlankTable().WithCellText(0, 0, "0012")
            .WithCellText(0, 1, "=1+1").WithCellText(1, 0, "A & <B>\n第二行");

        byte[] bytes = PdfTableExport.Export(table, PdfTableExportFormat.Xlsx);

        using var archive = new ZipArchive(new MemoryStream(bytes));
        using Stream sheetStream = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        XDocument sheet = XDocument.Load(sheetStream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        XElement[] cells = sheet.Descendants(ns + "c").ToArray();
        Assert.Equal(new[] { "A1", "B1", "A2", "B2" }, cells.Select(cell => cell.Attribute("r")!.Value));
        Assert.All(cells, cell => Assert.Equal("inlineStr", cell.Attribute("t")!.Value));
        Assert.Equal(new[] { "0012", "=1+1", "A & <B>\n第二行", "" },
            cells.Select(cell => cell.Descendants(ns + "t").Single().Value));
        Assert.Empty(sheet.Descendants(ns + "f"));
        using Stream stylesStream = archive.GetEntry("xl/styles.xml")!.Open();
        XDocument styles = XDocument.Load(stylesStream);
        Assert.Contains(styles.Descendants(ns + "xf"), style => style.Attribute("numFmtId")?.Value == "49");
    }

    [Theory]
    [InlineData("0012", "0012")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+SUM(A1)", "'+SUM(A1)")]
    [InlineData("-1+2", "'-1+2")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData(" \t=1+1", "' \t=1+1")]
    [InlineData("\u200B\uFEFF=1+1", "'\u200B\uFEFF=1+1")]
    [InlineData("\uFF1D1+1", "'\uFF1D1+1")]
    [InlineData("A,\"B\"\nC", "A,\"\"B\"\"\nC")]
    public void CsvQuotesCellsAndNeutralizesFormulaInitiators(string value, string escaped)
    {
        byte[] bytes = PdfTableExport.Export(BlankTable().WithCellText(0, 0, value), PdfTableExportFormat.Csv);

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal("\"" + escaped + "\",\"\"\r\n\"\",\"\"\r\n", Encoding.UTF8.GetString(bytes[3..]));
    }

    [Theory]
    [InlineData("_x0041_", "_x005F_x0041_")]
    [InlineData("A\0B", "A_x0000_B")]
    [InlineData("A\r\nB", "A_x000D_\nB")]
    public void XlsxPreservesLiteralEscapeSequencesAndControlCharacters(string value, string encoded)
    {
        byte[] bytes = PdfTableExport.Export(BlankTable().WithCellText(0, 0, value), PdfTableExportFormat.Xlsx);
        using var archive = new ZipArchive(new MemoryStream(bytes));
        using Stream stream = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Assert.Equal(encoded, XDocument.Load(stream).Descendants(ns + "t").First().Value);
    }

    [Theory]
    [InlineData(PdfTableExportFormat.Xlsx)]
    [InlineData(PdfTableExportFormat.Csv)]
    public void ExportRejectsOversizedCellsInsteadOfTruncatingOrProducingInvalidWorkbooks(PdfTableExportFormat format)
    {
        PdfExtractedTable table = BlankTable().WithCellText(0, 0, new string('A', 32_768));

        ArgumentException error = Assert.Throws<ArgumentException>(() => PdfTableExport.Export(table, format));

        Assert.Contains("32,767", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfTableExportFormat.Xlsx)]
    [InlineData(PdfTableExportFormat.Csv)]
    public void ExportAcceptsTheCellLimitAndRejectsMalformedTextAndCancellation(PdfTableExportFormat format)
    {
        PdfExtractedTable table = BlankTable().WithCellText(0, 0, new string('A', 32_767));

        Assert.NotEmpty(PdfTableExport.Export(table, format));
        Assert.Throws<ArgumentException>(() => PdfTableExport.Export(table.WithCellText(0, 0, "\uD800"), format));
        Assert.Throws<OperationCanceledException>(() => PdfTableExport.Export(table, format, new CancellationToken(true)));
    }

    private static PdfExtractedTable BlankTable()
    {
        var content = new PdfContentStreamBuilder().Rectangle(20, 20, 200, 100).Stroke()
            .MoveTo(120, 20).LineTo(120, 120).Stroke()
            .MoveTo(20, 70).LineTo(220, 70).Stroke();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(300, 200, content).Build());
        return Assert.IsType<PdfExtractedTable>(PdfTableExtractor.Extract(document, 0,
            new PdfContentBounds(10, 10, 230, 130)).Table);
    }
}
