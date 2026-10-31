using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using System.Text;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfContentRedactionSurvivorTests
{
    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
    private static readonly (byte, byte, byte) Blue = (0, 0, 255);
    private static readonly (byte, byte, byte) Secret = (0x53, 0x45, 0x43);

    [Fact]
    public void HiddenCopiesWithDifferentSoftMasksKeepTheirVisiblePixels()
    {
        byte[] rgb = HalfImage(20, 10, Blue, Secret);
        string Page(int content, int image) => $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /XObject << /I {image} 0 R >> >> /Contents {content} 0 R >>";
        PdfDocument source = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
            Page(6, 9), Page(7, 9), Page(8, 10),
            Stream("q 200 0 0 100 0 0 cm /I Do Q"),
            Stream("q 0 0 100 100 re W n 200 0 0 100 0 0 cm /I Do Q"),
            Stream("q 0 0 100 100 re W n 200 0 0 100 0 0 cm /I Do Q"),
            ImageObject(rgb, 20, 10, "/ColorSpace /DeviceRGB /SMask 11 0 R"),
            ImageObject(rgb, 20, 10, "/ColorSpace /DeviceRGB /SMask 12 0 R"),
            ImageObject(Enumerable.Repeat((byte)255, 200).ToArray(), 20, 10, "/ColorSpace /DeviceGray"),
            ImageObject(Enumerable.Repeat((byte)128, 200).ToArray(), 20, 10, "/ColorSpace /DeviceGray")
        ]));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(100, 0, 200, 100));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(source, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Equal(0, CountOriginalPixels(output, Secret));
        AssertRendersTheSame(source, output, 0, except: region.Bounds);
        AssertRendersTheSame(source, output, 1);
        AssertRendersTheSame(source, output, 2);
        PdfContentRedaction.VerifyRetainedContent(source, [region], output);
    }

    [Fact]
    public void ASpriteDrawHidesNoCoveredPixelsAndStillLooksTheSame()
    {
        byte[] image = HalfImage(40, 20, Blue, Secret);
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 300] /Resources << /XObject << /I 5 0 R >> >> /Contents 4 0 R >>",
            Stream("q 100 100 100 100 re W n 200 0 0 100 100 100 cm /I Do Q\nq 300 100 100 100 re W n 200 0 0 100 200 100 cm /I Do Q\n"),
            ImageObject(image, 40, 20)
        ]));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(290, 90, 410, 210));

        Assert.Empty(PdfContentRedaction.Analyze(document, [region]).RasterPages);
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Equal(0, CountOriginalPixels(output, Secret));
        AssertRendersTheSame(document, output, 0, except: region.Bounds);
    }

    [Fact]
    public void AFormDrawHidesNoCoveredGlyphsAndKeepsWhatItShows()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 300] /Resources << /XObject << /Fm 5 0 R >> >> /Contents 4 0 R >>",
            Stream("q 0 0 200 100 re W n /Fm Do Q\nq 300 100 200 100 re W n 1 0 0 1 100 100 cm /Fm Do Q\n"),
            Stream("BT /F 12 Tf 10 10 Td (PUBLIC) Tj ET BT /F 12 Tf 210 10 Td (FORMSECRET) Tj ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 500 100] /Resources << /Font << /F 6 0 R >> >>"),
            Helvetica
        ]));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(295, 95, 505, 205));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Equal("PUBLICPUBLIC", Letters(output, 0));
        Assert.DoesNotContain("FORMSECRET", AllStreamText(output));
        AssertRendersTheSame(document, output, 0, except: region.Bounds);
    }

    [Fact]
    public void DivideSpreadsImageLeavesNoCoveredPixelsBehindTheOtherPagesBox()
    {
        PdfDocument document = DividedSpread(ImageSpread());
        var region = new PdfRedactionRegion(1, new PdfContentBounds(50, 50, 250, 250));

        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(document, [region]);
        Assert.Empty(plan.RasterPages);
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Equal(0, ImagePixelsUnderRegion(output, 60, 30, 35, 5, 55, 25, Secret));
        AssertRendersTheSame(document, output, 0);
        // A sample that touches the region is painted whole, so the comparison stops one sample out.
        AssertRendersTheSame(document, output, 1, except: new PdfContentBounds(40, 40, 260, 260));
    }

    [Fact]
    public void DivideSpreadsTextLeavesNoCoveredGlyphsBehindTheOtherPagesBox()
    {
        PdfDocument document = DividedSpread(TextSpread());
        IReadOnlyList<PdfPageInformation> pages = PdfPageInformation.Read(document);
        var region = new PdfRedactionRegion(1, new PdfContentBounds(0, 0, pages[1].Width, pages[1].Height));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Contains("LEFTPAGE", Letters(output, 0));
        Assert.DoesNotContain("RIGHTSECRET", Letters(output, 0));
        Assert.DoesNotContain("RIGHTSECRET", Letters(output, 1));
        Assert.DoesNotContain("RIGHTSECRET", AllStreamText(output));
        AssertRendersTheSame(document, output, 0);
    }

    [Fact]
    public void RegionsOnBothHalvesOfADividedSpreadEachReachTheOtherHalf()
    {
        PdfDocument document = DividedSpread(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 300] /Resources << /Font << /F 6 0 R >> /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>",
            Stream("q 600 0 0 150 0 150 cm /Im0 Do Q BT /F 12 Tf 20 100 Td (LEFTSECRET KEEPLEFT) Tj ET BT /F 12 Tf 320 100 Td (RIGHTSECRET KEEPRIGHT) Tj ET"),
            ImageObject(HalfImage(60, 30, Secret, Secret), 60, 30),
            Helvetica
        ]));
        PdfRedactionRegion[] regions =
        [
            new PdfRedactionRegion(0, new PdfContentBounds(10, 90, 95, 115)),
            new PdfRedactionRegion(1, new PdfContentBounds(10, 90, 105, 115)),
            new PdfRedactionRegion(0, new PdfContentBounds(50, 200, 100, 250)),
            new PdfRedactionRegion(1, new PdfContentBounds(50, 200, 100, 250))
        ];

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, regions);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        string text = AllStreamText(output);
        Assert.DoesNotContain("LEFTSECRET", text);
        Assert.DoesNotContain("RIGHTSECRET", text);
        Assert.Contains("KEEPLEFT", Letters(output, 0));
        Assert.Contains("KEEPRIGHT", Letters(output, 1));
        // Rows 10 to 20 of the image lie under both regions, in columns 5 to 10 and 35 to 40.
        Assert.Equal(0, ImagePixelsUnderRegion(output, 60, 30, 5, 11, 10, 19, Secret));
        Assert.Equal(0, ImagePixelsUnderRegion(output, 60, 30, 35, 11, 40, 19, Secret));
    }

    [Fact]
    public void AnIndexedSpreadThatCannotBePaintedAsksForTheOtherHalfAsAnImage()
    {
        byte[] indexes = new byte[60 * 30];
        for (int y = 0; y < 30; y++)
            for (int x = 30; x < 60; x++) indexes[y * 60 + x] = 1;
        PdfDocument document = DividedSpread(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 300] /Resources << /Font << /F 6 0 R >> /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>",
            Stream("q 600 0 0 300 0 0 cm /Im0 Do Q BT 3 Tr /F 12 Tf 20 100 Td (LEFTPAGE) Tj ET BT 3 Tr /F 12 Tf 320 100 Td (RIGHTSECRET) Tj ET"),
            ImageObject(indexes, 60, 30, "/ColorSpace [/Indexed /DeviceRGB 1 <0000FF534543>]"),
            Helvetica
        ]));
        var region = new PdfRedactionRegion(1, new PdfContentBounds(0, 0, 300, 300));

        PdfRedactionPageAnalysis page = Assert.Single(PdfContentRedaction.Analyze(document, [region]).Pages,
            item => item.PageIndex == 0);
        Assert.Equal(PdfRedactionPageMethod.Raster, page.Method);
        Assert.Equal([PdfContentRedaction.SharedContentReason], page.RasterReasons);
        Assert.Throws<InvalidOperationException>(() => PdfContentRedaction.Apply(document, [region]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ApprovedRasterPages = [0], RasterDpi = 72 });

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.DoesNotContain(Streams(output), stream => IsImage(stream) && Pixels(stream) == (60, 30));
        Assert.Contains("LEFTPAGE", Letters(output, 0));
        Assert.DoesNotContain("RIGHTSECRET", AllStreamText(output));
        Assert.Equal((0, 0, 255), Pixel(new PdfPageRenderer(output).Render(0, new PdfRenderOptions(300, 300)), 150, 150));
    }

    [Fact]
    public void ALogoDrawnInFullOnAnotherPageStaysUntouchedAndPasses()
    {
        byte[] logo = HalfImage(40, 20, Blue, Secret);
        PdfDocument document = PdfDocument.Open(TwoPages("q 100 0 0 50 100 100 cm /I Do Q", "q 200 0 0 100 100 100 cm /I Do Q", logo));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(90, 90, 210, 160));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Equal(20 * 20, CountOriginalPixels(output, Secret));
        Assert.Equal(1, Streams(output).Count(IsImage));
        AssertRendersTheSame(document, output, 1);
        PdfContentRedaction.VerifyRetainedContent(document, [region], output);
    }

    [Theory]
    [InlineData(150)]
    [InlineData(152.5)]
    public void APartlyVisibleSecondDrawKeepsEveryPixelItShows(double clipWidth)
    {
        PdfDocument document = PdfDocument.Open(TwoPages(
            "q 300 100 100 100 re W n 200 0 0 100 200 100 cm /I Do Q",
            $"q 100 100 {clipWidth} 100 re W n 200 0 0 100 100 100 cm /I Do Q",
            HalfImage(40, 20, Blue, Secret)));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(290, 90, 410, 210));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        // The image's 40 columns are 5 units wide, so the clip ends at the edge of column 30, or inside it.
        int shownColumns = clipWidth == 150 ? 10 : 11;
        Assert.Equal(shownColumns * 20, CountOriginalPixels(output, Secret));
        AssertRendersTheSame(document, output, 1);
    }

    // The clip shows the half of the image next to the page's bottom edge, which is the left half
    // of the image after one quarter turn and the covered right half after the opposite one.
    [Theory]
    [InlineData("0 200 -100 0 200 100", 0)]
    [InlineData("0 -200 100 0 100 300", 20 * 20)]
    public void AQuarterTurnedDrawKeepsWhatItShows(string matrix, int coveredPixelsShown)
    {
        PdfDocument document = PdfDocument.Open(TwoPages(
            "q 300 100 100 100 re W n 200 0 0 100 200 100 cm /I Do Q",
            $"q 100 100 100 100 re W n {matrix} cm /I Do Q",
            HalfImage(40, 20, Blue, Secret)));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(290, 90, 410, 210));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        AssertRendersTheSame(document, output, 1);
        Assert.Equal(coveredPixelsShown, CountOriginalPixels(output, Secret));
    }

    [Fact]
    public void AnObliquelyTurnedDrawLosesOnlyTheCoveredSamplesItHides()
    {
        // Turned 30 degrees; the clip cuts across the image, so part of the covered half shows.
        PdfDocument document = PdfDocument.Open(TwoPages(
            "q 300 100 100 100 re W n 200 0 0 100 200 100 cm /I Do Q",
            "q 100 100 170 200 re W n 173.205 100 -50 86.603 120 100 cm /I Do Q",
            HalfImage(40, 20, Blue, Secret)));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(290, 90, 410, 210));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        AssertRendersTheSame(document, output, 1);
        int left = CountOriginalPixels(output, Secret);
        Assert.InRange(left, 1, 20 * 20 - 1);
    }

    [Fact]
    public void AClipThatIsNotARectangleOverCoveredSamplesAsksForAnImage()
    {
        PdfDocument document = PdfDocument.Open(TwoPages(
            "q 300 100 100 100 re W n 200 0 0 100 200 100 cm /I Do Q",
            "q 100 100 m 300 100 l 200 200 l h W n 200 0 0 100 100 100 cm /I Do Q",
            HalfImage(40, 20, Blue, Secret)));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(290, 90, 410, 210));

        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(document, [region]);

        Assert.Equal([1], plan.RasterPages);
        Assert.Equal([PdfContentRedaction.SharedContentReason], plan.Pages.Single(page => page.PageIndex == 1).RasterReasons);
    }

    [Fact]
    public void AnImageUnderARegionNeedsNoPageImageForItsOwnCurvedClip()
    {
        PdfDocument document = PdfDocument.Open(TwoPages(
            "q 100 100 m 300 100 l 200 200 l h W n 200 0 0 100 100 100 cm /I Do Q", "0 0 m",
            HalfImage(40, 20, Blue, Secret)));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(180, 110, 220, 150));

        Assert.Empty(PdfContentRedaction.Analyze(document, [region]).RasterPages);
        Assert.True(PdfContentRedaction.Apply(document, [region]).Verification.Succeeded);
    }

    [Fact]
    public void APathWhollyBehindTheOtherPagesBoxGoesAndOneAcrossItAsksForAnImage()
    {
        const string text = "BT /F 12 Tf 320 100 Td (RIGHTSECRET) Tj ET ";
        PdfDocument hidden = DividedSpread(SpreadWithContent(text + "1 0 0 rg 320 200 100 20 re f"));
        PdfDocument crossing = DividedSpread(SpreadWithContent(text + "1 0 0 RG 10 200 m 590 200 l S"));
        var whole = new PdfRedactionRegion(1, new PdfContentBounds(0, 0, 300, 300));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(hidden, [whole]);
        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(crossing, [whole]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Empty(new PdfPageContentReader(output).Read(0).Paths);
        AssertRendersTheSame(hidden, output, 0);
        Assert.Contains(0, plan.RasterPages);
        Assert.Contains(PdfContentRedaction.SharedContentReason, plan.Pages.Single(page => page.PageIndex == 0).RasterReasons);
    }

    [Fact]
    public void VerificationFindsAHiddenCoveredDrawTheRewriteDidNotTake()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 300] /Resources << /XObject << /I 5 0 R >> >> /Contents 4 0 R >>",
            Stream("q 100 100 100 100 re W n 200 0 0 100 100 100 cm /I Do Q\nq 300 100 100 100 re W n 200 0 0 100 200 100 cm /I Do Q\n"),
            ImageObject(HalfImage(40, 20, Blue, Secret), 40, 20)
        ]));

        var error = Assert.Throws<PdfRedactionVerificationException>(() => PdfContentRedaction.VerifyRetainedContent(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(290, 90, 410, 210))], document));

        PdfRedactionVerificationFinding finding = Assert.Single(error.Report.Findings);
        Assert.Equal("RetainedHiddenContent", finding.Code);
        Assert.Equal(0, finding.PageIndex);
    }

    // The form's covered line lies in the bounds of the triangle on page 2 but outside its area.
    private static byte[] FormDrawnUnderATriangleClip() => CreatePdf([
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm 6 0 R >> >> /Contents 5 0 R >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm 6 0 R >> >> /Contents 8 0 R >>",
        Stream("/Fm Do"),
        Stream("BT /F1 12 Tf 100 600 Td (SECRETTEXT) Tj ET\nBT /F1 12 Tf 100 300 Td (PUBLICTEXT) Tj ET",
            "/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >> >>"),
        Helvetica,
        Stream("q 50 200 m 400 200 l 50 650 l h W n /Fm Do Q")
    ]);

    [Fact]
    public void APageImageLeavesNoCoveredTextInItsInvisibleLayer()
    {
        PdfDocument document = PdfDocument.Open(FormDrawnUnderATriangleClip());
        var region = new PdfRedactionRegion(0, new PdfContentBounds(90, 590, 300, 620));

        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(document, [region]);
        Assert.Equal([1], plan.RasterPages);
        Assert.Equal([PdfContentRedaction.SharedContentReason], plan.Pages.Single(page => page.PageIndex == 1).RasterReasons);
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ApprovedRasterPages = [1], RasterDpi = 72 });

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.True(result.Verification.Succeeded);
        Assert.DoesNotContain("SECRETTEXT", Letters(output, 1), StringComparison.Ordinal);
        Assert.Contains("PUBLICTEXT", Letters(output, 1), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETTEXT", AllStreamText(output), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRETTEXT", Encoding.Latin1.GetString(result.Document.Span), StringComparison.Ordinal);
    }

    [Fact]
    public void APageImageDropsALetterTheClipCutsOverACoveredArea()
    {
        // Page 2 shows the form up to x=140, which cuts through the covered word.
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm 6 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm 6 0 R >> >> /Contents 8 0 R >>",
            Stream("/Fm Do"),
            Stream("BT /F1 12 Tf 100 600 Td (SECRETTEXT) Tj ET\nBT /F1 12 Tf 100 300 Td (PUBLICTEXT) Tj ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >> >>"),
            Helvetica,
            Stream("q 0 0 139 792 re W n /Fm Do Q")
        ]));
        PdfRedactionRegion[] regions =
        [
            new PdfRedactionRegion(0, new PdfContentBounds(90, 590, 300, 620)),
            new PdfRedactionRegion(1, new PdfContentBounds(10, 10, 20, 20))
        ];
        PdfContentBounds[] before = [.. new PdfPageContentReader(document).Read(1).Letters
            .Where(letter => letter.BoundingBox.Bottom > 500 && letter.BoundingBox.Left < 139 && letter.BoundingBox.Right > 139)
            .Select(letter => letter.BoundingBox)];
        Assert.Single(before);

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, regions,
            new PdfContentRedactionOptions { ApprovedRasterPages = [1], RasterDpi = 72 });

        Assert.True(result.Verification.Succeeded);
        Assert.DoesNotContain(new PdfPageContentReader(PdfDocument.Open(result.Document)).Read(1).Letters,
            letter => letter.BoundingBox.Bottom > 500 && letter.BoundingBox.Right > 139);
    }

    private static byte[] RasterPageWithText(string text) => CreatePdf([
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> /XObject << /KPRedactImage 6 0 R >> >> /Contents 4 0 R >>",
        Stream($"BT 3 Tr /F1 12 Tf 100 600 Td ({text}) Tj ET q 612 0 0 792 0 0 cm /KPRedactImage Do Q"),
        Helvetica,
        ImageObject(new byte[3], 1, 1)
    ]);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void VerificationRefusesAnInvisibleLayerThatKeepsWhatAPageHid(bool rectangleClip, bool clipCutsLetters)
    {
        // A letter a draw shows whole is the user's own copy; the rest of the area it hid must be gone.
        var hidden = new PdfContentRedaction.HiddenArea(new PdfContentBounds(90, 590, 300, 620),
            new PdfContentBounds(0, 0, clipCutsLetters ? 130 : 612, 792), rectangleClip);

        PdfRedactionVerificationReport report = PdfContentRedaction.Verify(RasterPageWithText("SECRETTEXT"), [], [0], null, false,
            new Dictionary<int, PdfContentRedaction.HiddenArea[]> { [0] = [hidden] });

        Assert.Equal(!rectangleClip || clipCutsLetters, report.Findings.Any(finding => finding.Code == "TextInRegion"));
    }

    [Fact]
    public void ASoftMaskIsPaintedWithTheHiddenSamples()
    {
        byte[] alpha = new byte[40 * 20];
        Array.Fill(alpha, (byte)0x80);
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 300] /Resources << /XObject << /I 7 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 300] /Resources << /XObject << /I 7 0 R >> >> /Contents 6 0 R >>",
            Stream("q 300 100 100 100 re W n 200 0 0 100 200 100 cm /I Do Q"),
            Stream("q 100 100 100 100 re W n 200 0 0 100 100 100 cm /I Do Q"),
            ImageObject(HalfImage(40, 20, Blue, Secret), 40, 20, "/ColorSpace /DeviceRGB /SMask 8 0 R"),
            ImageObject(alpha, 40, 20, "/ColorSpace /DeviceGray")
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(290, 90, 410, 210))]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Equal(0, CountOriginalPixels(output, Secret));
        byte[] mask = Streams(output).Where(IsImage).Select(stream => output.DecodeStream(stream))
            .Single(samples => samples.Length == alpha.Length);
        Assert.Equal(20 * 20, mask.Count(sample => sample == 0x80));
        AssertRendersTheSame(document, output, 1);
    }

    [Fact]
    public void PagesWithTheSameBytesAndTheirOwnImagesAreNotCopiesOfEachOther()
    {
        // Each page draws its own /Im0 with the same operators, as scanned documents do.
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /XObject << /Im0 7 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 150 300] /Resources << /XObject << /Im0 8 0 R >> >> /Contents 6 0 R >>",
            Stream("q 300 0 0 300 0 0 cm /Im0 Do Q"),
            Stream("q 300 0 0 300 0 0 cm /Im0 Do Q"),
            ImageObject(HalfImage(30, 30, Blue, Secret), 30, 30),
            ImageObject(HalfImage(31, 31, (1, 2, 3), (4, 5, 6)), 31, 31)
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(200, 100, 280, 200))]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Empty(result.Plan.RasterPages);
        Assert.Equal(16 * 31, CountOriginalPixels(output, (4, 5, 6)));
    }

    [Fact]
    public void PagesThatHideTheSameSamplesShareOneCopy()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 300] /Resources 9 0 R /Contents 6 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 300] /Resources 9 0 R /Contents 7 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 300] /Resources 9 0 R /Contents 7 0 R >>",
            Stream("q 300 100 100 100 re W n 200 0 0 100 200 100 cm /I Do Q"),
            Stream("q 100 100 100 100 re W n 200 0 0 100 100 100 cm /I Do Q"),
            ImageObject(HalfImage(40, 20, Blue, Secret), 40, 20),
            "<< /XObject << /I 8 0 R >> >>"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(290, 90, 410, 210))]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Equal(1, Streams(output).Count(IsImage));
        Assert.Equal(0, CountOriginalPixels(output, Secret));
        AssertRendersTheSame(document, output, 1);
        AssertRendersTheSame(document, output, 2);
    }

    [Fact]
    public void ADrawOutsideThePageLeavesInsteadOfAskingForAnImage()
    {
        byte[] indexes = new byte[40 * 20];
        PdfDocument document = PdfDocument.Open(TwoPages("q 100 0 0 50 100 100 cm /I Do Q", "q 100 0 0 50 900 100 cm /I Do Q",
            indexes, "/ColorSpace [/Indexed /DeviceRGB 1 <0000FF534543>]"));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(90, 90, 210, 160))]);

        Assert.True(result.Verification.Succeeded);
        Assert.Empty(result.Plan.RasterPages);
        Assert.Equal(0, Streams(PdfDocument.Open(result.Document)).Count(IsImage));
    }

    [Fact]
    public void AGlyphHiddenFromASharedPageTakesItsStructureDescriptionWithIt()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 7 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 300] /CropBox [0 0 300 300] /Resources << /Font << /F 6 0 R >> >> /Contents 5 0 R /StructParents 0 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 300] /CropBox [300 0 600 300] /Resources << /Font << /F 6 0 R >> >> /Contents 5 0 R /StructParents 1 >>",
            Stream("/P << /MCID 0 >> BDC BT /F 12 Tf 320 100 Td (RIGHTSECRET) Tj ET EMC"),
            Helvetica,
            "<< /Type /StructTreeRoot /K [8 0 R] >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /ActualText (ELEMENTSECRET) /K [0] >>"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(1, new PdfContentBounds(0, 80, 299, 130))]);

        Assert.True(result.Verification.Succeeded);
        byte[] bytes = result.Document.ToArray();
        PdfDocument output = PdfDocument.Open(bytes);
        Assert.DoesNotContain("RIGHTSECRET", Letters(output, 0));
        Assert.DoesNotContain("ELEMENTSECRET", Encoding.Latin1.GetString(bytes) + AllStreamText(output));
    }

    [Fact]
    public void AHiddenFormImageIsReachedThroughTheFormThatDrawsIt()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 300] /Resources 9 0 R /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 300] /Resources 9 0 R /Contents 6 0 R >>",
            Stream("q 300 100 100 100 re W n 1 0 0 1 200 100 cm /Fm Do Q"),
            Stream("q 100 100 100 100 re W n 1 0 0 1 100 100 cm /Fm Do Q"),
            Stream("q 200 0 0 100 0 0 cm /I Do Q", "/Type /XObject /Subtype /Form /BBox [0 0 200 100] /Resources << /XObject << /I 8 0 R >> >>"),
            ImageObject(HalfImage(40, 20, Blue, Secret), 40, 20),
            "<< /XObject << /Fm 7 0 R >> >>"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(290, 90, 410, 210))]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Equal(0, CountOriginalPixels(output, Secret));
        AssertRendersTheSame(document, output, 1);
    }

    [Fact]
    public void DivideSpreadsCopiesOfAFormLoseTheHiddenGlyphsOnBothPages()
    {
        PdfDocument document = DividedSpread(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 300] /Resources << /XObject << /Fm 5 0 R >> >> /Contents 4 0 R >>",
            Stream("/Fm Do"),
            Stream("BT /F 12 Tf 20 100 Td (LEFTPAGE) Tj ET BT /F 12 Tf 320 100 Td (RIGHTSECRET) Tj ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 600 300] /Resources << /Font << /F 6 0 R >> >>"),
            Helvetica
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(1, new PdfContentBounds(0, 80, 300, 130))]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.DoesNotContain("RIGHTSECRET", AllStreamText(output));
        Assert.Contains("LEFTPAGE", Letters(output, 0));
        AssertRendersTheSame(document, output, 0);
    }

    [Fact]
    public void AGlyphTheOtherDrawShowsStaysEvenWhenItCrossesTheEdge()
    {
        PdfDocument document = DividedSpread(SpreadWithContent("BT /F 40 Tf 280 100 Td (WW) Tj ET"));
        var region = new PdfRedactionRegion(1, new PdfContentBounds(0, 0, 300, 300));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Contains("W", Letters(output, 0));
        AssertRendersTheSame(document, output, 0);
    }

    [Fact]
    public void AnAnnotationPastTheOtherPagesBoxLeavesWithTheRegionOverIt()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 300] /CropBox [0 0 300 300] /Resources << /Font << /F 6 0 R >> >> /Contents 5 0 R /Annots [7 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 300] /CropBox [300 0 600 300] /Resources << /Font << /F 6 0 R >> >> /Contents 5 0 R /Annots [8 0 R] >>",
            Stream("BT /F 12 Tf 320 100 Td (RIGHTSECRET) Tj ET"),
            Helvetica,
            "<< /Type /Annot /Subtype /Text /Rect [350 150 370 170] /Contents (NOTESECRET) /P 3 0 R >>",
            "<< /Type /Annot /Subtype /Text /Rect [350 150 370 170] /Contents (NOTESECRET) /P 4 0 R >>"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(1, new PdfContentBounds(0, 0, 300, 300))]);

        Assert.True(result.Verification.Succeeded);
        Assert.DoesNotContain("NOTESECRET", Encoding.Latin1.GetString(result.Document.Span));
    }

    [Fact]
    public void AnAnnotationOnlyChangeKeepsThePagesInheritedResources()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /Resources << /Font << /F 6 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 300] /CropBox [0 0 300 300] /Contents 5 0 R /Annots [7 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 300] /CropBox [300 0 600 300] /Contents 5 0 R >>",
            Stream("BT /F 12 Tf 20 100 Td (LEFTPAGE) Tj ET"),
            Helvetica,
            "<< /Type /Annot /Subtype /Text /Rect [350 150 370 170] /Contents (NOTESECRET) /P 3 0 R >>"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(1, new PdfContentBounds(0, 0, 300, 300))]);

        Assert.True(result.Verification.Succeeded);
        Assert.DoesNotContain("NOTESECRET", Encoding.Latin1.GetString(result.Document.Span));
        Assert.Equal("LEFTPAGE", Letters(PdfDocument.Open(result.Document), 0));
    }

    [Fact]
    public void ADownscaledDrawChangesNothingBeyondThePixelsAtTheEdgeOfWhatItShows()
    {
        // Four samples to a unit, rendered at two pixels to a unit. The renderer's filter reads the
        // sample just past the edge of the clip, so the last column of pixels can blend with the fill.
        var samples = new byte[400 * 200 * 3];
        for (int offset = 0; offset < samples.Length; offset += 3)
            (samples[offset], samples[offset + 1], samples[offset + 2]) = ((byte)(offset / 3 % 400 / 2), (byte)(offset / 3 / 400), 200);
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 100] /Resources 8 0 R /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 100] /Resources 8 0 R /Contents 6 0 R >>",
            Stream("q 50 0 50 100 re W n 100 0 0 50 0 0 cm /I Do Q"),
            Stream("q 0 0 50 100 re W n 100 0 0 50 0 0 cm /I Do Q"),
            ImageObject(samples, 400, 200),
            "<< /XObject << /I 7 0 R >> >>"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(45, 0, 105, 100))]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Equal(0, ImagePixelsUnderRegion(output, 400, 200, 200, 0, 400, 200, (200, 100, 200)));
        Assert.NotEqual(samples, output.DecodeStream(Assert.Single(Streams(output), IsImage)));
        AssertRendersTheSame(document, output, 1, except: new PdfContentBounds(49, 0, 300, 100));
    }

    [Theory]
    [InlineData("q 0.866 0.5 -0.5 0.866 200 50 cm /Fm Do Q")]
    [InlineData("q 1 0 0 1 100 100 cm /Fm Do Q")]
    public void AFormWithACurvedClipOfItsOwnDrawnInFullElsewhereStaysUntouched(string draw)
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 300] /Resources 9 0 R /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 300] /Resources 9 0 R /Contents 6 0 R >>",
            Stream("q 1 0 0 1 100 100 cm /Fm Do Q"),
            Stream(draw),
            Stream("0 0 m 200 0 l 200 50 200 100 100 100 c 0 100 l h W n BT /F 12 Tf 20 40 Td (STAMPWORDS) Tj ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 200 100] /Resources << /Font << /F 8 0 R >> >>"),
            Helvetica,
            "<< /XObject << /Fm 7 0 R >> >>"
        ]));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(90, 90, 310, 210));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        Assert.Empty(result.Plan.RasterPages);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Equal("STAMPWORDS", Letters(output, 1));
        AssertRendersTheSame(document, output, 1);
    }

    [Fact]
    public void AClipInsideADividedSpreadDoesNotStopItsHiddenTextLeaving()
    {
        PdfDocument document = DividedSpread(SpreadWithContent(
            "q 0 0 m 600 0 l 600 300 300 300 0 300 c h W n BT /F 12 Tf 20 100 Td (LEFTPAGE) Tj ET BT /F 12 Tf 320 100 Td (RIGHTSECRET) Tj ET Q"));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(1, new PdfContentBounds(0, 0, 300, 300))]);

        Assert.True(result.Verification.Succeeded);
        Assert.Empty(result.Plan.RasterPages);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.DoesNotContain("RIGHTSECRET", AllStreamText(output));
        Assert.Contains("LEFTPAGE", Letters(output, 0));
    }

    private static byte[] SpreadWithContent(string content) => CreatePdf([
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 300] /Resources << /Font << /F 5 0 R >> >> /Contents 4 0 R >>",
        Stream(content),
        Helvetica
    ]);

    private static byte[] TwoPages(string first, string second, byte[] image, string entries = "/ColorSpace /DeviceRGB") => CreatePdf([
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 300] /Resources 8 0 R /Contents 5 0 R >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 500 300] /Resources 8 0 R /Contents 6 0 R >>",
        Stream(first),
        Stream(second),
        ImageObject(image, 40, 20, entries),
        "<< /XObject << /I 7 0 R >> >>"
    ]);

    private static (long, long) Pixels(PdfStream image) => (
        ((PdfInteger)image.Dictionary[new PdfName("Width"u8)]).Value,
        ((PdfInteger)image.Dictionary[new PdfName("Height"u8)]).Value);

    private static (byte Red, byte Green, byte Blue) Pixel(PdfRenderedPage page, int x, int y)
    {
        int offset = (y * page.Width + x) * 4;
        ReadOnlySpan<byte> pixels = page.Pixels.Span;
        return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    private static byte[] ImageSpread() => CreatePdf([
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 300] /Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>",
        Stream("q 600 0 0 300 0 0 cm /Im0 Do Q"),
        ImageObject(HalfImage(60, 30, Blue, Secret), 60, 30)
    ]);

    private static byte[] TextSpread() => CreatePdf([
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 300] /Resources << /Font << /F 5 0 R >> >> /Contents 4 0 R >>",
        Stream("BT /F 12 Tf 20 100 Td (LEFTPAGE) Tj ET BT /F 12 Tf 320 100 Td (RIGHTSECRET) Tj ET"),
        Helvetica
    ]);

    private static PdfDocument DividedSpread(byte[] spread) => PdfDocument.Open(PdfSpreadDivider.Divide(
        PdfDocument.Open(spread), [new PdfSpreadDivisionRequest(0, PdfSpreadDivisionDirection.Vertical)]).Document);

    // Pixels of an RGB image, left half one color and right half another.
    private static byte[] HalfImage(int width, int height, (byte, byte, byte) left, (byte, byte, byte) right)
    {
        var samples = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                (samples[(y * width + x) * 3], samples[(y * width + x) * 3 + 1], samples[(y * width + x) * 3 + 2]) =
                    x < width / 2 ? left : right;
        return samples;
    }

    private static string ImageObject(byte[] samples, int width, int height, string entries = "/ColorSpace /DeviceRGB") =>
        $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} {entries} /BitsPerComponent 8 /Length {samples.Length} >>\nstream\n"
            + Encoding.Latin1.GetString(samples) + "\nendstream";

    private static IEnumerable<PdfStream> Streams(PdfDocument document)
    {
        foreach (PdfCrossReferenceEntry entry in document.CrossReferences.Values)
            if (entry.Type is PdfCrossReferenceEntryType.InUse or PdfCrossReferenceEntryType.Compressed
                && document.Resolve(entry.ObjectNumber) is PdfStream stream) yield return stream;
    }

    private static bool IsImage(PdfStream stream) => stream.Dictionary.ContainsKey(new PdfName("Width"u8));

    // Every pixel of that color in every image of the file.
    private static int CountOriginalPixels(PdfDocument document, (byte Red, byte Green, byte Blue) color)
    {
        int count = 0;
        foreach (PdfStream image in Streams(document).Where(IsImage))
        {
            byte[] samples = document.DecodeStream(image);
            for (int offset = 0; offset + 2 < samples.Length; offset += 3)
                if ((samples[offset], samples[offset + 1], samples[offset + 2]) == color) count++;
        }
        return count;
    }

    // Pixels of that color inside a rectangle of samples, in every image of that size.
    private static int ImagePixelsUnderRegion(PdfDocument document, int width, int height, int left, int top,
        int right, int bottom, (byte Red, byte Green, byte Blue) color)
    {
        int count = 0;
        foreach (PdfStream image in Streams(document).Where(IsImage))
        {
            byte[] samples = document.DecodeStream(image);
            if (samples.Length != width * height * 3) continue;
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                {
                    int offset = (y * width + x) * 3;
                    if ((samples[offset], samples[offset + 1], samples[offset + 2]) == color) count++;
                }
        }
        return count;
    }

    private static string AllStreamText(PdfDocument document) => string.Concat(Streams(document).Where(stream => !IsImage(stream))
        .Select(stream => Encoding.Latin1.GetString(document.DecodeStream(stream))));

    private static string Letters(PdfDocument document, int page) =>
        string.Concat(new PdfPageContentReader(document).Read(page).Letters.Select(letter => letter.Value));

    // Renders at twice the page size and compares every pixel, leaving out a region given in page units.
    private static void AssertRendersTheSame(PdfDocument before, PdfDocument after, int page, PdfContentBounds? except = null)
    {
        PdfPageInformation info = PdfPageInformation.Read(before)[page];
        Assert.Equal(info, PdfPageInformation.Read(after)[page]);
        int width = (int)Math.Round(info.Width * 2), height = (int)Math.Round(info.Height * 2);
        PdfRenderedPage first = new PdfPageRenderer(before).Render(page, new PdfRenderOptions(width, height));
        PdfRenderedPage second = new PdfPageRenderer(after).Render(page, new PdfRenderOptions(width, height));
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
            {
                if (except is PdfContentBounds area && x >= area.Left * 2 - 2 && x <= area.Right * 2 + 2
                    && first.Height - y >= area.Bottom * 2 - 2 && first.Height - y <= area.Top * 2 + 2) continue;
                int offset = (y * first.Width + x) * 4;
                Assert.True(first.Pixels.Span.Slice(offset, 4).SequenceEqual(second.Pixels.Span.Slice(offset, 4)),
                    $"Page {page + 1} changed at pixel ({x}, {y}).");
            }
    }

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
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
