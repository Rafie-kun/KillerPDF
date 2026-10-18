using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using KillerPdf.Engine.Rendering;
using System.Text;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfResizeMacroTests
{
    [Fact]
    public void ResizeDoesNotMistakeAFontResourceNamedRvForRichText()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(300, 400,
            new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 16)
                .MoveText(20, 300).ShowLatin1Text("Selectable text").EndText()).Build());
        PdfObject Resolve(PdfObject value) => value is PdfIndirectReference reference ? document.Resolve(reference) : value;
        var catalog = (PdfDictionary)Resolve(document.Trailer[new PdfName("Root"u8)]);
        var pages = (PdfDictionary)Resolve(catalog[new PdfName("Pages"u8)]);
        var pageReference = (PdfIndirectReference)((PdfArray)Resolve(pages[new PdfName("Kids"u8)]))[0];
        var page = ((PdfDictionary)Resolve(pageReference)).ToDictionary(entry => entry.Key, entry => entry.Value);
        var resources = ((PdfDictionary)Resolve(page[new PdfName("Resources"u8)])).ToDictionary(entry => entry.Key, entry => entry.Value);
        var fonts = ((PdfDictionary)Resolve(resources[new PdfName("Font"u8)])).ToDictionary(entry => entry.Key, entry => entry.Value);
        fonts[new PdfName("RV"u8)] = fonts.Values.First();
        resources[new PdfName("Font"u8)] = new PdfDictionary(fonts);
        page[new PdfName("Resources"u8)] = new PdfDictionary(resources);
        var catalogEntries = catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
        catalogEntries[new PdfName("DA"u8)] = new PdfString("Unrelated (metadata"u8, PdfStringForm.Literal);
        var root = (PdfIndirectReference)document.Trailer[new PdfName("Root"u8)];
        byte[] source = new PdfIncrementalUpdateBuilder(document).ReplaceObject(pageReference.ObjectNumber,
            new PdfDictionary(page)).ReplaceObject(root.ObjectNumber, new PdfDictionary(catalogEntries)).Build();

        var result = PdfResizeMacro.Execute(PdfResizeMacro.ScaleStep(50), source, new Dictionary<string, string>());

        Assert.Equal(150, Assert.Single(PdfPageInformation.Read(PdfDocument.Open(result.Data))).Width);
    }

    [Fact]
    public void ProportionalResizePreservesTheRenderedPageIncludingFormsAndInk()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(300, 400,
            new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 16)
                .MoveText(20, 300).ShowLatin1Text("Selectable text").EndText())
            .AddTextField(0, "Name", 20, 20, 120, 24, "Ada")
            .AddInkAnnotation(0, new PdfPoint[] { new(20, 100), new(40, 120), new(80, 100) })
            .Build();
        var result = PdfResizeMacro.Execute(PdfResizeMacro.ScaleStep(50), source,
            new Dictionary<string, string>());
        var options = new PdfRenderOptions(300, 400);

        var original = new PdfPageRenderer(PdfDocument.Open(source)).Render(0, options);
        var resized = new PdfPageRenderer(PdfDocument.Open(result.Data)).Render(0, options);

        Assert.True(original.Pixels.Span.SequenceEqual(resized.Pixels.Span));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ResizeRejectsFormLayoutsWhoseXmlCoordinatesCannotBePreserved(bool xfa)
    {
        byte[] source;
        if (xfa)
        {
            PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(300, 400).Build());
            var root = Assert.IsType<PdfIndirectReference>(document.Trailer[new PdfName("Root"u8)]);
            var catalog = Assert.IsType<PdfDictionary>(document.Resolve(root)).ToDictionary(entry => entry.Key, entry => entry.Value);
            var update = new PdfIncrementalUpdateBuilder(document);
            var packet = update.AddObject(new PdfStream(new PdfDictionary([]), "<xdp/>"u8));
            catalog[new PdfName("AcroForm"u8)] = update.AddObject(new PdfDictionary([
                new(new PdfName("Fields"u8), new PdfArray([])), new(new PdfName("XFA"u8), packet)]));
            update.ReplaceObject(root.ObjectNumber, new PdfDictionary(catalog));
            source = update.Build();
        }
        else source = new PdfDocumentBuilder().AddBlankPage(300, 400)
            .AddTextField(0, "Name", 20, 20, 120, 24, "Ada",
                richTextValue: "<body xmlns=\"http://www.w3.org/1999/xhtml\"><p style=\"font-size:12pt\">Ada</p></body>").Build();

        Assert.Throws<NotSupportedException>(() => PdfResizeMacro.Execute(PdfResizeMacro.ScaleStep(50), source,
            new Dictionary<string, string>()));
    }

    [Fact]
    public void ResizeScalesEditableDimensionLineEndpointsAndStrokeWidth()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage(300, 400)
            .AddLineAnnotation(0, new PdfPoint(20, 40), new PdfPoint(100, 200),
                lineWidth: 4, intent: PdfLineAnnotationIntent.Dimension).Build();

        var result = PdfResizeMacro.Execute(PdfResizeMacro.ScaleStep(50), source,
            new Dictionary<string, string>());
        var annotation = Assert.Single(PdfMeasurementAnnotationReader.Read(PdfDocument.Open(result.Data)));

        Assert.Equal(new[] { new PdfMeasurementPoint(10, 20), new PdfMeasurementPoint(50, 100) }, annotation.Points);
        Assert.Equal(2, annotation.LineWidth);
    }

    [Fact]
    public void ResizeMovesBookmarkAndNamedDestinationCoordinatesWithThePage()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage(300, 400)
            .AddNamedDestination("section", 0, PdfDestination.At(100, 300, 1.5))
            .AddNamedDestinationBookmark("Named section", "section")
            .AddBookmark("Rectangle", 0, options: new PdfBookmarkOptions
                { Destination = PdfDestination.FitRectangle(20, 40, 100, 200), Style = PdfBookmarkStyle.Bold })
            .Build();

        var result = PdfResizeMacro.Execute(PdfResizeMacro.ScaleStep(50), source,
            new Dictionary<string, string>());
        var bookmarks = PdfBookmarkReader.Read(PdfDocument.Open(result.Data));

        Assert.Equal(new double?[] { 50, 150, 1.5 }, bookmarks[0].Destination!.Values);
        Assert.Equal(new double?[] { 10, 20, 50, 100 }, bookmarks[1].Destination!.Values);
        Assert.Equal(PdfBookmarkStyle.Bold, bookmarks[1].Style);
    }

    [Fact]
    public void ResizedFormUsesTheScaledFontWhenItsValueIsEdited()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage(300, 400)
            .AddTextField(0, "Name", 20, 20, 120, 24, "Ada", fontSize: 12).Build();
        var resized = PdfResizeMacro.Execute(PdfResizeMacro.ScaleStep(50), source,
            new Dictionary<string, string>());
        PdfDocument edited = PdfDocument.Open(new PdfIncrementalPageEditor(PdfDocument.Open(resized.Data))
            .SetTextFieldValue("Name", "Grace").Build());
        PdfFormWidgetInfo widget = Assert.Single(PdfFormWidgetReader.ReadPage(edited, 0));
        PdfObject fontSize = Assert.Single(PdfContentStreamReader.Read(Encoding.ASCII.GetBytes(widget.DefaultAppearance)),
            instruction => instruction.Operator == "Tf").Operands[1];

        Assert.Equal(6d, fontSize switch { PdfInteger integer => integer.Value, PdfReal real => real.Value, _ => double.NaN });
        Assert.Equal(10, widget.Left);
        Assert.Equal(70, widget.Right);
        Assert.Equal(22, widget.Top);
    }

    [Fact]
    public void ResizePreservesTextAndAnEditableFormAtTheNewPageSize()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(300, 400,
            new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 16)
                .MoveText(20, 300).ShowLatin1Text("Selectable text").EndText())
            .AddTextField(0, "Name", 20, 20, 120, 24, "Ada").Build();

        var result = PdfResizeMacro.Execute(PdfResizeMacro.ScaleStep(50), source,
            new Dictionary<string, string>(), CancellationToken.None);
        PdfDocument resized = PdfDocument.Open(result.Data);

        Assert.Equal(150, Assert.Single(PdfPageInformation.Read(resized)).Width);
        Assert.Equal(200, Assert.Single(PdfPageInformation.Read(resized)).Height);
        Assert.Equal("Selectable text", string.Concat(new PdfPageContentReader(resized).Read(0).TextRuns.Select(run => run.Text)));
        Assert.Empty(new PdfPageContentReader(resized).Read(0).Images);
        Assert.Equal("Ada", Assert.Single(PdfFormWidgetReader.ReadPage(resized, 0)).Value);
        PdfDocument edited = PdfDocument.Open(new PdfIncrementalPageEditor(resized).SetTextFieldValue("Name", "Grace").Build());
        Assert.Equal("Grace", Assert.Single(PdfFormWidgetReader.ReadPage(edited, 0)).Value);
        Assert.Equal(300, Assert.Single(PdfPageInformation.Read(PdfDocument.Open(source))).Width);
    }
}
