using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Tests.Fonts;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfParagraphEditorTests
{
    private static TrueTypeFont Font() => PdfStandardFontSubstitutes.OcrTrainingFonts()[0];

    [Fact]
    public void CandidateInspectionRetainsRotationRefusalAndSupportedCandidates()
    {
        PdfDocument ordinary = Raw("BT /F1 10 Tf 30 250 Td (OLD) Tj ET");
        var supported = PdfParagraphEditor.InspectCandidates(ordinary, 0);
        Assert.Single(supported.Candidates);
        Assert.Empty(supported.Refusals);
        PdfDocument rotated = Raw("BT /F1 10 Tf 30 250 Td (OLD) Tj ET", "/Rotate 90");
        byte[] before = rotated.Source.ToArray();
        var rejected = PdfParagraphEditor.InspectCandidates(rotated, 0);
        Assert.Empty(rejected.Candidates);
        Assert.All(rejected.Refusals, item => Assert.Equal(PdfParagraphRefusalCode.RotatedPage, item.RefusalCode));
        Assert.NotEmpty(rejected.Refusals);
        Assert.Equal(before, rotated.Source.ToArray());
    }

    [Fact]
    public void PreviewAndApplyMatchAndReplaceOnlyTheSelectedParagraph()
    {
        PdfDocument source = Document();
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0),
            item => item.Text.Contains("OLD", StringComparison.Ordinal));
        var target = new PdfContentBounds(30, 100, 90, 180);
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            target, "AAAA AAAA AAAA AAAA", Font(), 10, 15, PdfParagraphAlignment.Right);
        Assert.False(plan.HasOverflow);
        byte[] preview = plan.GetPreviewDocument();
        Assert.Equal(preview, PdfParagraphEditor.Apply(source, plan));
        PdfDocument changed = PdfDocument.Open(preview);
        PdfPageContent page = new PdfPageContentReader(changed).Read(0);
        Assert.DoesNotContain("OLD", page.Text, StringComparison.Ordinal);
        Assert.Contains("AAAA", page.Text, StringComparison.Ordinal);
        Assert.Equal("Other page", new PdfPageContentReader(changed).Read(1).Text);
        Assert.Equal(Unselected(source, candidate.SourceRegion), Unselected(changed, target));
        Assert.All(plan.Lines, line => Assert.Equal(target.Right, line.X + line.Width, 6));
        preview[0] = 0;
        Assert.Equal((byte)'%', plan.GetPreviewDocument()[0]);
    }

    [Fact]
    public void OverflowCannotBeAppliedAndPlanningDoesNotChangeTheSource()
    {
        PdfDocument source = Document();
        var candidate = PdfParagraphEditor.FindParagraphs(source, 0).First();
        var plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(10, 10, 11, 11), "AAAA", Font(), 10, 12, PdfParagraphAlignment.Left);
        Assert.True(plan.HasOverflow);
        Assert.Throws<InvalidOperationException>(() => PdfParagraphEditor.Apply(source, plan));
        Assert.Throws<InvalidOperationException>(() => plan.GetPreviewDocument());
        Assert.Contains("OLD", new PdfPageContentReader(source).Read(0).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyReplacementDeletesAndStalePlanIsRejected()
    {
        PdfDocument source = Document();
        var candidate = PdfParagraphEditor.FindParagraphs(source, 0).First();
        var plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            candidate.SourceRegion, "", Font(), 10, 12, PdfParagraphAlignment.Left);
        PdfDocument deleted = PdfDocument.Open(PdfParagraphEditor.Apply(source, plan));
        Assert.DoesNotContain("OLD", new PdfPageContentReader(deleted).Read(0).Text, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => PdfParagraphEditor.Apply(deleted, plan));
    }

    [Fact]
    public void RejectsCutLinesOutOfPageMissingGlyphsAndUnsupportedShaping()
    {
        PdfDocument source = Document();
        var candidate = PdfParagraphEditor.FindParagraphs(source, 0).First();
        var cut = candidate.SourceRegion with { Right = candidate.SourceRegion.Left + 4 };
        Assert.False(PdfParagraphEditor.Inspect(source, 0, cut).CanEdit);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfParagraphEditor.Plan(source, 0,
            candidate.SourceRegion, new PdfContentBounds(-1, 10, 40, 40), "A", Font(), 10, 12, PdfParagraphAlignment.Left));
        Assert.Throws<NotSupportedException>(() => PdfParagraphEditor.Plan(source, 0,
            candidate.SourceRegion, candidate.SourceRegion, "Z",
            TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false)), 10, 12, PdfParagraphAlignment.Left));
        Assert.Throws<NotSupportedException>(() => PdfParagraphEditor.Plan(source, 0,
            candidate.SourceRegion, candidate.SourceRegion, "\u0645", Font(), 10, 12, PdfParagraphAlignment.Left));
    }

    [Fact]
    public void PlansAndAppliesOrdinaryLatinTypographicPunctuation()
    {
        const string text = "It\u2019s \u2018quoted\u2019 and \u201Cclear\u201D\u2026 \u2022 \u20AC10 \u2122";
        TrueTypeFont font = Font();
        Assert.All(text.EnumerateRunes(), rune => Assert.NotEqual((ushort)0, font.GetGlyphId(rune.Value)));
        PdfDocument source = Document();
        var candidate = PdfParagraphEditor.FindParagraphs(source, 0).First();
        var plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(20, 100, 280, 220), text, font, 10, 12, PdfParagraphAlignment.Left);
        Assert.False(plan.HasOverflow);
        PdfDocument changed = PdfDocument.Open(PdfParagraphEditor.Apply(source, plan));
        Assert.Contains(text, new PdfPageContentReader(changed).Read(0).Text, StringComparison.Ordinal);
        Assert.DoesNotContain("OLD", new PdfPageContentReader(changed).Read(0).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfParagraphAlignment.Left)]
    [InlineData(PdfParagraphAlignment.Center)]
    [InlineData(PdfParagraphAlignment.Right)]
    public void UsesThePlannedAlignmentAndExplicitLeading(PdfParagraphAlignment alignment)
    {
        PdfDocument source = Document();
        var candidate = PdfParagraphEditor.FindParagraphs(source, 0).First();
        var target = new PdfContentBounds(20, 100, 180, 200);
        var plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion, target, "AA\nAAAA", Font(), 12, 19, alignment);
        Assert.False(plan.HasOverflow);
        Assert.Equal(2, plan.Lines.Count);
        Assert.Equal(19, plan.Lines[0].Baseline - plan.Lines[1].Baseline, 5);
        foreach (PdfParagraphLine line in plan.Lines)
        {
            double expected = alignment == PdfParagraphAlignment.Left ? target.Left
                : alignment == PdfParagraphAlignment.Right ? target.Right - line.Width
                : target.Left + (target.Width - line.Width) / 2;
            Assert.Equal(expected, line.X, 5);
        }
        PdfExtractedLetter[] letters = [.. new PdfPageContentReader(PdfDocument.Open(plan.GetPreviewDocument())).Read(0)
            .Letters.Where(letter => letter.Value == "A")];
        Assert.Equal(plan.Lines[0].X, letters[0].StartBaseLine.X, 5);
        Assert.Equal(plan.Lines[0].Baseline, letters[0].StartBaseLine.Y, 5);
        Assert.Equal(plan.Lines[1].X, letters[2].StartBaseLine.X, 5);
        Assert.Equal(plan.Lines[1].Baseline, letters[2].StartBaseLine.Y, 5);
        Assert.Contains("OLD", new PdfPageContentReader(source).Read(0).Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BT /F1 10 Tf 3 Tr 30 250 Td (OLD) Tj ET", "", "")]
    [InlineData("BT /F1 10 Tf 1 0.1 0 1 30 250 Tm (OLD) Tj ET", "", "")]
    [InlineData("BT /F1 10 Tf 30 250 Td (OLD) Tj 1 0 0 rg 0 -12 Td (OLD) Tj ET", "", "")]
    [InlineData("BT /F1 10 Tf 30 250 Td (OLD) Tj ET BT /F1 10 Tf 30 238 Td (OLD) Tj ET", "", "")]
    [InlineData("BT /F1 10 Tf 30 250 Td (OLD) Tj ET", "/Rotate 90", "")]
    [InlineData("BT /F1 10 Tf 30 250 Td (OLD) Tj ET", "", "/StructTreeRoot << /Type /StructTreeRoot /K [] >>")]
    [InlineData("BT /F1 10 Tf 30 250 Td (OLD) Tj ET", "", "/Perms << >>")]
    [InlineData("BT /F1 10 Tf 30 250 Td (OLD) Tj ET", "", "/AcroForm << /Fields [<< /FT /Sig /T (Signed) /V << /Type /Sig /ByteRange [0 1 2 3] /Contents <010203> >> >>] >>")]
    public void RefusesUnsafeSourceContent(string content, string pageExtra, string catalogExtra)
    {
        PdfDocument source = Raw(content, pageExtra, catalogExtra);
        Assert.False(PdfParagraphEditor.Inspect(source, 0, new PdfContentBounds(20, 220, 100, 280)).CanEdit);
    }

    [Fact]
    public void RejectsEncryptedAndRestrictedDocuments()
    {
        byte[] bytes = PdfDocumentEncryptionWriter.EncryptWithPasswords(Document(), new PdfPasswordEncryptionOptions
        {
            UserPassword = "reader", OwnerPassword = "owner", AllowDocumentModification = false
        });
        PdfDocument source = PdfDocument.Open(bytes, "reader");
        Assert.False(PdfParagraphEditor.Inspect(source, 0, new PdfContentBounds(20, 220, 100, 280)).CanEdit);
        Assert.Empty(PdfParagraphEditor.FindParagraphs(source, 0));
    }

    [Fact]
    public void HandlesCropCoordinatesAndSharedContentWithoutChangingTheOtherPage()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 130 250 Td (OLD) Tj ET", "/CropBox [100 100 300 300]", shared: true);
        var candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.InRange(candidate.SourceRegion.Left, 29, 31);
        var target = new PdfContentBounds(20, 50, 140, 140);
        var plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion, target, "AAAA", Font(), 12, 16, PdfParagraphAlignment.Left);
        PdfDocument changed = PdfDocument.Open(PdfParagraphEditor.Apply(source, plan));
        Assert.DoesNotContain("OLD", new PdfPageContentReader(changed).Read(0).Text, StringComparison.Ordinal);
        Assert.Equal("OLD", new PdfPageContentReader(changed).Read(1).Text);
        PdfExtractedLetter first = new PdfPageContentReader(changed).Read(0).Letters[0];
        Assert.Equal(target.Left, first.StartBaseLine.X, 5);
        Assert.Equal(plan.Lines[0].Baseline, first.StartBaseLine.Y, 5);
    }

    [Fact]
    public void PreservesSourceColorAndPageDrawings()
    {
        PdfDocument source = Raw("0.2 0.3 0.4 rg 10 10 20 20 re f BT /F1 10 Tf 30 250 Td (OLD) Tj ET 10 10 50 50 re S");
        var candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.Equal(new PdfRgbColor(0.2, 0.3, 0.4), candidate.Color);
        var plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(30, 100, 200, 200), "AAAA", Font(), 10, 12, PdfParagraphAlignment.Left);
        var before = new PdfPageContentReader(source).Read(0);
        var after = new PdfPageContentReader(PdfDocument.Open(plan.GetPreviewDocument())).Read(0);
        Assert.Equal(before.Paths.Count, after.Paths.Count);
        Assert.Equal(before.Paths.Select(path => path.BoundingBox), after.Paths.Select(path => path.BoundingBox));
        Assert.Single(after.Instructions, instruction => instruction.Operator == "rg");
    }

    private static PdfDocument Raw(string content, string pageExtra = "", string catalogExtra = "", bool shared = false)
    {
        string page = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] {pageExtra} /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>";
        string[] objects = [$"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>",
            $"<< /Type /Pages /Kids [3 0 R{(shared ? " 6 0 R" : "")}] /Count {(shared ? 2 : 1)} >>", page,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream", page];
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

    private static (string, double, double)[] Unselected(PdfDocument doc, PdfContentBounds area) =>
        new PdfPageContentReader(doc).Read(0).Letters.Where(letter =>
            (letter.BoundingBox.Left + letter.BoundingBox.Right) / 2 < area.Left
            || (letter.BoundingBox.Left + letter.BoundingBox.Right) / 2 > area.Right
            || (letter.BoundingBox.Bottom + letter.BoundingBox.Top) / 2 < area.Bottom
            || (letter.BoundingBox.Bottom + letter.BoundingBox.Top) / 2 > area.Top)
            .Select(letter => (letter.Value, Math.Round(letter.StartBaseLine.X, 5), Math.Round(letter.StartBaseLine.Y, 5))).ToArray();

    private static PdfDocument Document() => PdfDocument.Open(new PdfDocumentBuilder()
        .AddPage(300, 300, new PdfContentStreamBuilder()
            .BeginText().SetFont(PdfStandardFont.Helvetica, 10).MoveText(30, 250)
            .ShowLatin1Text("OLD line").MoveText(0, -12).ShowLatin1Text("OLD next").EndText()
            .BeginText().SetFont(PdfStandardFont.Helvetica, 10).MoveText(200, 30).ShowLatin1Text("Footer").EndText())
        .AddPage(300, 300, new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 10)
            .MoveText(30, 250).ShowLatin1Text("Other page").EndText()).Build());
}
