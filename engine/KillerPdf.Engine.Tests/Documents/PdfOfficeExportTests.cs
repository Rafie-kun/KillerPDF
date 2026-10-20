using System.IO.Compression;
using System.Xml.Linq;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOfficeExportTests
{
    [Fact]
    public void WordPagePicturesRetainTheSourceAspectRatio()
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(200, 400, new PdfContentStreamBuilder()).Build());
        using var archive = new ZipArchive(new MemoryStream(PdfStructuredExport.ToDocx(document,
            officeMode: PdfOfficeExportMode.PageImages)));
        using Stream stream = archive.GetEntry("word/document.xml")!.Open();
        XDocument xml = XDocument.Load(stream);
        XNamespace wp = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
        XElement extent = Assert.Single(xml.Descendants(wp + "extent"));
        Assert.Equal(0.5, (double)extent.Attribute("cx")! / (double)extent.Attribute("cy")!, 6);
    }

    [Theory]
    [InlineData(PdfStructuredExportFormat.WordDocument)]
    [InlineData(PdfStructuredExportFormat.Presentation)]
    public void ClippedImagesAreReportedInsteadOfRevealingHiddenPixels(PdfStructuredExportFormat format)
    {
        var content = new PdfContentStreamBuilder().SaveState().Rectangle(20, 20, 10, 10).Clip().EndPath()
            .DrawImage(PdfImage.FromRgb(1, 1, new byte[] { 255, 0, 0 }), 20, 20, 40, 40).RestoreState();
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(200, 200, content).Build());
        using var archive = new ZipArchive(new MemoryStream(PdfStructuredExport.Export(document, format)));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("/media/"));
        Assert.Contains(PdfStructuredExport.InspectLosses(document, format).Findings,
            finding => finding.Code == "ImageContentNotExported");
    }

    [Fact]
    public void WordGroupsAdjacentLinesAndRetainsParagraphGaps()
    {
        var content = new PdfContentStreamBuilder();
        foreach (var line in new[] { (180d, "First line"), (164d, "Second line"), (110d, "New paragraph") })
            content.BeginText().SetFont(PdfStandardFont.Helvetica, 12).MoveText(25, line.Item1).ShowLatin1Text(line.Item2).EndText();
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(300, 220, content).Build());
        using var archive = new ZipArchive(new MemoryStream(PdfStructuredExport.ToDocx(document)));
        using Stream stream = archive.GetEntry("word/document.xml")!.Open();
        XDocument xml = XDocument.Load(stream);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        XElement[] paragraphs = xml.Descendants(w + "p").Where(element => element.Descendants(w + "t").Any()).ToArray();
        Assert.Equal(2, paragraphs.Length);
        Assert.Equal(new[] { "First line", "Second line" }, paragraphs[0].Descendants(w + "t").Select(element => element.Value));
        Assert.Single(paragraphs[0].Descendants(w + "br"));
        Assert.True((int?)paragraphs[1].Element(w + "pPr")?.Element(w + "spacing")?.Attribute(w + "before") > 400);
    }

    [Fact]
    public void PresentationTextScalesWithItsPageWithoutDefaultTextBoxInsets()
    {
        var content = new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .MoveText(20, 100).ShowLatin1Text("Caption").EndText();
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(360, 240, content).Build());
        using var archive = new ZipArchive(new MemoryStream(PdfStructuredExport.ToPptx(document)));
        using Stream stream = archive.GetEntry("ppt/slides/slide1.xml")!.Open();
        XDocument xml = XDocument.Load(stream);
        XNamespace a = "http://schemas.openxmlformats.org/drawingml/2006/main";
        Assert.Equal("2400", Assert.Single(xml.Descendants(a + "rPr")).Attribute("sz")?.Value);
        XElement body = Assert.Single(xml.Descendants(a + "bodyPr"));
        foreach (string edge in new[] { "lIns", "rIns", "tIns", "bIns" })
            Assert.Equal("0", body.Attribute(edge)?.Value);
    }

    [Theory]
    [InlineData(PdfStructuredExportFormat.WordDocument)]
    [InlineData(PdfStructuredExportFormat.Presentation)]
    public void UnsupportedImageOpacityIsReportedInsteadOfExposingInvisiblePixels(PdfStructuredExportFormat format)
    {
        var content = new PdfContentStreamBuilder().SetOpacity(0)
            .DrawImage(PdfImage.FromRgb(1, 1, new byte[] { 255, 0, 0 }), 20, 20, 40, 40);
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(200, 200, content).Build());
        using var archive = new ZipArchive(new MemoryStream(PdfStructuredExport.Export(document, format)));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.Contains("/media/"));
        Assert.Contains(PdfStructuredExport.InspectLosses(document, format).Findings,
            finding => finding.Code == "ImageContentNotExported");
    }

    [Fact]
    public void SavedMacroAndBatchRetainPageImageChoice()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(200, 200, new PdfContentStreamBuilder()
            .BeginText().SetFont(PdfStandardFont.Helvetica, 12).MoveText(20, 100).ShowLatin1Text("Page").EndText()).Build();
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Pictures", [PdfStructuredExportMacro.Step(
            PdfStructuredExportFormat.WordDocument, officeMode: PdfOfficeExportMode.PageImages)]).ToJson());
        PdfMacroStep step = Assert.Single(macro.Steps);
        Assert.Equal(PdfOfficeExportMode.PageImages, PdfStructuredExportMacro.ReadOfficeMode(step));
        byte[] fromMacro = PdfStructuredExportMacro.Execute(step, source).ToArray();
        var result = Assert.Single(PdfStructuredExportBatchRunner.Run([new("source.pdf", source)],
            PdfStructuredExportFormat.WordDocument, officeMode: PdfOfficeExportMode.PageImages));
        Assert.True(result.Succeeded, result.Error);
        foreach (byte[] bytes in new[] { fromMacro, result.Data!.Value.ToArray() })
        {
            using var archive = new ZipArchive(new MemoryStream(bytes));
            using Stream stream = archive.GetEntry("word/document.xml")!.Open();
            XDocument xml = XDocument.Load(stream);
            Assert.DoesNotContain(xml.Descendants(), element => element.Name.LocalName == "t");
            Assert.Single(xml.Descendants(), element => element.Name.LocalName == "blip");
        }
    }

    [Theory]
    [InlineData(PdfStructuredExportFormat.WordDocument, "word/document.xml")]
    [InlineData(PdfStructuredExportFormat.Presentation, "ppt/slides/slide1.xml")]
    public void ImageOutsideTableDoesNotPreventEditableCells(PdfStructuredExportFormat format, string part)
    {
        var content = new PdfContentStreamBuilder().Rectangle(20, 80, 200, 100).Stroke()
            .MoveTo(120, 80).LineTo(120, 180).Stroke()
            .BeginText().SetFont(PdfStandardFont.Helvetica, 12).MoveText(25, 120).ShowLatin1Text("Cell").EndText()
            .DrawImage(PdfImage.FromRgb(1, 1, new byte[] { 255, 0, 0 }), 20, 20, 30, 30);
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(300, 220, content).Build());
        using var archive = new ZipArchive(new MemoryStream(PdfStructuredExport.Export(document, format)));
        using Stream stream = archive.GetEntry(part)!.Open();
        XDocument xml = XDocument.Load(stream);
        Assert.Single(xml.Descendants(), element => element.Name.LocalName == "tbl");
        Assert.Single(xml.Descendants(), element => element.Name.LocalName == "blip");
        Assert.DoesNotContain(PdfStructuredExport.InspectLosses(document, format).Findings,
            finding => finding.Code == "ImageContentNotExported");
    }

    [Fact]
    public void WordKeepsHeadingBeforeTableAndFollowingParagraphAfterIt()
    {
        var content = new PdfContentStreamBuilder().Rectangle(20, 50, 200, 100).Stroke()
            .MoveTo(120, 50).LineTo(120, 150).Stroke();
        foreach (var item in new[] { (180d, "Heading"), (110d, "Cell"), (30d, "Following") })
            content.BeginText().SetFont(PdfStandardFont.Helvetica, 12).MoveText(25, item.Item1).ShowLatin1Text(item.Item2).EndText();
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(300, 220, content).Build());
        using var archive = new ZipArchive(new MemoryStream(PdfStructuredExport.ToDocx(document)));
        using Stream stream = archive.GetEntry("word/document.xml")!.Open();
        XDocument xml = XDocument.Load(stream);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        Assert.Equal(new[] { "Heading", "Cell", "Following" }, xml.Descendants(w + "t").Select(element => element.Value));
    }

    [Theory]
    [InlineData(PdfStructuredExportFormat.WordDocument, "word/document.xml")]
    [InlineData(PdfStructuredExportFormat.Presentation, "ppt/slides/slide1.xml")]
    public void PageImageModeIsExplicitAndReportsReducedEditability(PdfStructuredExportFormat format, string part)
    {
        var content = new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .MoveText(20, 100).ShowLatin1Text("Visible page").EndText();
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(200, 200, content).Build());
        using var archive = new ZipArchive(new MemoryStream(PdfStructuredExport.Export(document, format,
            officeMode: PdfOfficeExportMode.PageImages)));
        using Stream stream = archive.GetEntry(part)!.Open();
        XDocument xml = XDocument.Load(stream);
        Assert.DoesNotContain(xml.Descendants(), element => element.Name.LocalName == "t");
        Assert.Single(xml.Descendants(), element => element.Name.LocalName == "blip");
        Assert.Contains(PdfStructuredExport.InspectLosses(document, format, officeMode: PdfOfficeExportMode.PageImages).Findings,
            finding => finding.Code == "PageImagesNotEditable");
    }

    [Theory]
    [InlineData(PdfStructuredExportFormat.WordDocument, "word/document.xml", "word/media/")]
    [InlineData(PdfStructuredExportFormat.Presentation, "ppt/slides/slide1.xml", "ppt/media/")]
    public void EmbedsAnActualImageAlongsideEditableText(PdfStructuredExportFormat format, string part, string media)
    {
        var content = new PdfContentStreamBuilder()
            .DrawImage(PdfImage.FromRgb(1, 1, new byte[] { 255, 0, 0 }), 20, 20, 40, 40)
            .BeginText().SetFont(PdfStandardFont.Helvetica, 12).MoveText(20, 100).ShowLatin1Text("Caption").EndText();
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(200, 200, content).Build());
        using var archive = new ZipArchive(new MemoryStream(PdfStructuredExport.Export(document, format)));
        ZipArchiveEntry asset = Assert.Single(archive.Entries, entry => entry.FullName.StartsWith(media));
        using var bytes = new MemoryStream();
        using (Stream source = asset.Open()) source.CopyTo(bytes);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes.ToArray()[..8]);
        using Stream xmlStream = archive.GetEntry(part)!.Open();
        XDocument xml = XDocument.Load(xmlStream);
        Assert.Equal("Caption", Assert.Single(xml.Descendants(), element => element.Name.LocalName == "t").Value);
        Assert.Single(xml.Descendants(), element => element.Name.LocalName == "blip");
    }

    [Theory]
    [InlineData(PdfStructuredExportFormat.WordDocument, "word/document.xml", "http://schemas.openxmlformats.org/wordprocessingml/2006/main")]
    [InlineData(PdfStructuredExportFormat.Presentation, "ppt/slides/slide1.xml", "http://schemas.openxmlformats.org/drawingml/2006/main")]
    public void ExportsRuledTableAsEditableCellsWithoutDuplicatingText(PdfStructuredExportFormat format, string part, string ns)
    {
        var content = new PdfContentStreamBuilder();
        foreach (double x in new[] { 20d, 120, 220 })
            content.MoveTo(x, 20).LineTo(x, 120).Stroke();
        foreach (double y in new[] { 20d, 70, 120 })
            content.MoveTo(20, y).LineTo(220, y).Stroke();
        content.BeginText().SetFont(PdfStandardFont.Helvetica, 10)
            .MoveText(25, 100).ShowLatin1Text("0012").EndText();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(300, 200, content).Build());

        using var archive = new ZipArchive(new MemoryStream(PdfStructuredExport.Export(document, format)));
        using Stream stream = archive.GetEntry(part)!.Open();
        XDocument xml = XDocument.Load(stream);
        XNamespace w = ns;
        XElement table = Assert.Single(xml.Descendants(w + "tbl"));
        Assert.Equal(2, table.Elements(w + "tr").Count());
        Assert.Equal(4, table.Descendants(w + "tc").Count());
        Assert.Equal("0012", Assert.Single(xml.Descendants(w + "t")).Value);
    }
}
