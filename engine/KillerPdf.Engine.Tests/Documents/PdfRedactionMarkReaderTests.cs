using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfRedactionMarkReaderTests
{
    [Fact]
    public void ReadReturnsEachQuadOfSavedMarksWithOverlayAndFill()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().AddBlankPage().Build();
        byte[] marked = new PdfIncrementalAnnotationEditor(PdfDocument.Open(source))
            .AddTextNote(0, 10, 20, "Not a redaction")
            .AddRedactionMark(1, [Quad(10, 20, 80, 14), Quad(10, 40, 50, 14)], "Account",
                fillColor: new PdfRgbColor(0, 0, 1), overlayText: "REDACTED")
            .Build();

        PdfRedactionMarkInfo mark = Assert.Single(PdfRedactionMarkReader.Read(PdfDocument.Open(marked)));

        Assert.Equal(1, mark.PageIndex);
        Assert.Equal(0, mark.AnnotationIndex);
        Assert.Equal(2, mark.Areas.Count);
        Assert.Equal(new PdfContentBounds(10, 20, 90, 34), mark.Areas[0]);
        Assert.Equal(new PdfContentBounds(10, 40, 60, 54), mark.Areas[1]);
        Assert.Equal("REDACTED", mark.OverlayText);
        Assert.Equal("Account", mark.Contents);
        Assert.Equal(new PdfRgbColor(0, 0, 1), mark.FillColor);
    }

    [Fact]
    public void ReadReturnsNothingForADocumentWithoutMarks()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();

        Assert.Empty(PdfRedactionMarkReader.Read(PdfDocument.Open(source)));
    }

    private static PdfTextQuad Quad(double x, double y, double width, double height) =>
        new(new PdfPoint(x, y + height), new PdfPoint(x + width, y + height),
            new PdfPoint(x, y), new PdfPoint(x + width, y));
}
