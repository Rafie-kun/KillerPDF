using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Tests.Fonts;
using System.Text;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfTextLineEditorTests
{
    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    [Fact]
    public void RewritesTheLineInItsOwnFontWithoutCoveringIt()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 12 Tf 0 0 0.5 rg 72 700 Td (Hello OLD world) Tj 0 -20 Td (Next line) Tj ET"));
        PdfContentBounds region = Region(document, "OLD");
        PdfExtractedLetter firstOld = Letter(document, "O");

        PdfTextLineEditResult result = PdfTextLineEditor.Replace(document, new PdfTextLineEdit(0, region, "NEWER"));

        Assert.Equal(PdfTextLineFont.Original, result.Font);
        Assert.Equal(3, result.RemovedGlyphs);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.True(output.CrossReferences.Sections.Count > 1, "the change is an incremental update");
        string letters = Letters(output);
        Assert.DoesNotContain("OLD", letters);
        Assert.Contains("NEWER", letters);
        // The new text starts where the old text started, and everything else keeps its place.
        PdfExtractedLetter firstNew = Letter(output, "N");
        Assert.Equal(firstOld.StartBaseLine.X, firstNew.StartBaseLine.X, 3);
        Assert.Equal(firstOld.StartBaseLine.Y, firstNew.StartBaseLine.Y, 3);
        Assert.Equal(12, firstNew.PointSize, 3);
        AssertOthersUnmoved(document, output, region, "NEWER");
        // Nothing is painted over the page: no rectangle, no path, no image.
        IReadOnlyList<PdfContentInstruction> content = new PdfPageContentReader(output).ReadInstructions(0);
        Assert.DoesNotContain(content, instruction => instruction.Operator is "re" or "f" or "S" or "Do");
        Assert.Single(content, instruction => instruction.Operator == "rg");
    }

    [Fact]
    public void ChangesSizeColorAndUnderlineOnlyForTheNewText()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 12 Tf 72 700 Td (Price OLD here) Tj 0 -20 Td (Next line) Tj ET"));
        PdfContentBounds region = Region(document, "OLD");

        PdfTextLineEditResult result = PdfTextLineEditor.Replace(document, new PdfTextLineEdit(0, region, "NEW")
        {
            FontSize = 24,
            FillColor = new PdfRgbColor(1, 0, 0),
            Underline = true
        });

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Equal(24, Letter(output, "N").PointSize, 3);
        Assert.Equal(12, Letter(output, "x").PointSize, 3);
        AssertOthersUnmoved(document, output, region, "NEW");
        IReadOnlyList<PdfContentInstruction> content = new PdfPageContentReader(output).ReadInstructions(0);
        int red = IndexOf(content, instruction => instruction.Operator == "rg" && Number(instruction.Operands[0]) == 1);
        int restored = IndexOf(content, instruction => instruction.Operator == "g" && Number(instruction.Operands[0]) == 0);
        int end = IndexOf(content, instruction => instruction.Operator == "ET");
        int stroke = IndexOf(content, instruction => instruction.Operator == "S");
        Assert.True(red >= 0 && restored > red && end > restored, "the color is set for the new text and put back after it");
        Assert.True(stroke > end, "the underline is drawn after the text object");
    }

    [Fact]
    public void BreaksLinesBelowTheFirstAndKeepsTheRestInPlace()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 10 Tf 72 700 Td (Address OLD) Tj ET BT /F1 10 Tf 72 600 Td (Footer) Tj ET"));
        PdfContentBounds region = Region(document, "OLD");

        PdfDocument output = PdfDocument.Open(PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, region, "One\nTwo")).Document);

        PdfExtractedLetter one = Letter(output, "O");
        PdfExtractedLetter two = Letter(output, "T");
        Assert.Equal(one.StartBaseLine.X, two.StartBaseLine.X, 3);
        Assert.Equal(one.StartBaseLine.Y - 12, two.StartBaseLine.Y, 3);
        AssertOthersUnmoved(document, output, region, "OneTwo");
    }

    [Fact]
    public void UsesTheSubstituteOnlyWhenTheEmbeddedFontLacksAGlyph()
    {
        PdfDocument document = PdfDocument.Open(Type3Page("BT /T3 12 Tf 72 700 Td (BB) Tj ET"));
        var area = new PdfContentBounds(60, 690, 120, 720);
        TrueTypeFont substitute = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false));

        PdfTextLineEditResult kept = PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, area, "B") { Substitute = substitute });
        PdfTextLineEditResult substituted = PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, area, "A") { Substitute = substitute });

        Assert.Equal(PdfTextLineFont.Original, kept.Font);
        Assert.Equal("B", Letters(PdfDocument.Open(kept.Document)));
        Assert.Equal(PdfTextLineFont.Substitute, substituted.Font);
        Assert.Equal("A", Letters(PdfDocument.Open(substituted.Document)));
        Assert.Throws<NotSupportedException>(() => PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, area, "A")));
    }

    [Fact]
    public void ChangesTextInsideAFormOnlyForThatPage()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm1 6 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm1 6 0 R >> >> /Contents 5 0 R >>",
            Stream("q 1 0 0 1 100 500 cm /Fm1 Do Q"),
            Stream("BT /F1 12 Tf 10 10 Td (Shared OLD text) Tj ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 400 100] /Resources << /Font << /F1 7 0 R >> >>"),
            Helvetica
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfContentBounds region = Region(document, "OLD");

        PdfDocument output = PdfDocument.Open(PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, region, "NEW")).Document);

        Assert.Contains("NEW", Letters(output));
        Assert.DoesNotContain("OLD", Letters(output));
        Assert.Contains("OLD", Letters(output, 1));
        AssertOthersUnmoved(document, output, region, "NEW");
    }

    [Fact]
    public void InspectionReportsWhatCannotBeRewritten()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 12 Tf 72 700 Td (Visible) Tj ET BT 3 Tr /F1 12 Tf 72 600 Td (Hidden) Tj ET"));

        PdfTextLineInspection visible = PdfTextLineEditor.Inspect(document, 0, Region(document, "Visible"));
        PdfTextLineInspection hidden = PdfTextLineEditor.Inspect(document, 0, Region(document, "Hidden"));
        PdfTextLineInspection empty = PdfTextLineEditor.Inspect(document, 0, new PdfContentBounds(300, 100, 400, 150));

        Assert.True(visible.CanEdit);
        Assert.Equal(7, visible.Glyphs);
        Assert.Equal("Helvetica", visible.FontName);
        Assert.Equal(12, visible.FontSize, 3);
        Assert.Equal(PdfTextLineBlocker.InvisibleText, hidden.Blocker);
        Assert.Equal(PdfTextLineBlocker.NoText, empty.Blocker);
        Assert.Throws<NotSupportedException>(() => PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, Region(document, "Hidden"), "Shown")));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfTextLineEditor.Replace(document,
            new PdfTextLineEdit(0, Region(document, "Visible"), "x") { FontSize = 0 }));
    }

    private static void AssertOthersUnmoved(PdfDocument before, PdfDocument after, PdfContentBounds region, string inserted)
    {
        var expected = new PdfPageContentReader(before).Read(0).Letters
            .Where(letter => !CenterInside(letter.BoundingBox, region))
            .Select(letter => (letter.Value, Math.Round(letter.StartBaseLine.X, 3), Math.Round(letter.StartBaseLine.Y, 3)))
            .ToList();
        var actual = new PdfPageContentReader(after).Read(0).Letters
            .Select(letter => (letter.Value, Math.Round(letter.StartBaseLine.X, 3), Math.Round(letter.StartBaseLine.Y, 3)))
            .ToList();
        // The new letters are the only additions.
        foreach (char character in inserted)
        {
            int index = actual.FindIndex(item => item.Value == character.ToString()
                && !expected.Contains(item));
            Assert.True(index >= 0, $"'{character}' was not written.");
            actual.RemoveAt(index);
        }
        Assert.Equal(expected, actual);
    }

    private static bool CenterInside(PdfContentBounds box, PdfContentBounds region)
    {
        double x = (box.Left + box.Right) / 2, y = (box.Bottom + box.Top) / 2;
        return x >= region.Left && x <= region.Right && y >= region.Bottom && y <= region.Top;
    }

    private static PdfContentBounds Region(PdfDocument document, string word)
    {
        IReadOnlyList<PdfExtractedLetter> letters = new PdfPageContentReader(document).Read(0).Letters;
        string text = string.Concat(letters.Select(letter => letter.Value));
        int start = text.IndexOf(word, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{word}' was not found.");
        PdfExtractedLetter[] selected = [.. letters.Skip(start).Take(word.Length)];
        return new PdfContentBounds(
            selected.Min(letter => letter.BoundingBox.Left), selected.Min(letter => letter.BoundingBox.Bottom),
            selected.Max(letter => letter.BoundingBox.Right), selected.Max(letter => letter.BoundingBox.Top));
    }

    private static PdfExtractedLetter Letter(PdfDocument document, string value, int occurrence = 0) =>
        new PdfPageContentReader(document).Read(0).Letters.Where(letter => letter.Value == value).ElementAt(occurrence);

    private static string Letters(PdfDocument document, int pageIndex = 0) =>
        string.Concat(new PdfPageContentReader(document).Read(pageIndex).Letters.Select(letter => letter.Value));

    private static int IndexOf(IReadOnlyList<PdfContentInstruction> content, Func<PdfContentInstruction, bool> match)
    {
        for (int index = 0; index < content.Count; index++)
            if (match(content[index])) return index;
        return -1;
    }

    private static double Number(PdfObject value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => double.NaN
    };

    private static byte[] SinglePage(string content) =>
        CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream(content)
        ]);

    // A Type 3 font is embedded and holds only the glyph it defines, a B.
    private static byte[] Type3Page(string content) =>
        CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /T3 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 600 700] /FontMatrix [0.001 0 0 0.001 0 0] " +
                "/CharProcs << /B 6 0 R >> /Encoding << /Type /Encoding /Differences [66 /B] >> " +
                "/FirstChar 66 /LastChar 66 /Widths [600] /Resources << >> >>",
            Stream(content),
            Stream("600 0 0 0 600 700 d1 0 0 600 700 re f")
        ]);

    private static string Stream(string content, string entries = "") =>
        $"<< {entries} /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static byte[] CreatePdf(List<string> objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\n").Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
