using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfParagraphEditorTests
{
    private static TrueTypeFont Font() => PdfStandardFontSubstitutes.OcrTrainingFonts()[0];

    private const string StructureRoot = "/StructTreeRoot << /Type /StructTreeRoot /K [] >>";

    [Fact]
    public void EditsATaggedParagraphAndKeepsItsTextInTheSameElement()
    {
        PdfDocument source = Raw(
            "/P <</MCID 0>> BDC q 0 0 300 300 re W* n BT /F1 10 Tf 1 0 0 1 30 250 Tm (OLD line) Tj ET Q " +
            "q 0 0 300 300 re W* n BT /F1 10 Tf 1 0 0 1 30 238 Tm (OLD next) Tj ET Q EMC " +
            "/P <</MCID 1>> BDC q 0 0 300 300 re W* n BT /F1 10 Tf 1 0 0 1 30 200 Tm (Second) Tj ET Q EMC",
            catalogExtra: StructureRoot);
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0),
            item => item.Text.Contains("OLD", StringComparison.Ordinal));
        var plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(30, 215, 250, 262), "AAAA", Font(), 10, 12, PdfParagraphAlignment.Left);
        PdfDocument changed = PdfDocument.Open(PdfParagraphEditor.Apply(source, plan));
        Assert.Contains(PdfPageTree.Read(changed).Catalog.Keys, key => key.ValueAsLatin1() == "StructTreeRoot");
        PdfPageContent page = new PdfPageContentReader(changed).Read(0);
        Assert.DoesNotContain("OLD", page.Text, StringComparison.Ordinal);
        Assert.All(page.Letters.Where(letter => letter.Value == "A"), letter => Assert.Equal(0, letter.MarkedContentId));
        Assert.All(page.Letters.Where(letter => letter.Value == "S"), letter => Assert.Equal(1, letter.MarkedContentId));
    }

    // Assistive technology reads these instead of the page text, so an edit would leave them stale.
    [Theory]
    [InlineData("<< /S /P /K 0 /ActualText (OLD) >>", false)]
    [InlineData("<< /S /P /K 0 /Alt (OLD) >>", false)]
    [InlineData("<< /S /P /K [<< /Type /MCR /MCID 0 >>] /E (OLD) >>", false)]
    [InlineData("<< /S /Div /Alt (OLD) /K [<< /S /P /K 0 >>] >>", false)]
    [InlineData("<< /S /P /K 0 >>", true)]
    [InlineData("<< /S /Div /K [<< /S /P /K 0 >> << /S /P /K 1 /ActualText (OTHER) >>] >>", true)]
    public void ATaggedParagraphWithReplacementTextOnItsElementIsRefused(string element, bool editable)
    {
        PdfDocument source = Raw("/P <</MCID 0>> BDC BT /F1 10 Tf 1 0 0 1 30 250 Tm (OLD line) Tj ET EMC",
            catalogExtra: $"/StructTreeRoot << /Type /StructTreeRoot /K {element} >>");
        Assert.Equal(editable, PdfParagraphEditor.FindParagraphs(source, 0).Count == 1);
    }

    [Fact]
    public void LinesInDifferentTaggedElementsStaySeparateParagraphs()
    {
        PdfDocument source = Raw(
            "/P <</MCID 0>> BDC BT /F1 10 Tf 1 0 0 1 30 250 Tm (One) Tj ET EMC " +
            "/P <</MCID 1>> BDC BT /F1 10 Tf 1 0 0 1 30 238 Tm (Two) Tj ET EMC", catalogExtra: StructureRoot);
        Assert.Equal(["One", "Two"], PdfParagraphEditor.FindParagraphs(source, 0).Select(item => item.Text));
        Assert.False(PdfParagraphEditor.Inspect(source, 0, new PdfContentBounds(20, 220, 100, 280)).CanEdit);
    }

    [Fact]
    public void ADocumentWideRefusalIsReportedOnceRatherThanForEveryParagraph()
    {
        PdfDocument rotated = Raw("BT /F1 10 Tf 30 250 Td (One) Tj ET BT /F1 10 Tf 30 100 Td (Two) Tj ET", "/Rotate 90");
        Assert.Single(PdfParagraphEditor.InspectCandidates(rotated, 0).Refusals);
    }

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
    [InlineData("BT /F1 10 Tf 30 250 Td (OLD) Tj ET 1 0 0 rg BT /F1 10 Tf 30 238 Td (OLD) Tj ET", "", "")]
    [InlineData("BT /F1 10 Tf 30 250 Td (OLD) Tj ET", "/Rotate 90", "")]
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

    // Word writes every run as q, a page-sized clip and its own text object, under pagination artifacts.
    private const string WordShapedLines =
        "/Artifact <</Type /Pagination>> BDC EMC " +
        "q 0 0 300 300 re W* n BT /F1 10 Tf 1 0 0 1 30 250 Tm (OLD line) Tj ET Q " +
        "q 0 0 300 300 re W* n BT /F1 10 Tf 1 0 0 1 30 238 Tm (OLD next) Tj ET Q " +
        "q 0 0 300 300 re W* n BT /F1 10 Tf 1 0 0 1 200 30 Tm (Footer) Tj ET Q";

    [Fact]
    public void EditsAParagraphWrittenLineByLineTheWayWordExportsIt()
    {
        PdfDocument source = Raw(WordShapedLines);
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0),
            item => item.Text.Contains("OLD", StringComparison.Ordinal));
        Assert.Equal("OLD line OLD next", candidate.Text);
        var plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(30, 200, 200, 262), "AAAA AAAA", Font(), 10, 12, PdfParagraphAlignment.Left);
        PdfPageContent page = new PdfPageContentReader(PdfDocument.Open(PdfParagraphEditor.Apply(source, plan))).Read(0);
        Assert.DoesNotContain("OLD", page.Text, StringComparison.Ordinal);
        Assert.Contains("AAAA AAAA", page.Text, StringComparison.Ordinal);
        Assert.Contains("Footer", page.Text, StringComparison.Ordinal);
    }

    // Extraction reports hidden text as present, so only rendered pixels can catch this.
    [Theory]
    [InlineData("q 1 1 0 rg 20 234 200 13 re f Q")]
    [InlineData("q 0.9 0.9 0.9 rg 0 0 300 245 re f Q")]
    public void AReplacementIsNotPaintedUnderContentBetweenTheSourceLines(string between)
    {
        PdfDocument source = Raw($"BT /F1 10 Tf 1 0 0 1 30 250 Tm (OLD line) Tj ET {between} BT /F1 10 Tf 1 0 0 1 30 238 Tm (OLD next) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        var plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(30, 200, 200, 262), "AAAA AAAA\nAAAA AAAA\nAAAA AAAA", Font(), 10, 12, PdfParagraphAlignment.Left);
        var page = new PdfPageRenderer(PdfDocument.Open(PdfParagraphEditor.Apply(source, plan)))
            .Render(0, new PdfRenderOptions(300, 300));
        byte[] pixels = page.Pixels.ToArray();
        foreach (PdfParagraphLine line in plan.Lines)
        {
            int dark = 0;
            for (int y = 300 - (int)line.Baseline - 8; y < 300 - (int)line.Baseline; y++)
                for (int x = 30; x < 120; x++)
                    if (pixels[(y * page.Width + x) * 4 + 1] < 90) dark++;
            Assert.True(dark > 20, $"Line at {line.Baseline} has {dark} dark pixels.");
        }
    }

    // The replacement paints at the last source glyph, so a box dragged over content painted after
    // it would be hidden. Extraction still reports the text, so the plan has to refuse instead.
    [Theory]
    [InlineData("q 1 1 0 rg 20 234 200 13 re f Q", "")]
    [InlineData("q 150 0 0 30 40 230 cm /Fm1 Do Q", "/XObject << /Fm1 7 0 R >>")]
    [InlineData("q 0 0 300 300 re W n 0.9 g 0 0 300 300 re f Q", "")]
    public void RefusesABoxThatContentPaintedAfterTheParagraphWouldCover(string after, string resources)
    {
        PdfDocument source = Raw($"BT /F1 10 Tf 1 0 0 1 30 250 Tm (OLD line) Tj ET BT /F1 10 Tf 1 0 0 1 30 238 Tm (OLD next) Tj ET {after}",
            resources: resources, extraObject: LogoForm);
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        NotSupportedException refusal = Assert.Throws<NotSupportedException>(() => PdfParagraphEditor.Plan(source, 0,
            candidate.SourceRegion, new PdfContentBounds(30, 200, 200, 262), "AAAA AAAA", Font(), 10, 12,
            PdfParagraphAlignment.Left));
        Assert.Contains("painted after", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptsABoxClearOfContentPaintedAfterTheParagraph()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 1 0 0 1 30 250 Tm (OLD line) Tj ET BT /F1 10 Tf 1 0 0 1 30 238 Tm (OLD next) Tj ET " +
            "q 1 1 0 rg 20 20 200 13 re f Q 0 G 20 100 m 280 100 l S");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(30, 200, 200, 262), "AAAA AAAA", Font(), 10, 12, PdfParagraphAlignment.Left);
        Assert.False(plan.HasOverflow);
        Assert.Contains("AAAA", new PdfPageContentReader(PdfDocument.Open(plan.GetPreviewDocument())).Read(0).Text,
            StringComparison.Ordinal);
    }

    // The clip is only a bounding box in the reader, so these cut the replacement differently than the source.
    [Theory]
    [InlineData("q 20 20 m 280 20 l 20 280 l h W n BT /F1 10 Tf 30 150 Td (OLD) Tj ET Q")]
    [InlineData("q 20 240 30 30 re 150 240 30 30 re W n BT /F1 10 Tf 30 250 Td (OLD) Tj ET Q")]
    [InlineData("q 20 240 m 200 240 l 200 270 l 20 270 l 20 250 200 250 20 245 c h W n BT /F1 10 Tf 30 250 Td (OLD) Tj ET Q")]
    [InlineData("BT /F1 200 Tf 7 Tr 20 20 Td (M) Tj ET BT /F1 10 Tf 0 Tr 30 250 Td (OLD) Tj ET")]
    public void RefusesAParagraphWhoseClipIsNotOneRectangle(string content)
    {
        PdfDocument source = Raw(content);
        PdfParagraphInspection inspection = PdfParagraphEditor.Inspect(source, 0, new PdfContentBounds(20, 100, 150, 280));
        Assert.False(inspection.CanEdit);
        Assert.DoesNotContain(PdfParagraphEditor.FindParagraphs(source, 0), item => item.Text == "OLD");
    }

    [Theory]
    [InlineData("q 20 246 200 15 re W n BT /F1 10 Tf 30 250 Td (OLD) Tj ET Q")]
    [InlineData("q 20 246 m 220 246 l 220 261 l 20 261 l h W n BT /F1 10 Tf 30 250 Td (OLD) Tj ET Q")]
    [InlineData("q 20 246 m 220 246 l 220 261 l 20 261 l 20 246 l W n BT /F1 10 Tf 30 250 Td (OLD) Tj ET Q")]
    [InlineData("q 1 0 0 1 10 10 cm 10 236 200 25 re W* n BT /F1 10 Tf 1 0 0 1 20 240 Tm (OLD) Tj ET Q")]
    public void StillEditsAParagraphUnderOneRectangularClip(string content)
    {
        PdfDocument source = Raw(content);
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.Equal("OLD", candidate.Text);
        Assert.NotNull(candidate.Clip);
    }

    // The dialog's default box adds room above and below the ink, which a tight clip does not hold.
    [Fact]
    public void ACandidateKnowsItsClipSoADefaultBoxCanStayInsideIt()
    {
        PdfDocument source = Raw("q 20 246 200 15 re W n BT /F1 10 Tf 30 250 Td (OLD text) Tj ET Q");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        var padded = new PdfContentBounds(candidate.SourceRegion.Left, candidate.SourceRegion.Bottom - 2,
            candidate.SourceRegion.Right, candidate.SourceRegion.Top + 2);
        Assert.Throws<NotSupportedException>(() => PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion, padded,
            "AAAA", Font(), 10, 12, PdfParagraphAlignment.Left));
        PdfContentBounds fitted = candidate.FitToClip(padded);
        Assert.True(fitted.Bottom >= 246 - 0.01 && fitted.Top <= 261 + 0.01);
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion, fitted, "AAAA", Font(), 10, 12,
            PdfParagraphAlignment.Left);
        Assert.False(plan.HasOverflow);
    }

    // The dialog needs to tell a refused paragraph from empty page area when someone clicks on it.
    [Fact]
    public void ARefusalSaysWhereItsParagraphIs()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (Fine) Tj ET " +
            "q 20 20 m 280 20 l 20 80 l h W n BT /F1 10 Tf 0 Tr 30 40 Td (Cut) Tj ET Q");
        PdfParagraphCandidateInspection inspection = PdfParagraphEditor.InspectCandidates(source, 0);
        Assert.Equal(["Fine"], inspection.Candidates.Select(item => item.Text));
        PdfParagraphInspection refusal = Assert.Single(inspection.Refusals);
        Assert.NotNull(refusal.Region);
        Assert.True(refusal.Region.Value.Bottom < 45 && refusal.Region.Value.Top > 45);
        Assert.Contains("clip", refusal.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // A citation or bold word must not make a whole paragraph uneditable. One box holds one style,
    // so the replacement takes the paragraph's dominant style and the candidate says it is mixed.
    [Fact]
    public void AParagraphMixingStylesTakesItsDominantStyle()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 1 0 0 1 30 250 Tm (Old body text) Tj /F1 6 Tf 3 Ts (12) Tj /F1 10 Tf 0 Ts ( more) Tj " +
            "/F2 10 Tf ( bold) Tj ET BT /F1 10 Tf 1 0 0 1 30 238 Tm (Second line of body) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.True(candidate.MixedStyles);
        Assert.Equal(10, candidate.FontSize);
        Assert.Contains("Helvetica", candidate.FontName, StringComparison.Ordinal);
        Assert.DoesNotContain("Bold", candidate.FontName, StringComparison.Ordinal);
        var plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(30, 200, 280, 262), "AAAA", Font(), candidate.FontSize, 12, PdfParagraphAlignment.Left);
        PdfPageContent page = new PdfPageContentReader(PdfDocument.Open(PdfParagraphEditor.Apply(source, plan))).Read(0);
        Assert.DoesNotContain("body", page.Text, StringComparison.Ordinal);
        Assert.All(page.Letters.Where(letter => letter.Value == "A"), letter =>
        {
            Assert.Equal(10, letter.PointSize, 1);
            Assert.Equal(plan.Lines[0].Baseline, letter.StartBaseLine.Y, 1);
        });
    }

    [Fact]
    public void AHeadingInAnotherStyleStaysItsOwnParagraph()
    {
        PdfDocument source = Raw("BT /F2 10 Tf 1 0 0 1 30 250 Tm (Heading line) Tj ET BT /F1 10 Tf 1 0 0 1 30 238 Tm (Body line) Tj ET");
        Assert.Equal(["Heading line", "Body line"], PdfParagraphEditor.FindParagraphs(source, 0).Select(item => item.Text));
    }

    // Ghostscript scales by 0.12 then 8.333 with a 0.999 text matrix, and browsers flip the page.
    [Theory]
    [InlineData("q 0.12 0 0 0.12 0 0 cm q 8.333 0 0 8.333 0 0 cm BT /F1 10 Tf 0.999 0 0 1 30 250 Tm (OLD line) Tj 0.999 0 0 1 30 238 Tm (OLD next) Tj ET Q Q")]
    [InlineData("q 1 0 0 -1 0 300 cm BT /F1 10 Tf 1 0 0 -1 30 50 Tm (OLD line) Tj 1 0 0 -1 30 62 Tm (OLD next) Tj ET Q")]
    [InlineData("q 0.5 0 0 0.5 0 0 cm BT /F1 20 Tf 1 0 0 1 60 500 Tm (OLD line) Tj 1 0 0 1 60 476 Tm (OLD next) Tj ET Q")]
    public void EditsTextUnderAnUprightScaledOrFlippedTransform(string content)
    {
        PdfDocument source = Raw(content);
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.Equal("OLD line OLD next", candidate.Text);
        var plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(30, 200, 250, 262), "AAAA", Font(), 10, 12, PdfParagraphAlignment.Left);
        PdfPageContent page = new PdfPageContentReader(PdfDocument.Open(PdfParagraphEditor.Apply(source, plan))).Read(0);
        Assert.DoesNotContain("OLD", page.Text, StringComparison.Ordinal);
        PdfExtractedLetter first = page.Letters[0];
        Assert.Equal("A", first.Value);
        Assert.Equal(plan.Lines[0].X, first.StartBaseLine.X, 1);
        Assert.Equal(plan.Lines[0].Baseline, first.StartBaseLine.Y, 1);
        Assert.Equal(10, first.PointSize, 1);
    }

    [Theory]
    [InlineData("q 0.5 0 0 1 0 0 cm BT /F1 10 Tf 60 250 Td (OLD) Tj ET Q")]
    [InlineData("BT /F1 10 Tf 0.9 0.1 -0.1 0.9 30 250 Tm (OLD) Tj ET")]
    [InlineData("BT /F1 10 Tf -1 0 0 1 90 250 Tm (OLD) Tj ET")]
    public void StillRefusesAStretchedRotatedOrMirroredTransform(string content) =>
        Assert.False(PdfParagraphEditor.Inspect(Raw(content), 0, new PdfContentBounds(20, 220, 100, 280)).CanEdit);

    // Word justifies text with word and character spacing, which changes from line to line.
    [Fact]
    public void EditsJustifiedLinesAndLeavesLaterTextSpacedAsItWas()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 2 Tw 1 Tc 1 0 0 1 30 250 Tm (OLD line) Tj " +
            "0.5 Tw 0.2 Tc 1 0 0 1 30 238 Tm (OLD next) Tj 1 0 0 1 200 30 Tm (Tail text) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0),
            item => item.Text.Contains("OLD", StringComparison.Ordinal));
        var target = new PdfContentBounds(30, 200, 200, 262);
        var plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion, target, "AAAA", Font(), 10, 12, PdfParagraphAlignment.Left);
        PdfDocument changed = PdfDocument.Open(PdfParagraphEditor.Apply(source, plan));
        PdfExtractedLetter[] letters = [.. new PdfPageContentReader(changed).Read(0).Letters.Where(letter => letter.Value == "A")];
        Assert.Equal(letters[0].EndBaseLine.X, letters[1].StartBaseLine.X, 5);
        Assert.Equal(Unselected(source, candidate.SourceRegion), Unselected(changed, target));
    }

    [Fact]
    public void PageGraphicsStatesAndFormsElsewhereDoNotBlockTheParagraph()
    {
        PdfDocument source = Raw("/GS1 gs q 20 0 0 20 5 5 cm /Fm1 Do Q BT /F1 10 Tf 30 250 Td (OLD) Tj ET",
            resources: "/ExtGState << /GS1 << /ca 1 /BM /Normal >> >> /XObject << /Fm1 7 0 R >>", extraObject: LogoForm);
        Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
    }

    [Theory]
    // Text inside a reusable form.
    [InlineData("/Fm1 Do", "/XObject << /Fm1 7 0 R >>")]
    // A soft mask would cut the replacement differently than the source.
    [InlineData("/GS1 gs BT /F1 10 Tf 30 250 Td (OLD) Tj ET", "/ExtGState << /GS1 << /SMask << /Type /Mask /S /Luminosity /G 7 0 R >> >> >>")]
    // Lines painted under different graphics states.
    [InlineData("/GS1 gs BT /F1 10 Tf 30 250 Td (OLD) Tj ET /GS2 gs BT /F1 10 Tf 30 238 Td (OLD) Tj ET", "/ExtGState << /GS1 << /ca 1 >> /GS2 << /ca 0.5 >> >>")]
    // A clip that cuts the source text.
    [InlineData("q 30 240 5 30 re W n BT /F1 10 Tf 30 250 Td (OLD) Tj ET Q", "")]
    // Text on an optional content layer.
    [InlineData("/OC /MC0 BDC BT /F1 10 Tf 30 250 Td (OLD) Tj ET EMC", "/Properties << /MC0 << /Type /OCG /Name (Layer) >> >>")]
    // Marked content that overrides the extracted text.
    [InlineData("/Span << /ActualText (NEW) >> BDC BT /F1 10 Tf 30 250 Td (OLD) Tj ET EMC", "")]
    public void RefusesWhenTheParagraphItselfCannotBeReplacedFaithfully(string content, string resources)
    {
        PdfDocument source = Raw(content, resources: resources, extraObject: FormWithOldText);
        Assert.False(PdfParagraphEditor.Inspect(source, 0, new PdfContentBounds(20, 220, 100, 280)).CanEdit);
    }

    [Fact]
    public void RefusesAReplacementBoxOutsideTheClipThatPaintsIt()
    {
        PdfDocument source = Raw("q 20 230 100 40 re W n BT /F1 10 Tf 30 250 Td (OLD) Tj ET Q");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.Throws<NotSupportedException>(() => PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(30, 100, 200, 200), "AAAA", Font(), 10, 12, PdfParagraphAlignment.Left));
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

    private const string LogoForm = "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Length 12 >>\nstream\n0 0 1 1 re f\nendstream";
    private const string FormWithOldText = "<< /Type /XObject /Subtype /Form /BBox [0 0 300 300] /Resources << /Font << /F1 4 0 R >> >> " +
        "/Length 39 >>\nstream\nBT /F1 10 Tf 30 250 Td (OLD) Tj ET     \nendstream";

    private static PdfDocument Raw(string content, string pageExtra = "", string catalogExtra = "", bool shared = false,
        string resources = "", string? extraObject = null)
    {
        string page = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] {pageExtra} /Resources << /Font << /F1 4 0 R /F2 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >> >> {resources} >> /Contents 5 0 R >>";
        string[] objects = [$"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>",
            $"<< /Type /Pages /Kids [3 0 R{(shared ? " 6 0 R" : "")}] /Count {(shared ? 2 : 1)} >>", page,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream", page,
            .. extraObject is null ? Array.Empty<string>() : [extraObject]];
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
