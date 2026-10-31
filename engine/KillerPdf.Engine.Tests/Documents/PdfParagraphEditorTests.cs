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

    // A page of pictures or a scan has no paragraph to complain about, so it says so.
    [Fact]
    public void APageWithNoTextSaysSo()
    {
        var inspection = PdfParagraphEditor.InspectCandidates(Raw("0 0 m 100 100 l S"), 0);

        Assert.Empty(inspection.Candidates);
        Assert.Equal(PdfParagraphRefusalCode.NoText, Assert.Single(inspection.Refusals).RefusalCode);
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
        Assert.ThrowsAny<NotSupportedException>(() => PdfParagraphEditor.Plan(source, 0,
            candidate.SourceRegion, candidate.SourceRegion, "Z",
            TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(format12: false)), 10, 12, PdfParagraphAlignment.Left));
        Assert.ThrowsAny<NotSupportedException>(() => PdfParagraphEditor.Plan(source, 0,
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
        NotSupportedException refusal = Assert.ThrowsAny<NotSupportedException>(() => PdfParagraphEditor.Plan(source, 0,
            candidate.SourceRegion, new PdfContentBounds(30, 200, 200, 262), "AAAA AAAA\nAAAA\nAAAA", Font(), 10, 12,
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

    // Only ink that leaves a tight clip is lost, so a default box wider than the clip is fine.
    [Fact]
    public void ACandidateKnowsItsClipAndABoxMayCrossItWhileTheGlyphsStayInside()
    {
        PdfDocument source = Raw("q 20 246 200 15 re W n BT /F1 10 Tf 30 250 Td (OLD text) Tj ET Q");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        var padded = new PdfContentBounds(candidate.SourceRegion.Left, 240, 280, 262);
        PdfContentBounds fitted = candidate.FitToClip(padded);
        Assert.True(fitted.Bottom >= 246 - 0.01 && fitted.Top <= 261 + 0.01);
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion, padded, "AAAA", Font(), 10, 12,
            PdfParagraphAlignment.Left);
        Assert.False(plan.HasOverflow, plan.OverflowReason);
        Assert.ThrowsAny<NotSupportedException>(() => PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            padded with { Bottom = 200 }, "AAAA", Font(), 30, 32, PdfParagraphAlignment.Left));
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
        Assert.ThrowsAny<NotSupportedException>(() => PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
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

    // pdfTeX places words with gaps instead of space glyphs, so the gaps have to come back as spaces.
    [Theory]
    [InlineData("[(Word) -300 (gaps) -300 (here)] TJ", "Word gaps here")]
    [InlineData("[(Ke) -100 (rned) 60 (word)] TJ", "Kernedword")]
    [InlineData("(Alpha) Tj 30 0 Td /F2 10 Tf (beta) Tj", "Alpha beta")]
    [InlineData("(Two  spaces) Tj", "Two spaces")]
    public void TheCandidateTextKeepsItsWordSpaces(string shown, string expected)
    {
        PdfDocument source = Raw($"BT /F1 10 Tf 30 250 Td {shown} ET");
        Assert.Equal(expected, Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0)).Text);
    }

    // A box's left edge is where glyph origins start, and some glyphs reach past their origin.
    [Theory]
    [InlineData("jump")]
    [InlineData("Every line here fits and the next one starts with an overhanging glyph jump")]
    public void AGlyphOverhangingTheBoxEdgeStillFits(string text)
    {
        TrueTypeFont italic = PdfStandardFontSubstitutes.OcrTrainingFonts()[6];
        PdfDocument source = Document();
        PdfParagraphCandidate candidate = PdfParagraphEditor.FindParagraphs(source, 0).First();
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(30, 100, 110, 260), text, italic, 10, 12, PdfParagraphAlignment.Left);
        Assert.False(plan.HasOverflow, plan.OverflowReason);
        Assert.All(plan.Lines, line => Assert.Equal(30, line.X, 6));
    }

    [Fact]
    public void GlyphsThatWouldLeaveThePageDoNotFit()
    {
        TrueTypeFont italic = PdfStandardFontSubstitutes.OcrTrainingFonts()[6];
        PdfDocument source = Document();
        PdfParagraphCandidate candidate = PdfParagraphEditor.FindParagraphs(source, 0).First();
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(0, 100, 110, 260), "jump", italic, 10, 12, PdfParagraphAlignment.Left);
        Assert.True(plan.HasOverflow);
    }

    // The default box comes from glyph origins, so opening a paragraph and pressing Preview must not move it.
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(8)]
    public void UnchangedTextPreviewsInPlaceFromTheDefaultBox(int font)
    {
        TrueTypeFont replacement = PdfStandardFontSubstitutes.OcrTrainingFonts()[font];
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (A short plain line of text here) Tj ET " +
            "BT /F1 10 Tf 30 200 Td (The first line of a short paragraph) Tj 0 -12 Td (continues here on the second line) Tj " +
            "0 -12 Td (and jumps on this third line) Tj ET");
        IReadOnlyList<PdfParagraphCandidate> candidates = PdfParagraphEditor.FindParagraphs(source, 0);
        Assert.Equal(2, candidates.Count);
        foreach (PdfParagraphCandidate candidate in candidates)
        {
            PdfParagraphPlan plan = Unchanged(source, candidate, replacement);
            Assert.False(plan.HasOverflow, plan.OverflowReason);
            Assert.Equal(30, plan.Lines[0].X, 6);
            Assert.Equal(candidate.FirstBaseline, plan.Lines[0].Baseline, 6);
            Assert.True(plan.Lines.Count <= candidate.RowTexts.Count);
            Assert.All(plan.Lines.Select((line, index) => (line, index)), item =>
                Assert.Equal(candidate.FirstBaseline - item.index * candidate.Leading, item.line.Baseline, 6));
        }
    }

    [Fact]
    public void TheDefaultBoxIsBuiltFromGlyphOriginsAndTheSourceRows()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (Old line) Tj 0 -12 Td (Old next line) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.Equal(30, candidate.TextLeft, 6);
        Assert.Equal(250, candidate.FirstBaseline, 6);
        Assert.Equal(["Old line", "Old next line"], candidate.RowTexts);
        PdfContentBounds box = PdfParagraphEditor.DefaultBox(candidate, 300, 300);
        Assert.Equal(30, box.Left, 6);
        Assert.Equal(258, box.Top, 6);
        Assert.Equal(candidate.TextRight, box.Right, 6);
        Assert.Equal(258 - 10 - 12, box.Bottom, 6);
    }

    // The box follows the text until someone resizes it, so a longer text or a larger size still fits.
    [Fact]
    public void TheDefaultBoxGrowsWithTheTextAndTheChosenSize()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (A short plain line of text here) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        string longer = candidate.Text + " and then another whole sentence is added by hand.";
        PdfContentBounds box = PdfParagraphEditor.DefaultBox(candidate, 300, 300, longer, Font());
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion, box, longer, Font(),
            candidate.FontSize, candidate.Leading, PdfParagraphAlignment.Left);
        Assert.False(plan.HasOverflow, plan.OverflowReason);
        Assert.True(plan.Lines.Count > 1);
        PdfContentBounds larger = PdfParagraphEditor.DefaultBox(candidate, 300, 300, candidate.Text, Font(), 14, 20);
        PdfParagraphPlan big = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion, larger, candidate.Text, Font(),
            14, 20, PdfParagraphAlignment.Left);
        Assert.False(big.HasOverflow, big.OverflowReason);
        Assert.Equal(250, big.Lines[0].Baseline, 6);
    }

    [Fact]
    public void TheDefaultBoxStopsAtTheSourceClipAndWrapsInstead()
    {
        TrueTypeFont wide = PdfStandardFontSubstitutes.OcrTrainingFonts()[8];
        PdfDocument source = Raw("q 0 0 170 300 re W n BT /F1 10 Tf 30 250 Td (Old line of text near the edge) Tj ET Q");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        PdfParagraphPlan plan = Unchanged(source, candidate, wide);
        Assert.False(plan.HasOverflow, plan.OverflowReason);
        Assert.True(plan.TargetBounds.Right <= 170);
        Assert.Equal(2, plan.Lines.Count);
    }

    [Fact]
    public void TheCoverCheckIsAboutTheNewGlyphsNotTheEmptyPartOfTheBox()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (Old line) Tj ET q 1 1 0 rg 20 100 200 13 re f Q");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(30, 90, 200, 258), "AAAA", Font(), 10, 12, PdfParagraphAlignment.Left);
        Assert.False(plan.HasOverflow, plan.OverflowReason);
    }

    // None of these need shaping, and extraction returns them, so a paragraph's own text has to pass.
    [Fact]
    public void DashesTheMinusSignAndLatinLigaturesAreOrdinaryText()
    {
        string dashes = $"a{(char)0x2013}b c{(char)0x2014}d {(char)0x2212}e {(char)0x2010}f";
        PdfDocument source = Document();
        PdfParagraphCandidate candidate = PdfParagraphEditor.FindParagraphs(source, 0).First();
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(20, 100, 280, 262), dashes + " ﬁne ﬂy", Font(), 10, 12,
            PdfParagraphAlignment.Left);
        Assert.False(plan.HasOverflow, plan.OverflowReason);
        Assert.Equal(dashes + " fine fly", plan.Lines[0].Text);
    }

    [Fact]
    public void AParagraphsOwnDashesPassPreview()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (Old line \\226 with dash \\227 and more) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.False(Unchanged(source, candidate, Font()).HasOverflow);
    }

    [Fact]
    public void ALigatureComesIntoTheDialogAsItsLetters()
    {
        PdfDocument source = Raw("BT /F3 10 Tf 30 250 Td (aline a) Tj ET", fonts:
            "/F3 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Type /Encoding /Differences [97 /fi] >> >>");
        Assert.Equal("filine fi", Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0)).Text);
    }

    private const string AboveAHeading = "BT /F1 10 Tf 30 250 Td (aaaa bbbb cccc dddd eeee ffff) Tj 0 -12 Td (gggg hhhh iiii jjjj kkkk llll) Tj ET " +
        "BT /F1 14 Tf 30 222 Td (Heading below it here) Tj ET";

    // Adding a row would land on what follows, so the glyphs give up a little width instead.
    [Fact]
    public void AnEditThatWouldRunIntoWhatFollowsNarrowsInstead()
    {
        PdfDocument source = Raw(AboveAHeading);
        PdfParagraphCandidate candidate = PdfParagraphEditor.FindParagraphs(source, 0).First();
        string text = candidate.Text + " mmmm";
        Assert.ThrowsAny<NotSupportedException>(() => PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            PdfParagraphEditor.DefaultBox(candidate, 300, 300, text, Font()), text, Font(), 10, 12, PdfParagraphAlignment.Left));
        PdfParagraphPlan plan = PdfParagraphEditor.PlanFitted(source, 0, candidate, 300, 300, text, Font(), 10, 12, PdfParagraphAlignment.Left);
        Assert.False(plan.HasOverflow, plan.OverflowReason);
        Assert.InRange(plan.HorizontalScale, 0.9, 0.995);
        Assert.Equal(2, plan.Lines.Count);
        Assert.All(plan.Lines, line => Assert.True(line.X + line.Width <= plan.TargetBounds.Right + 0.001));
        PdfPageContent after = new PdfPageContentReader(PdfDocument.Open(plan.GetPreviewDocument())).Read(0);
        Assert.Contains("llll mmmm", after.Text, StringComparison.Ordinal);
        Assert.Contains("Heading below it here", after.Text, StringComparison.Ordinal);
        // The page has to show the narrowing, not only the plan.
        Assert.True(after.Letters.Where(letter => letter.BoundingBox.Bottom > 228).Max(letter => letter.BoundingBox.Right) <= plan.TargetBounds.Right + 0.5);
    }

    [Fact]
    public void RoomBelowLeavesTheTextAtFullWidth()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (aaaa bbbb cccc dddd eeee ffff) Tj 0 -12 Td (gggg hhhh iiii jjjj kkkk llll) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        PdfParagraphPlan plan = PdfParagraphEditor.PlanFitted(source, 0, candidate, 300, 300, candidate.Text + " mm", Font(), 10, 12, PdfParagraphAlignment.Left);
        Assert.False(plan.HasOverflow, plan.OverflowReason);
        Assert.Equal(1, plan.HorizontalScale);
    }

    // Past a point it stops being the same type, so a real rewrite still gets a straight answer.
    [Fact]
    public void FarTooMuchTextIsStillRefused()
    {
        PdfDocument source = Raw(AboveAHeading);
        PdfParagraphCandidate candidate = PdfParagraphEditor.FindParagraphs(source, 0).First();
        string text = candidate.Text + string.Concat(Enumerable.Repeat(" more words", 12));
        Assert.ThrowsAny<NotSupportedException>(() =>
            PdfParagraphEditor.PlanFitted(source, 0, candidate, 300, 300, text, Font(), 10, 12, PdfParagraphAlignment.Left));
    }

    [Fact]
    public void WidthRatioComparesAFontWithThePage()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (Old line of text here) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.InRange(PdfParagraphEditor.WidthRatio(candidate, Font()), 0.8, 1.25);
    }

    // One glyph for one character needs no shaping, only a font that has it.
    [Theory]
    [InlineData("Let α and β vary by 2 μm")]
    [InlineData("Привет, мир")]
    public void GreekAndCyrillicNeedNoShaping(string text)
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (Old line of text here) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion, new PdfContentBounds(20, 100, 280, 262),
            text, Font(), 10, 12, PdfParagraphAlignment.Left);
        Assert.False(plan.HasOverflow, plan.OverflowReason);
        Assert.Contains(text, new PdfPageContentReader(PdfDocument.Open(plan.GetPreviewDocument())).Read(0).Text, StringComparison.Ordinal);
    }

    // These join, reorder or stack, and nothing here can do that.
    [Theory]
    [InlineData("שלום")]
    [InlineData("مرحبا")]
    [InlineData("नमस्ते")]
    public void ScriptsThatNeedShapingStayRefused(string text)
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (Old line of text here) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        var error = Assert.ThrowsAny<NotSupportedException>(() => PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(20, 100, 280, 262), text, Font(), 10, 12, PdfParagraphAlignment.Left));
        Assert.Contains("shaping", error.Message, StringComparison.Ordinal);
    }

    // LaTeX writes an accent as a letter and a mark, which is one ordinary character once composed.
    [Fact]
    public void ALetterAndItsAccentAreComposed()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (Old line of text here) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion, new PdfContentBounds(20, 100, 280, 262),
            "A café here", Font(), 10, 12, PdfParagraphAlignment.Left);
        Assert.False(plan.HasOverflow, plan.OverflowReason);
        Assert.Contains("café", new PdfPageContentReader(PdfDocument.Open(plan.GetPreviewDocument())).Read(0).Text, StringComparison.Ordinal);
    }

    // The fix is to retype them, which a word about shaping would never suggest.
    [Theory]
    [InlineData("Old � line")]
    [InlineData("Old \u007F line")]
    [InlineData("Old  line")]
    public void CharactersThePdfNeverIdentifiedAreNamedAsSuch(string text)
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (Old line of text here) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        var error = Assert.ThrowsAny<NotSupportedException>(() => PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(20, 100, 280, 262), text, Font(), 10, 12, PdfParagraphAlignment.Left));
        Assert.Contains("does not identify", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATabIsASpace()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (Old line of text here) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion, new PdfContentBounds(20, 100, 280, 262),
            "One\ttwo", Font(), 10, 12, PdfParagraphAlignment.Left);
        Assert.Equal("One two", Assert.Single(plan.Lines).Text);
    }

    // Web exports leave zero-width spaces around links. They print nothing and need no shaping.
    [Fact]
    public void ZeroWidthCharactersAreNotText()
    {
        PdfDocument source = Raw("BT /F3 10 Tf 30 250 Td (See https://~example.org~ now) Tj ET", fonts:
            "/F3 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Type /Encoding /Differences [126 /uni200B] >> >>");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.Equal("See https://example.org now", candidate.Text);
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion, new PdfContentBounds(20, 100, 280, 262),
            "See https://​example.org﻿ now today", Font(), 10, 12, PdfParagraphAlignment.Left);
        Assert.False(plan.HasOverflow, plan.OverflowReason);
        Assert.Contains("See https://example.org now today", new PdfPageContentReader(PdfDocument.Open(plan.GetPreviewDocument())).Read(0).Text,
            StringComparison.Ordinal);
    }

    // microtype stretches each line by up to 2 percent, which is typesetting and not a style.
    [Fact]
    public void FontExpansionThatChangesFromLineToLineIsEditable()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 1.02 0 0 1 30 250 Tm (Old line one) Tj 0.98 0 0 1 30 238 Tm (Old line two) Tj " +
            "1.012 0 0 1 30 226 Tm (Old three) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.Equal("Old line one Old line two Old three", candidate.Text);
        PdfParagraphPlan plan = Unchanged(source, candidate, Font());
        Assert.False(plan.HasOverflow, plan.OverflowReason);
        Assert.Contains(candidate.Text, new PdfPageContentReader(PdfDocument.Open(plan.GetPreviewDocument())).Read(0).Text,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.98)]
    [InlineData(1.004)]
    [InlineData(1.02)]
    public void TheReplacementIsWrittenAtTheWidthThePlanMeasured(double scale)
    {
        PdfDocument source = Raw(FormattableString.Invariant($"BT /F1 10 Tf {scale} 0 0 1 30 250 Tm (Old line of text) Tj ET"));
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(30, 200, 280, 262), "A much longer line of replacement text here", Font(), 10, 12,
            PdfParagraphAlignment.Right);
        PdfExtractedLetter last = new PdfPageContentReader(PdfDocument.Open(plan.GetPreviewDocument())).Read(0).Letters[^1];
        Assert.Equal(280, last.EndBaseLine.X, 2);
    }

    // The replacement inherits the color of the graphics state, so any color space works.
    [Theory]
    [InlineData("0 0 0 1 k", "k")]
    [InlineData("/DeviceRGB cs 0 0 0 sc", "sc")]
    [InlineData("/DeviceGray cs 0.2 scn", "scn")]
    public void AParagraphInAnyColorSpaceIsEditable(string color, string op)
    {
        PdfDocument source = Raw($"{color} BT /F1 10 Tf 30 250 Td (Old line) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.False(candidate.MixedStyles);
        PdfParagraphPlan plan = Unchanged(source, candidate, Font());
        PdfPageContent page = new PdfPageContentReader(PdfDocument.Open(plan.GetPreviewDocument())).Read(0);
        Assert.Contains("Old line", page.Text, StringComparison.Ordinal);
        Assert.Contains(page.Instructions, instruction => instruction.Operator == op);
    }

    // The replacement paints at the last source glyph, so a closing link must not color all of it,
    // and whatever is drawn after it must keep the color it had.
    [Theory]
    [InlineData("0 g", "0 0 1 rg")]
    [InlineData("0 0 0 1 k", "1 0 0 0 k")]
    [InlineData("/DeviceGray cs 0 sc", "/DeviceRGB cs 0 0 1 sc")]
    public void AWordInAnotherColorTakesTheDominantColor(string body, string link)
    {
        PdfDocument source = Raw($"{body} BT /F1 10 Tf 30 250 Td (Old body text with its ) Tj {link} (link) Tj ET " +
            "BT /F1 10 Tf 30 100 Td (HHHHHHHH) Tj ET");
        PdfParagraphCandidate candidate = PdfParagraphEditor.FindParagraphs(source, 0).First();
        Assert.Equal("Old body text with its link", candidate.Text);
        Assert.True(candidate.MixedStyles);
        Assert.Equal(new PdfRgbColor(0, 0, 0), candidate.Color);
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(30, 200, 280, 258), "HHHHHHHHHHHH", Font(), 10, 12, PdfParagraphAlignment.Left);
        var page = new PdfPageRenderer(PdfDocument.Open(PdfParagraphEditor.Apply(source, plan))).Render(0, new PdfRenderOptions(300, 300));
        Assert.True(Colorfulness(page, 30, 240, 100, 260) < 40, "The replacement took the link's color.");
        Assert.True(Colorfulness(page, 30, 95, 100, 112) > 120, "Text drawn after the paragraph lost its color.");
    }

    [Theory]
    [InlineData("BT /F1 10 Tf 30 250 Td (OLD) Tj 1 0 0 rg 0 -12 Td (OLD) Tj ET")]
    [InlineData("BT /F1 10 Tf 30 250 Td (OLD) Tj ET 1 0 0 rg BT /F1 10 Tf 30 238 Td (OLD) Tj ET")]
    public void ALineInAnotherColorStaysInItsParagraph(string content) =>
        Assert.True(Assert.Single(PdfParagraphEditor.FindParagraphs(Raw(content), 0)).MixedStyles);

    [Fact]
    public void TheSameColorWrittenAgainIsNotAMixedStyle()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (Old line) Tj 0 g ( more) Tj ET");
        Assert.False(Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0)).MixedStyles);
    }

    [Fact]
    public void AFirstLineIndentStaysInItsParagraphAndIsKept()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 42 250 Td (Old first line indented) Tj -12 -12 Td (Old body line two) Tj " +
            "0 -12 Td (Old body line three) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.Equal("Old first line indented\nOld body line two\nOld body line three", candidate.Text);
        Assert.Equal(30, candidate.TextLeft, 6);
        Assert.Equal(12, candidate.FirstLineIndent, 6);
        PdfParagraphPlan plan = Unchanged(source, candidate, Font());
        Assert.False(plan.HasOverflow, plan.OverflowReason);
        Assert.Equal(42, plan.Lines[0].X, 6);
        Assert.All(plan.Lines.Skip(1), line => Assert.Equal(30, line.X, 6));
        PdfPageContent page = new PdfPageContentReader(PdfDocument.Open(plan.GetPreviewDocument())).Read(0);
        Assert.Equal(42, page.Letters[0].StartBaseLine.X, 2);
        Assert.Equal(250, page.Letters[0].StartBaseLine.Y, 2);
    }

    [Fact]
    public void AFirstLineIndentAppliesOnlyToLeftAlignedText()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 42 250 Td (Old first line) Tj -12 -12 Td (Old body line) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        var target = new PdfContentBounds(30, 200, 200, 258);
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion, target, "AAAA", Font(), 10, 12,
            PdfParagraphAlignment.Right);
        Assert.Equal(target.Right, plan.Lines[0].X + plan.Lines[0].Width, 6);
    }

    [Fact]
    public void ABoxNarrowerThanTheIndentDropsTheIndent()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 42 250 Td (Old first line) Tj -12 -12 Td (Old body line) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(30, 100, 40, 258), "AA", Font(), 10, 12, PdfParagraphAlignment.Left);
        Assert.Equal(30, plan.Lines[0].X, 6);
    }

    [Fact]
    public void TwoIndentedParagraphsStaySeparate()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 42 250 Td (First para opening) Tj -12 -12 Td (First para ends) Tj " +
            "12 -12 Td (Second para opening) Tj -12 -12 Td (Second para ends) Tj ET");
        Assert.Equal(["First para opening First para ends", "Second para opening Second para ends"],
            PdfParagraphEditor.FindParagraphs(source, 0).Select(item => item.Text));
    }

    [Fact]
    public void AWholeLineInAnotherFontStaysInItsParagraph()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (Old line one is plain) Tj 0 -12 Td /F2 10 Tf (This whole line is bold) Tj " +
            "0 -12 Td /F1 10 Tf (Old line three plain) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.Equal("Old line one is plain This whole line is bold Old line three plain", candidate.Text);
        Assert.True(candidate.MixedStyles);
    }

    [Fact]
    public void ABoldHeadingAboveItsBodyStaysItsOwnParagraph()
    {
        PdfDocument source = Raw("BT /F2 10 Tf 30 250 Td (Bold heading line) Tj /F1 10 Tf 0 -12 Td (Body line one) Tj 0 -12 Td (Body line two) Tj ET");
        Assert.Equal(["Bold heading line", "Body line one Body line two"],
            PdfParagraphEditor.FindParagraphs(source, 0).Select(item => item.Text));
    }

    [Theory]
    [InlineData("BT /F1 10 Tf 30 250 Td (Name) Tj 28 0 Td (Value) Tj -28 -12 Td (Alpha) Tj 28 0 Td (10) Tj -28 -12 Td (Beta) Tj 28 0 Td (20) Tj ET")]
    [InlineData("BT /F1 10 Tf 30 250 Td (Name) Tj 0 -12 Td (Alpha) Tj 0 -12 Td (Beta) Tj 28 24 Td (Value) Tj 0 -12 Td (10) Tj 0 -12 Td (20) Tj ET")]
    [InlineData("BT /F1 10 Tf 30 250 Td (Cat) Tj 28 0 Td (one) Tj -28 -12 Td (Dog) Tj 28 0 Td (two) Tj ET")]
    public void ASmallTableIsNotOfferedAsOneParagraph(string content)
    {
        PdfDocument source = Raw(content);
        Assert.DoesNotContain(PdfParagraphEditor.FindParagraphs(source, 0), item => item.Text.Contains("10", StringComparison.Ordinal)
            || item.Text.Contains("two", StringComparison.Ordinal));
        Assert.Contains(PdfParagraphEditor.InspectCandidates(source, 0).Refusals, item =>
            item.Reason.Contains("table", StringComparison.Ordinal));
    }

    [Fact]
    public void WordsThatLineUpByChanceAreNotATable()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (Old line ends here) Tj 0 -12 Td (Old line goes on) Tj 0 -12 Td (Old line stops) Tj ET");
        Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
    }

    // Each path to an element can add replacement text, so the first path must not settle it.
    [Theory]
    [InlineData("[7 0 R 8 0 R]", "<< /S /P /K 0 >>", "<< /S /Figure /Alt (desc) /K 7 0 R >>", "null")]
    [InlineData("[8 0 R 9 0 R]", "0", "<< /S /P /K 7 0 R >>", "<< /S /P /K 7 0 R /ActualText (x) >>")]
    [InlineData("[8 0 R 9 0 R]", "<< /Type /MCR /MCID 0 >>", "<< /S /P /Pg 6 0 R /K 7 0 R >>", "<< /S /P /Pg 3 0 R /K 7 0 R /ActualText (x) >>")]
    public void ReplacementTextOnASecondPathToAnElementIsRespected(string kids, string first, string second, string third)
    {
        PdfDocument source = Raw("/P <</MCID 0>> BDC BT /F1 10 Tf 1 0 0 1 30 250 Tm (OLD line) Tj ET EMC",
            catalogExtra: $"/StructTreeRoot << /Type /StructTreeRoot /K {kids} >>", extraObjects: [first, second, third]);
        Assert.Empty(PdfParagraphEditor.FindParagraphs(source, 0));
    }

    // One structure element can hold several marked content sequences, and inline elements sit inside a paragraph.
    [Theory]
    [InlineData("<< /S /P /K [0 1] >>")]
    [InlineData("<< /S /P /K [<< /Type /MCR /MCID 0 >> << /Type /MCR /MCID 1 >>] >>")]
    [InlineData("<< /S /P /K [0 << /S /Span /K 1 >>] >>")]
    [InlineData("<< /S /P /K [<< /S /Link /K 0 >> << /S /Em /K [<< /S /Strong /K 1 >>] >>] >>")]
    public void ThePartsOfOneTaggedParagraphAreOneParagraph(string element)
    {
        PdfDocument source = Raw("/P <</MCID 0>> BDC BT /F1 10 Tf 1 0 0 1 30 250 Tm (Word one) Tj ET EMC " +
            "/P <</MCID 1>> BDC BT /F1 10 Tf 1 0 0 1 30 238 Tm (Word two) Tj ET EMC",
            catalogExtra: $"/StructTreeRoot << /Type /StructTreeRoot /K {element} >>");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.Equal("Word one Word two", candidate.Text);
        PdfParagraphPlan plan = Unchanged(source, candidate, Font());
        Assert.False(plan.HasOverflow, plan.OverflowReason);
        Assert.Contains("Word one Word two", new PdfPageContentReader(PdfDocument.Open(plan.GetPreviewDocument())).Read(0).Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnInlineElementsReplacementTextStillRefusesTheParagraph()
    {
        PdfDocument source = Raw("/P <</MCID 0>> BDC BT /F1 10 Tf 1 0 0 1 30 250 Tm (Word one) Tj ET EMC " +
            "/P <</MCID 1>> BDC BT /F1 10 Tf 1 0 0 1 30 238 Tm (Word two) Tj ET EMC",
            catalogExtra: "/StructTreeRoot << /Type /StructTreeRoot /K << /S /P /K [0 << /S /Span /K 1 /ActualText (x) >>] >> >>");
        Assert.DoesNotContain(PdfParagraphEditor.FindParagraphs(source, 0), item => item.Text.Contains("Word two", StringComparison.Ordinal));
    }

    [Fact]
    public void DifferentTaggedElementsStaySeparateWhenTheTreeNamesThem()
    {
        PdfDocument source = Raw("/P <</MCID 0>> BDC BT /F1 10 Tf 1 0 0 1 30 250 Tm (Word one) Tj ET EMC " +
            "/P <</MCID 1>> BDC BT /F1 10 Tf 1 0 0 1 30 238 Tm (Word two) Tj ET EMC",
            catalogExtra: "/StructTreeRoot << /Type /StructTreeRoot /K << /S /Div /K [<< /S /P /K 0 >> << /S /P /K 1 >>] >> >>");
        Assert.Equal(["Word one", "Word two"], PdfParagraphEditor.FindParagraphs(source, 0).Select(item => item.Text));
    }

    // An MCR with /Stm names a sequence in that form's own numbering, not the page's.
    [Fact]
    public void AFormsMarkedContentNumberIsNotThePages()
    {
        PdfDocument source = Raw("/P <</MCID 0>> BDC BT /F1 10 Tf 1 0 0 1 30 250 Tm (OLD line) Tj ET EMC",
            catalogExtra: "/StructTreeRoot << /Type /StructTreeRoot /K << /S /P /ActualText (x) /Pg 3 0 R /K [<< /Type /MCR /MCID 0 /Stm 7 0 R >>] >> >>",
            extraObjects: [LogoForm]);
        Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
    }

    private const string Type3Font = "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 100 100] /FontMatrix [0.01 0 0 0.01 0 0] " +
        "/CharProcs << /a 8 0 R >> /Encoding << /Type /Encoding /Differences [97 /a] >> /FirstChar 97 /LastChar 97 /Widths [60] >>";
    private const string Type3Glyph = "<< /Length 21 >>\nstream\n60 0 d0 0 0 50 50 re f\nendstream";

    // Type 3 glyphs are drawn by the font itself, wherever they fall in the paragraph.
    [Fact]
    public void AType3GlyphAnywhereInTheParagraphRefusesIt()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (O) Tj /F3 10 Tf (aaaaaaaaaaaaaaaaaaaa) Tj ET", fonts: "/F3 7 0 R",
            extraObjects: [Type3Font, Type3Glyph]);
        Assert.Empty(PdfParagraphEditor.FindParagraphs(source, 0));
    }

    [Fact]
    public void Type3WidthsGoThroughTheFontMatrix()
    {
        PdfDocument source = Raw("BT /F3 10 Tf 30 250 Td (aa) Tj ET", fonts: "/F3 7 0 R", extraObjects: [Type3Font, Type3Glyph]);
        PdfExtractedLetter[] letters = [.. new PdfPageContentReader(source).Read(0).Letters];
        Assert.Equal(36, letters[1].StartBaseLine.X, 6);
        Assert.Equal(42, letters[1].EndBaseLine.X, 6);
    }

    // An underline drawn as a thin filled box belongs to its line.
    [Theory]
    [InlineData("BT /F1 10 Tf 1 0 0 1 30 250 Tm (Old line) Tj ET 30 247 33 .5 re f BT /F1 10 Tf 1 0 0 1 30 238 Tm (Old more) Tj ET")]
    [InlineData("BT /F1 10 Tf 1 0 0 1 30 250 Tm (Old line) Tj ET BT /F1 10 Tf 1 0 0 1 30 238 Tm (Old more) Tj ET 30 235 33 .5 re f")]
    [InlineData("BT /F1 10 Tf 1 0 0 1 30 250 Tm (Old line) Tj ET q 1 0 0 1 30 247 cm 0 0 33 .5 re f Q BT /F1 10 Tf 1 0 0 1 30 238 Tm (Old more) Tj ET")]
    public void AnUnderlineLeavesWithItsLine(string content)
    {
        PdfDocument source = Raw(content);
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        PdfParagraphPlan plan = Unchanged(source, candidate, Font());
        Assert.False(plan.HasOverflow, plan.OverflowReason);
        Assert.DoesNotContain(new PdfPageContentReader(PdfDocument.Open(plan.GetPreviewDocument())).Read(0).Paths,
            path => !path.IsClippingPath);
    }

    [Theory]
    // Wider than the line, so it is a page rule.
    [InlineData("q 20 247 200 .5 re f Q")]
    // Well below the baseline.
    [InlineData("q 30 243 35 .5 re f Q")]
    // Too thick for an underline.
    [InlineData("q 30 244 35 3 re f Q")]
    public void AThinRuleThatIsNotAnUnderlineStays(string rule)
    {
        PdfDocument source = Raw($"BT /F1 10 Tf 1 0 0 1 30 250 Tm (Old line) Tj ET {rule} BT /F1 10 Tf 1 0 0 1 30 230 Tm (Other) Tj ET");
        PdfParagraphCandidate candidate = PdfParagraphEditor.FindParagraphs(source, 0).First();
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(30, 248, 200, 258), "New line", Font(), 10, 12, PdfParagraphAlignment.Left);
        Assert.Single(new PdfPageContentReader(PdfDocument.Open(plan.GetPreviewDocument())).Read(0).Paths);
    }

    [Fact]
    public void ARuleInsideALongerPathOrUnderARotationIsLeftAlone()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 1 0 0 1 30 250 Tm (Old line) Tj ET 30 247 m 40 247 l 30 247 35 .5 re f " +
            "q 0 1 -1 0 300 0 cm 30 247 35 .5 re f Q");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(30, 248, 200, 258), "New line", Font(), 10, 12, PdfParagraphAlignment.Left);
        Assert.Equal(2, new PdfPageContentReader(PdfDocument.Open(plan.GetPreviewDocument())).Read(0).Paths.Count);
    }

    // A damaged font must come back as a refusal rather than an arithmetic error.
    [Fact]
    public void AnImpossibleFontLengthIsARefusal()
    {
        PdfDocument source = Raw("BT /F3 10 Tf 30 250 Td (OLD) Tj ET", fonts: "/F3 7 0 R", extraObjects:
        [
            "<< /Type /Font /Subtype /Type1 /BaseFont /Bad /FontDescriptor 8 0 R /FirstChar 65 /LastChar 65 /Widths [500] >>",
            "<< /Type /FontDescriptor /FontName /Bad /Flags 32 /FontFile 9 0 R >>",
            "<< /Length1 99999999999 /Length2 0 /Length3 0 /Length 5 >>\nstream\nhello\nendstream"
        ]);
        PdfParagraphCandidateInspection inspection = PdfParagraphEditor.InspectCandidates(source, 0);
        Assert.Empty(inspection.Candidates);
        Assert.Contains("damaged", Assert.Single(inspection.Refusals).Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("(The word is sim-)", "(ilar to most)", "The word is similar to most")]
    [InlineData("(See Smith-)", "(Jones here)", "See Smith-Jones here")]
    [InlineData("(Now 3-)", "(way talks)", "Now 3-way talks")]
    public void ALineEndHyphenIsDroppedFromABrokenWord(string first, string second, string expected)
    {
        PdfDocument source = Raw($"BT /F1 10 Tf 30 250 Td {first} Tj 0 -12 Td {second} Tj ET");
        Assert.Equal(expected, Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0)).Text);
    }

    // A soft break and a compound look the same at a line end, so the rest of the document decides,
    // and what nothing settles is joined and handed to the dialog to ask about.
    [Theory]
    // The compound written whole, on another page.
    [InlineData("It is well-", "known, to all", "a well-known thing", "It is well-known, to all", "")]
    // The word written whole with no hyphen.
    [InlineData("It runs in real-", "time always", "and realtime data", "It runs in realtime always", "")]
    // Both forms: a real hyphen is never the one to lose.
    [InlineData("It runs in real-", "time always", "realtime and real-time", "It runs in real-time always", "")]
    // Both halves are words of the document.
    [InlineData("This leads to the so-", "called problem", "so it was called", "This leads to the so-called problem", "")]
    [InlineData("It is the real-", "time rate", "a real rate in time", "It is the real-time rate", "")]
    [InlineData("A state-of-the-", "art method", "the art of it", "A state-of-the-art method", "")]
    // A longer compound that opens with it, and the one after a slash.
    [InlineData("A clear line-", "of-sight path", "the line-of-sight view", "A clear line-of-sight path", "")]
    [InlineData("On a sub-", "graph here", "many sub-graphs and a non-sub-graph", "On a sub-graph here", "")]
    [InlineData("A passive/device-", "free one", "the device was free", "A passive/device-free one", "")]
    // A compound inside text whose word spaces did not survive, and one broken at a later hyphen.
    [InlineData("A good short-", "term plan", "Predictingshort-terminterests", "A good short-term plan", "")]
    [InlineData("Given two anti-", "self-dual ones", "is anti-self-\ndual here", "Given two anti-self-dual ones", "")]
    // A hyphen in an address is never a soft break.
    [InlineData("See http://a.example/pc-", "dpc here", "other things", "See http://a.example/pc-dpc here", "")]
    [InlineData("Mail yi.wang-", "li@uconn.edu now", "other things", "Mail yi.wang-li@uconn.edu now", "")]
    // The word written whole outranks a compound that only opens the same way.
    [InlineData("It is re-", "al work", "real and re-allocate", "It is real work", "")]
    // Nothing settles it.
    [InlineData("The word is sim-", "ilar to most", "other things", "The word is similar to most", "similar")]
    [InlineData("Well-", "known, to all", "other things", "Wellknown, to all", "Wellknown")]
    // A capital or a digit on either side rules out a soft break, and no space goes inside the word.
    [InlineData("We use Wi-", "Fi here", "other things", "We use Wi-Fi here", "")]
    [InlineData("The COVID-", "19 rules", "other things", "The COVID-19 rules", "")]
    [InlineData("The COVID-", "related rules", "other things", "The COVID-related rules", "")]
    [InlineData("An Anglo-", "Saxon word", "other things", "An Anglo-Saxon word", "")]
    [InlineData("Now 3-", "way talks", "other things", "Now 3-way talks", "")]
    // A word broken across a line end somewhere else proves nothing about either half.
    [InlineData("The com-", "puter here", "a com-\nputer there", "The computer here", "computer")]
    public void TheDocumentDecidesALineEndHyphen(string first, string second, string other, string expected, string guess)
    {
        PdfDocument source = Pages([first, second], other.Split('\n'));
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.Equal(expected, candidate.Text);
        Assert.Equal(guess.Length == 0 ? [] : [guess], candidate.HyphenGuesses);
    }

    [Fact]
    public void ThePageBeingEditedCountsEvenPastTheLastPageReadForEvidence()
    {
        var builder = new PdfDocumentBuilder();
        for (int index = 0; index < 301; index++)
            builder.AddPage(300, 300, Rows(index == 300 ? ["It is well-", "known, to all", "", "", "", "", "", "", "a well-known thing"] : ["Other"]));
        PdfParagraphCandidate candidate = PdfParagraphEditor.FindParagraphs(PdfDocument.Open(builder.Build()), 300).First();
        Assert.Equal("It is well-known, to all", candidate.Text);
    }

    // A soft hyphen says what it is, so there is nothing to ask about.
    [Fact]
    public void ASoftHyphenAtTheEndOfARowJoinsItsWord()
    {
        PdfDocument source = Raw("BT /F3 10 Tf 30 250 Td (The interA) Tj 0 -12 Td (national body) Tj ET", fonts:
            "/F3 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Type /Encoding /Differences [65 /sfthyphen] >> >>");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.Equal("The international body", candidate.Text);
        Assert.Empty(candidate.HyphenGuesses);
    }

    // Not every font has the Unicode hyphen, and the plain one looks the same.
    [Fact]
    public void AKeptHyphenIsThePlainOne()
    {
        PdfDocument source = Raw("BT /F3 10 Tf 30 250 Td (We use WiA) Tj 0 -12 Td (Fi here) Tj ET", fonts:
            "/F3 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding << /Type /Encoding /Differences [65 /uni2010] >> >>");
        Assert.Equal("We use Wi-Fi here", Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0)).Text);
    }

    // A URL broken across rows must not gain a space.
    [Theory]
    [InlineData("(See example.com/)", "(path/to here)", "See example.com/path/to here")]
    [InlineData("(Use and/)", "(or both)", "Use and/or both")]
    [InlineData("(See https://example.com/download/v)", "(1.2/file.zip)", "See https://example.com/download/v1.2/file.zip")]
    [InlineData("(See https://example.com/path)", "(for more of it)", "See https://example.com/path for more of it")]
    [InlineData("(See https://example.com/path.)", "(And/or more)", "See https://example.com/path. And/or more")]
    public void NoSpaceFollowsASlashAtTheEndOfARow(string first, string second, string expected)
    {
        PdfDocument source = Raw($"BT /F1 10 Tf 30 250 Td {first} Tj 0 -12 Td {second} Tj ET");
        Assert.Equal(expected, Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0)).Text);
    }

    // A shell line that continues on the next row is still its own row, however long that row is.
    [Fact]
    public void ABackslashAtTheEndOfARowKeepsTheRow()
    {
        PdfDocument source = Raw(@"BT /F1 10 Tf 30 250 Td ($ wget \\) Tj 0 -12 Td (https://example.com/download/file-1.2.zip) Tj ET");
        Assert.Equal("$ wget \\\nhttps://example.com/download/file-1.2.zip", Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0)).Text);
    }

    // Nothing was typed, so nothing moves and no hyphen goes, whichever way the words were joined.
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(6)]
    public void UnchangedTextComesBackInTheRowsThePageHasThem(int font)
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (The method is well-) Tj 0 -12 Td (known and also rea-) Tj " +
            "0 -12 Td (sonable in a way that) Tj 0 -12 Td (runs on for a while) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        PdfParagraphPlan plan = Unchanged(source, candidate, PdfStandardFontSubstitutes.OcrTrainingFonts()[font]);
        Assert.False(plan.HasOverflow, plan.OverflowReason);
        Assert.Equal(candidate.RowTexts, plan.Lines.Select(line => line.Text));
    }

    // Rows set a little unevenly stay on their own baselines, and an edit goes back to even lines.
    [Theory]
    [InlineData("BT /F1 10 Tf 30 250 Td (Old line of text here) Tj 0 -12 Td (and a second line of it) Tj 0 -16 Td (and the third row here) Tj ET")]
    [InlineData("BT /F1 10 Tf 30 250 Td (Old line of text here) Tj 0 -14 Td (and a second line of it) Tj 0 -11.5 Td (and the third row here) Tj ET")]
    public void UnchangedRowsStayOnTheirOwnBaselines(string content)
    {
        PdfDocument source = Raw(content);
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        PdfParagraphPlan plan = Unchanged(source, candidate, Font());
        Assert.False(plan.HasOverflow, plan.OverflowReason);
        double[] baselines = [.. new PdfPageContentReader(source).Read(0).Lines.Select(line => line.Runs[0].Letters[0].StartBaseLine.Y)];
        Assert.Equal(baselines, plan.Lines.Select(line => line.Baseline), (a, b) => Math.Abs(a - b) < 0.001);
        string edited = candidate.Text + " more";
        PdfParagraphPlan changed = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            PdfParagraphEditor.DefaultBox(candidate, 300, 300, edited, Font()), edited, Font(), candidate.FontSize,
            candidate.Leading, PdfParagraphAlignment.Left);
        Assert.Equal(candidate.Leading, changed.Lines[0].Baseline - changed.Lines[1].Baseline, 6);
        Assert.Equal(candidate.Leading, changed.Lines[1].Baseline - changed.Lines[2].Baseline, 6);
    }

    [Fact]
    public void AnEditedParagraphIsWrappedFromItsJoinedText()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (The word is sim-) Tj 0 -12 Td (ilar to most) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        string edited = candidate.Text.Replace("most", "many", StringComparison.Ordinal);
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            PdfParagraphEditor.DefaultBox(candidate, 300, 300, edited, Font()), edited, Font(), candidate.FontSize,
            candidate.Leading, PdfParagraphAlignment.Left);
        Assert.Equal(edited, string.Join(" ", plan.Lines.Select(line => line.Text)));
    }

    // Letter spacing widens every gap, so a word gap is measured from the row's own letter gap.
    [Theory]
    [InlineData("2 Tc (ANNUAL REPORT) Tj", "ANNUAL REPORT")]
    [InlineData("1.6 Tc (Tracked body words here) Tj", "Tracked body words here")]
    [InlineData("2 Tc [(TWO) -300 (WORDS)] TJ", "TWO WORDS")]
    [InlineData("0 Tc [(Word) -300 (gaps) -300 (here)] TJ", "Word gaps here")]
    // Digits are not letters spaced apart.
    [InlineData("0 Tc [(1) -300 (2) -300 (3) -300 (4) -300 (5)] TJ", "1 2 3 4 5")]
    public void LetterSpacingIsNotReadAsWordSpaces(string shown, string expected)
    {
        PdfDocument source = Raw($"BT /F1 10 Tf 30 250 Td {shown} ET");
        Assert.Equal(expected, Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0)).Text);
    }

    [Fact]
    public void EachRowOfATrackedParagraphReadsAsWords()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 1.6 Tc 30 250 Td (Tracked body words here) Tj 0 -12 Td (and more words follow) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.Equal(["Tracked body words here", "and more words follow"], candidate.RowTexts);
    }

    // A row that stops with room for the next word ended on purpose, as lists and addresses do.
    [Theory]
    [InlineData(new[] { "John Smith", "123 Main Street, Apartment 4", "Springfield" }, 0)]
    [InlineData(new[] { "John Smith", "123 Main Street, Apartment 4", "Springfield" }, 4)]
    [InlineData(new[] { "1. Apples", "2. A much longer list item with words", "3. Plums", "4. Pears" }, 0)]
    [InlineData(new[] { "1. Apples", "2. A much longer list item with words", "3. Plums", "4. Pears" }, 4)]
    [InlineData(new[] { "1. Apples", "2. A much longer list item with words", "3. Plums", "4. Pears" }, 6)]
    public void RowsThatEndOnPurposeKeepTheirBreaks(string[] rows, int font)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(300, 300, Rows(rows)).Build());
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.Equal(string.Join("\n", rows), candidate.Text);
        PdfParagraphPlan plan = Unchanged(source, candidate, PdfStandardFontSubstitutes.OcrTrainingFonts()[font]);
        Assert.Equal(rows, plan.Lines.Select(line => line.Text));
    }

    [Fact]
    public void AnEditKeepsTheRowBreaksInTheText()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(300, 300, Rows(["John Smith", "123 Main Street, Apartment 4", "Springfield"])).Build());
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        string edited = candidate.Text.Replace("John", "Jon", StringComparison.Ordinal);
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            PdfParagraphEditor.DefaultBox(candidate, 300, 300, edited, Font()), edited, Font(), candidate.FontSize,
            candidate.Leading, PdfParagraphAlignment.Left);
        Assert.Equal(edited.Split('\n'), plan.Lines.Select(line => line.Text));
    }

    [Fact]
    public void AFullJustifiedParagraphHasNoRowBreaks()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(300, 300,
            Rows(["aaaa aaaa aaaa aaaa", "bbbb bbbb bbbb bbbb", "dddd dddd dddd dddd", "ee nn"])).Build());
        Assert.DoesNotContain('\n', Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0)).Text);
    }

    // The room is judged against the longest row, so the short rows of ragged text stay joined.
    [Fact]
    public void ARaggedRowWithNoRoomForTheNextWordIsNotAnEnd()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(300, 300,
            Rows(["some words on one row", "extraordinarily long word", "and then the last row of it"])).Build());
        Assert.DoesNotContain('\n', Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0)).Text);
    }

    private const string Courier = "/F3 << /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>";

    // A space glyph leaves no gap between its neighbours, so a run of them has to count as one.
    [Theory]
    [InlineData("BT /F3 10 Tf 30 250 Td (Name        Qty    Price) Tj 0 -11 Td (Alpha       10     1.50) Tj 0 -11 Td (Beta        200    12.25) Tj ET")]
    // Right aligned cells start anywhere and end together.
    [InlineData("BT /F1 10 Tf 30 250 Td (Apples) Tj ET BT /F1 10 Tf 73 250 Td (1.50) Tj ET BT /F1 10 Tf 30 238 Td (Pears) Tj ET BT /F1 10 Tf 67.4 238 Td (12.25) Tj ET")]
    public void TablesPaddedWithSpacesOrAlignedRightAreRefused(string content)
    {
        PdfParagraphCandidateInspection inspection = PdfParagraphEditor.InspectCandidates(Raw(content, fonts: Courier), 0);
        Assert.Empty(inspection.Candidates);
        Assert.Contains(inspection.Refusals, item => item.Reason.Contains("table", StringComparison.Ordinal));
    }

    // One space glyph is a word space, however far justification stretches it, and a double space
    // after a sentence is not a column just because a word starts below it in a monospaced font.
    [Theory]
    [InlineData("BT /F1 10 Tf 9 Tw 30 250 Td (But of those that) Tj 2 Tw 0 -12 Td (offer total choice over half think) Tj 7 Tw 0 -12 Td (everybody needs to be) Tj ET")]
    [InlineData("BT /F1 10 Tf 9 Tw 30 250 Td (Two words) Tj 0 -12 Td (More words) Tj ET")]
    [InlineData("BT /F3 10 Tf 30 250 Td (Copyright 2012 the Trust and the persons) Tj 0 -11 Td (document authors.  All rights reserved.) Tj ET")]
    public void WordSpacesAreNotColumns(string content) =>
        Assert.Single(PdfParagraphEditor.FindParagraphs(Raw(content, fonts: Courier), 0));

    // Only the right margin lines up here, which is what every justified paragraph does.
    [Fact]
    public void JustifiedRowsWithWideGapsAreNotATable()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td [(one) -900 (two) -900 (three) -700 (four)] TJ 0 -12 Td " +
            "[(fives) -600 (sixes) -1000 (seven)] TJ 0 -12 Td (the last row) Tj ET");
        Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
    }

    [Fact]
    public void ATableDrawnColumnByColumnKeepsItsRows()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (Name) Tj 0 -12 Td (Alpha) Tj 0 -12 Td (Beta) Tj ET " +
            "BT /F1 10 Tf 120 250 Td (Qty) Tj 0 -12 Td (10) Tj 0 -12 Td (20) Tj ET");
        foreach (PdfParagraphCandidate candidate in PdfParagraphEditor.FindParagraphs(source, 0))
            Assert.Equal(candidate.RowTexts, Unchanged(source, candidate, Font()).Lines.Select(line => line.Text));
    }

    private const string TwoColumns =
        "BT /F1 10 Tf 30 250 Td (Old line of text near the edge) Tj 0 -12 Td (and a second line of it here) Tj ET " +
        "BT /F1 10 Tf 182 250 Td (Next column words) Tj 0 -12 Td (and its second line) Tj 0 -12 Td (and a third) Tj ET";

    // A wider replacement font has to wrap, because the next column stays where it is.
    [Fact]
    public void TheDefaultBoxStopsShortOfTheNextColumn()
    {
        TrueTypeFont wide = PdfStandardFontSubstitutes.OcrTrainingFonts()[8];
        PdfDocument source = Raw(TwoColumns);
        PdfParagraphCandidate candidate = PdfParagraphEditor.FindParagraphs(source, 0).First();
        Assert.Equal(182, candidate.RightLimit!.Value, 0);
        PdfContentBounds box = PdfParagraphEditor.DefaultBox(candidate, 300, 300, candidate.Text, wide);
        Assert.True(box.Right < 182, $"The box ends at {box.Right}.");
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion, box, candidate.Text, wide,
            candidate.FontSize, candidate.Leading, PdfParagraphAlignment.Left);
        Assert.All(plan.Lines, line => Assert.True(line.X + line.Width < 182, $"{line.Text} ends at {line.X + line.Width}."));
    }

    [Fact]
    public void WideningForARejoinedWordStopsAtTheNextColumnToo()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (aaaa bbbb cccc dd-) Tj 0 -12 Td (eeee ffff gggg hhh) Tj ET " +
            "BT /F1 10 Tf 135 250 Td (Next) Tj 0 -12 Td (column) Tj ET");
        PdfParagraphCandidate candidate = PdfParagraphEditor.FindParagraphs(source, 0).First();
        Assert.True(PdfParagraphEditor.DefaultBox(candidate, 300, 300, candidate.Text, Font()).Right < 135);
    }

    // Extraction reports both texts, so nothing else would notice one printed over the other. Text
    // painted after the paragraph is the cover check's, whichever side of it the column lies.
    [Theory]
    [InlineData(TwoColumns, "would cover")]
    [InlineData("BT /F1 10 Tf 182 250 Td (Next column words) Tj 0 -12 Td (and its second line) Tj ET " +
        "BT /F1 10 Tf 30 250 Td (Old line of text near the edge) Tj 0 -12 Td (and a second line of it here) Tj ET", "other text")]
    public void AnEditThatWouldLandOnOtherTextIsRefused(string content, string reason)
    {
        PdfDocument source = Raw(content);
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0),
            item => item.Text.StartsWith("Old", StringComparison.Ordinal));
        NotSupportedException refusal = Assert.ThrowsAny<NotSupportedException>(() => PdfParagraphEditor.Plan(source, 0,
            candidate.SourceRegion, new PdfContentBounds(30, 200, 290, 258), "A new line that runs right across the column beside it", Font(), 10, 12,
            PdfParagraphAlignment.Left));
        Assert.Contains(reason, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExtraRowThatWouldLandOnTheTextBelowIsRefused()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (Old line) Tj ET BT /F1 10 Tf 30 226 Td (The text below it) Tj ET");
        PdfParagraphCandidate candidate = PdfParagraphEditor.FindParagraphs(source, 0).First();
        Assert.ThrowsAny<NotSupportedException>(() => PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(30, 100, 60, 258), "AAAA BBBB CCCC DDDD EEEE FFFF", Font(), 10, 12, PdfParagraphAlignment.Left));
    }

    [Fact]
    public void AnEditThatStaysInItsOwnRoomIsNotRefused()
    {
        PdfDocument source = Raw(TwoColumns);
        PdfParagraphCandidate candidate = PdfParagraphEditor.FindParagraphs(source, 0).First();
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            new PdfContentBounds(30, 200, 170, 258), "A new line", Font(), 10, 12, PdfParagraphAlignment.Left);
        Assert.False(plan.HasOverflow, plan.OverflowReason);
    }

    // The word fits the box and only the indent is in its way.
    [Fact]
    public void ALongFirstWordIsNotSplitByTheIndent()
    {
        TrueTypeFont font = Font();
        const string word = "Electroencephalographically";
        double width = PdfTextReflow.Wrap(word, font, 10, 1000).Single().Width;
        PdfDocument source = Raw("BT /F1 10 Tf 60 250 Td (Old first line) Tj -30 -12 Td (Old body line of it here) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.Equal(30, candidate.FirstLineIndent, 6);
        var box = new PdfContentBounds(30, 200, 30 + width + 10, 258);
        PdfParagraphPlan plan = PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion, box, word + " measured", font, 10, 12,
            PdfParagraphAlignment.Left);
        Assert.False(plan.HasOverflow, plan.OverflowReason);
        Assert.Equal([word, "measured"], plan.Lines.Select(line => line.Text));
        Assert.Equal(30, plan.Lines[0].X, 6);
    }

    [Fact]
    public void ARejoinedWordDoesNotPushUnchangedTextOntoAnotherRow()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (aaaa bbbb cccc dd-) Tj 0 -12 Td (eeee ffff gggg hhh) Tj ET");
        PdfParagraphCandidate candidate = Assert.Single(PdfParagraphEditor.FindParagraphs(source, 0));
        Assert.Equal("aaaa bbbb cccc ddeeee ffff gggg hhh", candidate.Text);
        PdfParagraphPlan plan = Unchanged(source, candidate, Font());
        Assert.False(plan.HasOverflow, plan.OverflowReason);
        Assert.Equal(2, plan.Lines.Count);
    }

    [Fact]
    public void AHyphenStaysWhenThePageShowsTheCompoundWhole()
    {
        PdfDocument source = Raw("BT /F1 10 Tf 30 250 Td (It is well-) Tj 0 -12 Td (known, to all) Tj ET " +
            "BT /F1 10 Tf 30 100 Td (a well-known thing) Tj ET");
        Assert.Equal("It is well-known, to all", PdfParagraphEditor.FindParagraphs(source, 0).First().Text);
    }

    [Fact]
    public void TheRefusalOfATransformDoesNotBlameMixedStyles()
    {
        PdfParagraphInspection inspection = PdfParagraphEditor.Inspect(Raw("q 0.5 0 0 1 0 0 cm BT /F1 10 Tf 60 250 Td (OLD) Tj ET Q"),
            0, new PdfContentBounds(20, 220, 100, 280));
        Assert.False(inspection.CanEdit);
        Assert.DoesNotContain("mixed", inspection.Reason, StringComparison.OrdinalIgnoreCase);
    }

    private static PdfParagraphPlan Unchanged(PdfDocument source, PdfParagraphCandidate candidate, TrueTypeFont font) =>
        PdfParagraphEditor.Plan(source, 0, candidate.SourceRegion,
            PdfParagraphEditor.DefaultBox(candidate, 300, 300, candidate.Text, font), candidate.Text, font,
            candidate.FontSize, candidate.Leading, PdfParagraphAlignment.Left);

    // How far the strongest ink in an area is from gray, whichever order the channels come in.
    private static int Colorfulness(PdfRenderedPage page, int left, int bottom, int right, int top)
    {
        byte[] pixels = page.Pixels.ToArray();
        int result = 0;
        for (int y = page.Height - top; y < page.Height - bottom; y++)
            for (int x = left; x < right; x++)
            {
                int index = (y * page.Width + x) * 4;
                int[] channels = [pixels[index], pixels[index + 1], pixels[index + 2]];
                result = Math.Max(result, channels.Max() - channels.Min());
            }
        return result;
    }

    private const string LogoForm = "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Length 12 >>\nstream\n0 0 1 1 re f\nendstream";
    private const string FormWithOldText = "<< /Type /XObject /Subtype /Form /BBox [0 0 300 300] /Resources << /Font << /F1 4 0 R >> >> " +
        "/Length 39 >>\nstream\nBT /F1 10 Tf 30 250 Td (OLD) Tj ET     \nendstream";

    private static PdfDocument Raw(string content, string pageExtra = "", string catalogExtra = "", bool shared = false,
        string resources = "", string? extraObject = null, string fonts = "", string[]? extraObjects = null)
    {
        string page = $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] {pageExtra} /Resources << /Font << /F1 4 0 R /F2 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >> {fonts} >> {resources} >> /Contents 5 0 R >>";
        string[] objects = [$"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>",
            $"<< /Type /Pages /Kids [3 0 R{(shared ? " 6 0 R" : "")}] /Count {(shared ? 2 : 1)} >>", page,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream", page,
            .. extraObject is null ? Array.Empty<string>() : [extraObject], .. extraObjects ?? []];
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

    private static PdfContentStreamBuilder Rows(string[] rows)
    {
        PdfContentStreamBuilder content = new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 10).MoveText(30, 250);
        foreach (string row in rows)
        {
            if (row.Length != 0) content.ShowLatin1Text(row);
            content.MoveText(0, -12);
        }
        return content.EndText();
    }

    private static PdfDocument Pages(params string[][] pages)
    {
        var builder = new PdfDocumentBuilder();
        foreach (string[] rows in pages) builder.AddPage(300, 300, Rows(rows));
        return PdfDocument.Open(builder.Build());
    }

    private static PdfDocument Document() => PdfDocument.Open(new PdfDocumentBuilder()
        .AddPage(300, 300, new PdfContentStreamBuilder()
            .BeginText().SetFont(PdfStandardFont.Helvetica, 10).MoveText(30, 250)
            .ShowLatin1Text("OLD line").MoveText(0, -12).ShowLatin1Text("OLD next").EndText()
            .BeginText().SetFont(PdfStandardFont.Helvetica, 10).MoveText(200, 30).ShowLatin1Text("Footer").EndText())
        .AddPage(300, 300, new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 10)
            .MoveText(30, 250).ShowLatin1Text("Other page").EndText()).Build());
}
