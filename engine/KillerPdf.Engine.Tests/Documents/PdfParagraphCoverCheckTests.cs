using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using System.Text;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

/// <summary>
/// A paragraph edit refuses when something painted after the paragraph would lie over the new text,
/// since the page would then show that content instead.
/// </summary>
public sealed class PdfParagraphCoverCheckTests
{
    private const string Paragraph = "BT /F1 10 Tf 30 250 Td (Old words sit here today) Tj ET\n";
    // The new text goes below the source, so what is painted over it is no part of the source.
    private static readonly PdfContentBounds Target = new(20, 130, 260, 152);

    private static TrueTypeFont Font() => PdfStandardFontSubstitutes.OcrTrainingFonts()[0];

    private static PdfParagraphPlan Plan(string later)
    {
        PdfDocument document = Page(Paragraph + later);
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(document, 0),
            item => item.Text.Contains("Old words", StringComparison.Ordinal));
        return PdfParagraphEditor.Plan(document, 0, candidate.SourceRegion, Target, "Some new words sit there today",
            Font(), 10, 12, PdfParagraphAlignment.Left);
    }

    private static void AssertRefused(string later)
    {
        var error = Assert.Throws<NotSupportedException>(() => Plan(later));
        Assert.Contains("would cover the new box", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    // Closed by a segment, which has always been seen.
    [InlineData("1 G 14 w 10 144 m 10 180 l 250 180 l 250 144 l h S")]
    // Closed by the paint operator, the same painted result.
    [InlineData("1 G 14 w 10 144 m 10 180 l 250 180 l 250 144 l s")]
    [InlineData("1 G 0.5 g 14 w 10 156 m 10 180 l 250 180 l 250 156 l b")]
    [InlineData("1 G 0.5 g 14 w 10 156 m 10 180 l 250 180 l 250 156 l b*")]
    public void AStrokeAlongTheClosingEdgeIsRefused(string later) => AssertRefused(later);

    [Theory]
    [InlineData("1 G 14 w 10 144 m 10 180 l 250 180 l 250 144 l S")]
    [InlineData("1 G 0.5 g 14 w 10 170 m 10 180 l 250 180 l 250 170 l b")]
    [InlineData("1 G 14 w 300 100 200 100 re s")]
    public void AStrokeThatStaysClearIsAccepted(string later) => Assert.NotNull(Plan(later));

    [Theory]
    [InlineData("BT 1 g /F2 60 Tf 30 120 Td (OO) Tj ET")]
    [InlineData("BT 0 g /F1 10 Tf 40 140 Td (Dark text after it) Tj ET")]
    [InlineData("q 30 130 200 30 re W n BT 1 g /F2 60 Tf 30 120 Td (OO) Tj ET Q")]
    [InlineData("BT 0.5 g 1 Tr /F2 60 Tf 30 120 Td (OO) Tj ET")]
    [InlineData("BT 0.5 g 4 Tr /F2 60 Tf 30 120 Td (OO) Tj ET")]
    public void LaterTextOverTheNewTextIsRefused(string later) => AssertRefused(later);

    [Theory]
    // Invisible text paints nothing, as over a scan.
    [InlineData("BT 3 Tr 0 g /F1 10 Tf 40 140 Td (Hidden layer) Tj ET")]
    [InlineData("BT 7 Tr 0 g /F1 10 Tf 40 140 Td (Hidden clip) Tj ET")]
    // Clear of the new text.
    [InlineData("BT 0 g /F1 10 Tf 30 50 Td (Far below) Tj ET")]
    [InlineData("BT 0 g /F1 10 Tf 400 140 Td (Beside it) Tj ET")]
    // A word space paints nothing.
    [InlineData("BT 0 g /F1 10 Tf 40 140 Td (       ) Tj ET")]
    // Clipped away before it reaches the new text.
    [InlineData("q 0 0 300 50 re W n BT 0 g /F1 10 Tf 40 140 Td (Clipped away) Tj ET Q")]
    public void LaterTextThatPaintsNothingThereIsAccepted(string later) => Assert.NotNull(Plan(later));

    [Fact]
    public void TextPaintedBeforeTheParagraphIsNotACover()
    {
        PdfDocument document = Page("BT 0.8 g /F2 60 Tf 30 120 Td (OO) Tj ET\n0 g\n" + Paragraph);
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(document, 0),
            item => item.Text.Contains("Old words", StringComparison.Ordinal));

        Assert.NotNull(PdfParagraphEditor.Plan(document, 0, candidate.SourceRegion, Target,
            "Some new words sit there today", Font(), 10, 12, PdfParagraphAlignment.Left));
    }

    private static PdfDocument Page(string content)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 400] /Resources << /Font << /F1 4 0 R /F2 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >> >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }
}
