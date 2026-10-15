using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class OfficeConversionTests
{
    private const string Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string PackageRels = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string DocumentRels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    [Fact]
    public void Docx_WritesStyledRunsAsText()
    {
        byte[] pdf = DocxConverter.Convert(Docx(
            "<w:p><w:r><w:rPr><w:b/></w:rPr><w:t>Hello</w:t></w:r><w:r><w:t xml:space=\"preserve\"> world</w:t></w:r></w:p>"));

        List<string> pages = PageTexts(pdf);
        Assert.Single(pages);
        Assert.Contains("Hello world", pages[0]);
    }

    [Fact]
    public void Docx_PageBreakStartsANewPage()
    {
        byte[] pdf = DocxConverter.Convert(Docx(
            "<w:p><w:r><w:t>First</w:t></w:r></w:p>" +
            "<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>" +
            "<w:p><w:r><w:t>Second</w:t></w:r></w:p>"));

        List<string> pages = PageTexts(pdf);
        Assert.Equal(2, pages.Count);
        Assert.Contains("First", pages[0]);
        Assert.Contains("Second", pages[1]);
    }

    [Fact]
    public void Docx_LongTextFlowsOntoMorePages()
    {
        var body = new StringBuilder();
        for (int index = 0; index < 120; index++)
            body.Append("<w:p><w:r><w:t>Paragraph ").Append(index).Append(" with enough words to take a line of its own.</w:t></w:r></w:p>");

        List<string> pages = PageTexts(DocxConverter.Convert(Docx(body.ToString())));

        Assert.True(pages.Count > 1);
        Assert.Contains("Paragraph 0 ", pages[0]);
        Assert.Contains("Paragraph 119 ", pages[^1]);
    }

    [Fact]
    public void Docx_NumbersListItemsAndDrawsTables()
    {
        string numbering =
            $"<w:numbering xmlns:w=\"{Word}\"><w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/>" +
            "<w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/><w:pPr><w:ind w:left=\"720\" w:hanging=\"360\"/></w:pPr></w:lvl>" +
            "</w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num></w:numbering>";
        string item = "<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr><w:r><w:t>{0}</w:t></w:r></w:p>";
        string table =
            "<w:tbl><w:tblPr><w:tblBorders><w:top w:val=\"single\" w:sz=\"4\"/><w:bottom w:val=\"single\" w:sz=\"4\"/>" +
            "<w:insideH w:val=\"single\" w:sz=\"4\"/><w:insideV w:val=\"single\" w:sz=\"4\"/></w:tblBorders></w:tblPr>" +
            "<w:tblGrid><w:gridCol w:w=\"3000\"/><w:gridCol w:w=\"3000\"/></w:tblGrid>" +
            "<w:tr><w:tc><w:p><w:r><w:t>Cell A1</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Cell B1</w:t></w:r></w:p></w:tc></w:tr>" +
            "<w:tr><w:tc><w:p><w:r><w:t>Cell A2</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Cell B2</w:t></w:r></w:p></w:tc></w:tr></w:tbl>";

        byte[] pdf = DocxConverter.Convert(Docx(
            string.Format(CultureInfo.InvariantCulture, item, "Apples") + string.Format(CultureInfo.InvariantCulture, item, "Pears") + table,
            numbering));

        PdfDocument document = PdfDocument.Open(pdf);
        PdfPageContent page = new PdfPageContentReader(document).Read(0);
        Assert.Contains("1.", page.Text);
        Assert.Contains("2.", page.Text);
        Assert.Contains("Apples", page.Text);
        Assert.Contains("Cell B2", page.Text);
        Assert.True(page.Paths.Count >= 4);
    }

    [Fact]
    public void Xlsx_PrintsCellsWithTheirNumberFormats()
    {
        byte[] workbook = Xlsx(
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" s=\"1\"><v>1234.5</v></c></row>" +
            "<row r=\"2\"><c r=\"A2\" t=\"inlineStr\"><is><t>Due</t></is></c><c r=\"B2\" s=\"2\"><v>45000</v></c></row>",
            "<sst xmlns=\"" + Sheet + "\"><si><t>Total</t></si></sst>",
            "<styleSheet xmlns=\"" + Sheet + "\"><numFmts><numFmt numFmtId=\"164\" formatCode=\"yyyy-mm-dd\"/></numFmts>" +
            "<fonts><font><sz val=\"11\"/><name val=\"Arial\"/></font></fonts><fills><fill><patternFill patternType=\"none\"/></fill></fills>" +
            "<borders><border/></borders><cellXfs><xf numFmtId=\"0\"/><xf numFmtId=\"4\"/><xf numFmtId=\"164\"/></cellXfs></styleSheet>",
            "<cols><col min=\"2\" max=\"2\" width=\"16\" customWidth=\"1\"/></cols>");

        List<string> pages = PageTexts(XlsxConverter.Convert(workbook, CultureInfo.InvariantCulture));

        Assert.Single(pages);
        Assert.Contains("Total", pages[0]);
        Assert.Contains("1,234.50", pages[0]);
        Assert.Contains("Due", pages[0]);
        Assert.Contains("2023-03-15", pages[0]);
    }

    [Fact]
    public void Xlsx_EmptyWorkbookStillMakesOnePage()
    {
        List<string> pages = PageTexts(XlsxConverter.Convert(Xlsx("", null, null), CultureInfo.InvariantCulture));
        Assert.Single(pages);
    }

    [Theory]
    [InlineData(0.256, 10, null, "25.60%")]
    [InlineData(-1234.5, 0, "#,##0.00;(#,##0.00)", "(1,234.50)")]
    [InlineData(45000.75, 0, "m/d/yyyy h:mm AM/PM", "3/15/2023 6:00 PM")]
    [InlineData(1.5, 46, null, "36:00:00")]
    [InlineData(45000, 0, "mmm d, yyyy", "Mar 15, 2023")]
    [InlineData(0.30000000000000004, 0, null, "0.3")]
    [InlineData(1234567.891, 0, null, "1234567.891")]
    [InlineData(1234.5, 0, "\"$\"#,##0.00", "$1,234.50")]
    [InlineData(0.5, 0, "0.0%", "50.0%")]
    public void NumberFormats_MatchExcel(double value, int id, string? code, string expected)
    {
        Assert.Equal(expected, OfficeNumberFormat.Format(value, id, code, false, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void DocFileThatIsReallyADocxConvertsWithoutWord()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-office-{Guid.NewGuid():N}.doc");
        try
        {
            File.WriteAllBytes(path, Docx("<w:p><w:r><w:t>Renamed</w:t></w:r></w:p>"));
            List<string> pages = PageTexts(OfficeConversion.ToPdf(path));
            Assert.Contains("Renamed", pages[0]);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static List<string> PageTexts(byte[] pdf)
    {
        PdfDocument document = PdfDocument.Open(pdf);
        int count = PdfDocumentInformation.Read(document).PageCount;
        var reader = new PdfPageContentReader(document);
        return [.. Enumerable.Range(0, count).Select(index => reader.Read(index).Text)];
    }

    private static byte[] Docx(string body, string? numbering = null)
    {
        var parts = new List<(string, string)>
        {
            ("_rels/.rels", $"<Relationships xmlns=\"{PackageRels}\"><Relationship Id=\"rId1\" Type=\"{DocumentRels}/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
            ("word/document.xml", $"<w:document xmlns:w=\"{Word}\"><w:body>{body}<w:sectPr><w:pgSz w:w=\"12240\" w:h=\"15840\"/>" +
                "<w:pgMar w:top=\"1440\" w:bottom=\"1440\" w:left=\"1440\" w:right=\"1440\" w:header=\"720\" w:footer=\"720\"/></w:sectPr></w:body></w:document>")
        };
        if (numbering is not null)
        {
            parts.Add(("word/numbering.xml", numbering));
            parts.Add(("word/_rels/document.xml.rels",
                $"<Relationships xmlns=\"{PackageRels}\"><Relationship Id=\"rId2\" Type=\"{DocumentRels}/numbering\" Target=\"numbering.xml\"/></Relationships>"));
        }
        return Package(parts);
    }

    private static byte[] Xlsx(string rows, string? sharedStrings, string? styles, string columns = "")
    {
        var rels = new StringBuilder($"<Relationships xmlns=\"{PackageRels}\"><Relationship Id=\"rId1\" Type=\"{DocumentRels}/worksheet\" Target=\"worksheets/sheet1.xml\"/>");
        var parts = new List<(string, string)>
        {
            ("_rels/.rels", $"<Relationships xmlns=\"{PackageRels}\"><Relationship Id=\"rId1\" Type=\"{DocumentRels}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", $"<workbook xmlns=\"{Sheet}\" xmlns:r=\"{DocumentRels}\"><sheets><sheet name=\"Sheet1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>"),
            ("xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"{Sheet}\">{columns}<sheetData>{rows}</sheetData></worksheet>")
        };
        if (sharedStrings is not null)
        {
            parts.Add(("xl/sharedStrings.xml", sharedStrings));
            rels.Append($"<Relationship Id=\"rId2\" Type=\"{DocumentRels}/sharedStrings\" Target=\"sharedStrings.xml\"/>");
        }
        if (styles is not null)
        {
            parts.Add(("xl/styles.xml", styles));
            rels.Append($"<Relationship Id=\"rId3\" Type=\"{DocumentRels}/styles\" Target=\"styles.xml\"/>");
        }
        parts.Add(("xl/_rels/workbook.xml.rels", rels.Append("</Relationships>").ToString()));
        return Package(parts);
    }

    private static byte[] Package(List<(string Path, string Content)> parts)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string path, string content) in parts)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return buffer.ToArray();
    }
}
