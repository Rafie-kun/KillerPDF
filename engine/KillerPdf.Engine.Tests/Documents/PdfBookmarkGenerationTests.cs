using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfBookmarkGenerationTests
{
    [Fact]
    public void DetectHeadingsBuildsReviewableSizeBasedHierarchy()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(300, 400, new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.HelveticaBold, 22)
                .MoveText(30, 350).ShowLatin1Text("Chapter One").EndText()
                .BeginText().SetFont(PdfStandardFont.HelveticaBold, 16)
                .MoveText(30, 300).ShowLatin1Text("First Topic").EndText()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 10)
                .MoveText(30, 260).ShowLatin1Text("Body copy").EndText())
            .Build());

        IReadOnlyList<PdfBookmarkProposal> proposals =
            PdfBookmarkGeneration.DetectHeadings(document);

        Assert.Equal(["Chapter One", "First Topic"],
            proposals.Select(item => item.Title));
        Assert.Equal([0, 1], proposals.Select(item => item.Level));
        Assert.All(proposals, item =>
            Assert.Equal(PdfBookmarkProposalDecision.Pending, item.Decision));
        Assert.True(proposals[0].Bounds.Top > proposals[1].Bounds.Top);
    }

    [Fact]
    public void ApplyRequiresReviewAndAuthorsAcceptedVisibleLocations()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage().Build());
        PdfBookmarkProposal[] pending =
        [
            new()
            {
                Title = "Keep", PageIndex = 1, Level = 0,
                Bounds = new PdfContentBounds(10, 600, 100, 620),
                PointSize = 18
            }
        ];

        Assert.Throws<InvalidOperationException>(() =>
            PdfBookmarkGeneration.Apply(document, pending));

        byte[] output = PdfBookmarkGeneration.Apply(document,
        [
            pending[0] with { Decision = PdfBookmarkProposalDecision.Accepted },
            new PdfBookmarkProposal
            {
                Title = "Skip", PageIndex = 0, Level = 1,
                Bounds = new PdfContentBounds(10, 500, 100, 520),
                PointSize = 16,
                Decision = PdfBookmarkProposalDecision.Rejected
            }
        ]);
        PdfBookmarkInfo bookmark = Assert.Single(
            PdfBookmarkReader.Read(PdfDocument.Open(output)));

        Assert.Equal("Keep", bookmark.Title);
        Assert.Equal(1, bookmark.DestinationPageIndex);
        Assert.Equal(PdfDestinationKind.FitH, bookmark.Destination?.Kind);
        Assert.Equal(620, bookmark.Destination?.Values[0]);
    }

    [Fact]
    public void DetectionCanFilterByPageRegionAndTitlePattern()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(300, 400, new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.HelveticaBold, 18)
                .MoveText(30, 350).ShowLatin1Text("Chapter 1").EndText()
                .BeginText().SetFont(PdfStandardFont.HelveticaBold, 18)
                .MoveText(200, 200).ShowLatin1Text("Sidebar").EndText())
            .AddPage(300, 400, new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.HelveticaBold, 18)
                .MoveText(30, 350).ShowLatin1Text("Chapter 2").EndText())
            .Build());

        IReadOnlyList<PdfBookmarkProposal> proposals =
            PdfBookmarkGeneration.DetectHeadings(document,
                new PdfBookmarkDetectionOptions
                {
                    TitlePattern = "^Chapter [0-9]+$",
                    PageRegions = new Dictionary<int, PdfContentBounds>
                    {
                        [0] = new(0, 300, 160, 400)
                    }
                });

        Assert.Equal("Chapter 1", Assert.Single(proposals).Title);
        Assert.Throws<ArgumentException>(() => PdfBookmarkGeneration.DetectHeadings(
            document, new PdfBookmarkDetectionOptions { TitlePattern = "[" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfBookmarkGeneration.DetectHeadings(
            document, new PdfBookmarkDetectionOptions
            {
                PageRegions = new Dictionary<int, PdfContentBounds> { [2] = new(0, 0, 10, 10) }
            }));
    }

    [Fact]
    public void BoldNumberedHeadingsAtBodySizeTakeTheirLevelFromTheirNumber()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(400, 500, new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.HelveticaBold, 11)
                .MoveText(30, 460).ShowLatin1Text("1 Introduction").EndText()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 11)
                .MoveText(30, 440).ShowLatin1Text("This paragraph is ordinary body text, set in the regular face.").EndText()
                .BeginText().SetFont(PdfStandardFont.HelveticaBold, 11)
                .MoveText(30, 410).ShowLatin1Text("1.1 Scope").EndText()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 11)
                .MoveText(30, 390).ShowLatin1Text("More ordinary body text follows the second heading here.").EndText()
                .BeginText().SetFont(PdfStandardFont.HelveticaBold, 11)
                .MoveText(30, 360).ShowLatin1Text("1.1.1 Details").EndText()
                .BeginText().SetFont(PdfStandardFont.HelveticaBold, 11)
                .MoveText(30, 330).ShowLatin1Text("A bold sentence inside a paragraph ends with a period.").EndText())
            .Build());

        IReadOnlyList<PdfBookmarkProposal> proposals = PdfBookmarkGeneration.DetectHeadings(document);

        Assert.Equal(["1 Introduction", "1.1 Scope", "1.1.1 Details"], proposals.Select(item => item.Title));
        Assert.Equal([0, 1, 2], proposals.Select(item => item.Level));
        Assert.All(proposals, item => Assert.Equal(PdfBookmarkProposalSource.TextAppearance, item.Source));
        Assert.Empty(PdfBookmarkGeneration.DetectHeadings(document,
            new PdfBookmarkDetectionOptions { DetectStyledHeadings = false }));
    }

    [Fact]
    public void RunningHeadersAndPageNumbersAreLeftOut()
    {
        var builder = new PdfDocumentBuilder();
        for (int page = 1; page <= 4; page++)
        {
            var content = new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.HelveticaBold, 16)
                .MoveText(30, 470).ShowLatin1Text("Annual Report 2026").EndText()
                .BeginText().SetFont(PdfStandardFont.HelveticaBold, 16)
                .MoveText(190, 20).ShowLatin1Text(page.ToString()).EndText();
            if (page == 3)
                content.BeginText().SetFont(PdfStandardFont.HelveticaBold, 20)
                    .MoveText(30, 400).ShowLatin1Text("Results").EndText();
            builder.AddPage(400, 500, content);
        }

        IReadOnlyList<PdfBookmarkProposal> proposals =
            PdfBookmarkGeneration.DetectHeadings(PdfDocument.Open(builder.Build()));

        PdfBookmarkProposal only = Assert.Single(proposals);
        Assert.Equal("Results", only.Title);
        Assert.Equal(2, only.PageIndex);
    }

    [Fact]
    public void AWrappedTitleBecomesOneHeading()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(400, 500, new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.HelveticaBold, 20)
                .MoveText(30, 450).ShowLatin1Text("A Long Chapter Title That").EndText()
                .BeginText().SetFont(PdfStandardFont.HelveticaBold, 20)
                .MoveText(30, 426).ShowLatin1Text("Wraps Onto Two Lines").EndText()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 10)
                .MoveText(30, 380).ShowLatin1Text("Body copy").EndText())
            .Build());

        PdfBookmarkProposal heading = Assert.Single(PdfBookmarkGeneration.DetectHeadings(document));

        Assert.Equal("A Long Chapter Title That Wraps Onto Two Lines", heading.Title);
    }

    [Fact]
    public void TaggedHeadingsComeFromTheStructureTreeThroughItsRoleMap()
    {
        const string content =
            "/Title2 << /MCID 0 >> BDC BT /F1 12 Tf 72 700 Td (Tagged Title) Tj ET EMC " +
            "/P << /MCID 1 >> BDC BT /F1 12 Tf 72 680 Td (Body text) Tj ET EMC " +
            "BT /F1 24 Tf 72 600 Td (Big Decoy) Tj ET";
        PdfDocument document = PdfDocument.Open(CreatePdf(
        [
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /StructParents 0 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            Stream(content),
            "<< /Type /StructTreeRoot /K [7 0 R] /RoleMap << /Title2 /H2 >> >>",
            "<< /Type /StructElem /S /Sect /P 6 0 R /K [8 0 R 9 0 R] >>",
            "<< /Type /StructElem /S /Title2 /P 7 0 R /Pg 3 0 R /K 0 >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 1 >>"
        ]));

        PdfBookmarkProposal heading = Assert.Single(PdfBookmarkGeneration.DetectHeadings(document));

        Assert.Equal("Tagged Title", heading.Title);
        Assert.Equal(1, heading.Level);
        Assert.Equal(PdfBookmarkProposalSource.DocumentTags, heading.Source);
        Assert.Equal("Big Decoy", Assert.Single(PdfBookmarkGeneration.DetectHeadings(document,
            new PdfBookmarkDetectionOptions { UseDocumentTags = false })).Title);
    }

    [Fact]
    public void APageWithoutTextHasNoHeadings()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());

        Assert.Empty(PdfBookmarkGeneration.DetectHeadings(document));
    }

    private static string Stream(string content) =>
        $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static byte[] CreatePdf(IReadOnlyList<string> objects)
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
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
