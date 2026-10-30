using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

public sealed class PdfOptimizationResourceTests
{
    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string Pages = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
    private const string Type3WithoutResources =
        "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 100 100] /FontMatrix [0.01 0 0 0.01 0 0] "
        + "/Encoding << /Type /Encoding /Differences [65 /A] >> /FirstChar 65 /LastChar 65 "
        + "/Widths [100] /CharProcs << /A 6 0 R >> >>";
    private const string Word = " BT /H 12 Tf 10 150 Td (WORD) Tj ET";
    private const string PaintImage = "q 100 0 0 100 0 0 cm /Im Do Q";

    [Theory]
    [InlineData("type3 selected by Tf")]
    [InlineData("type3 selected by gs")]
    [InlineData("tiling pattern")]
    [InlineData("soft mask group")]
    [InlineData("annotation appearance")]
    [InlineData("form drawing a type3 font")]
    public void ReduceKeepsTheInkOfContentThatPaintsWithThePageResources(string route)
    {
        PdfDocument source = PdfDocument.Open(InheritingFixture(route));
        byte[] before = Render(source);
        Assert.Contains(before.Chunk(4), IsDark);

        PdfOptimizationResult result = Reduce(source);

        Assert.Equal(before, Render(PdfDocument.Open(result.Data)));
    }

    [Fact]
    public void AnInheritingPageIsLeftOutOfTheResourceCleanup()
    {
        PdfDocument source = PdfDocument.Open(InheritingFixture("type3 selected by Tf"));

        Assert.Empty(PdfOptimizer.UnusedResourcePages(source));
    }

    [Fact]
    public void APageWhoseContentCarriesItsOwnResourcesIsStillPruned()
    {
        string font = Type3WithoutResources.Replace("/CharProcs", "/Resources << /XObject << /Im 7 0 R >> >> /CharProcs");
        PdfDocument source = PdfDocument.Open(Build([Catalog, Pages,
            Page("/Font << /F1 5 0 R /H 8 0 R /Unused 8 0 R >> /XObject << /Spare 7 0 R >>"),
            Stream("BT /F1 100 Tf 0 0 Td (A) Tj ET" + Word), font,
            Stream("100 0 d0 " + PaintImage), Image(0), Helvetica]));
        byte[] before = Render(source);

        PdfOptimizationResult result = Reduce(source);
        PdfDocument output = PdfDocument.Open(result.Data);

        Assert.Contains(PdfOptimizationChangeKind.PruneUnusedPageResources, result.Changes);
        Assert.Equal(before, Render(output));
        PdfDictionary resources = Resources(output, 0);
        Assert.False(resources.ContainsKey(Name("XObject")));
        Assert.False(Category(output, resources, "Font").ContainsKey(Name("Unused")));
    }

    // Device color operators paint through a default color space, and no operator names it.
    [Fact]
    public void ReduceKeepsTheDefaultColorSpaceDeviceColorsPaintThrough()
    {
        PdfDocument source = PdfDocument.Open(Build([Catalog, Pages,
            Page("/ColorSpace << /DefaultGray [/Separation /Spot /DeviceRGB << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >>] >> /Font << /H 5 0 R /Unused 5 0 R >>"),
            Stream("0.5 g 0 0 100 100 re f" + Word), Helvetica]));
        byte[] before = Render(source);

        PdfDocument output = PdfDocument.Open(Reduce(source).Data);

        Assert.Equal(before, Render(output));
        PdfDictionary resources = Resources(output, 0);
        Assert.True(Category(output, resources, "ColorSpace").ContainsKey(Name("DefaultGray")));
        Assert.False(Category(output, resources, "Font").ContainsKey(Name("Unused")));
    }

    [Fact]
    public void ADefaultColorSpaceAloneDoesNotCountAsSomethingUnused()
    {
        PdfDocument source = PdfDocument.Open(Build([Catalog, Pages,
            Page("/ColorSpace << /DefaultGray /DeviceGray >>"), Stream("0.5 g 0 0 100 100 re f")]));

        Assert.Empty(PdfOptimizer.UnusedResourcePages(source));
    }

    // The editor's other callers name no default color space, so for them the page stays whole.
    [Fact]
    public void PruningThroughTheEditorKeepsAPageWithADefaultColorSpaceWhole()
    {
        PdfDocument source = PdfDocument.Open(Build([Catalog, Pages,
            Page("/ColorSpace << /DefaultGray /DeviceGray >> /Font << /Unused 5 0 R >>"),
            Stream("0.5 g 0 0 100 100 re f"), Helvetica]));

        PdfDocument output = PdfDocument.Open(new KillerPdf.Engine.Editing.PdfIncrementalPageEditor(source)
            .SetPageContentAndPruneResources(0, new PdfPageContentReader(source).ReadInstructions(0)).Build());

        Assert.True(Category(output, Resources(output, 0), "ColorSpace").ContainsKey(Name("DefaultGray")));
    }

    [Fact]
    public void AReaderThatTheReduceDoesNotNeedCannotStopIt()
    {
        PdfDocument source = PdfDocument.Open(Build([Catalog, Pages,
            Page("/Font << /H 5 0 R >>", "/Annots [(not a dictionary)]"), Stream(Word), Helvetica]));
        byte[] before = Render(source);

        Assert.Equal(before, Render(PdfDocument.Open(Reduce(source).Data)));
    }

    [Fact]
    public void AnUnreadablePageIsLeftAloneInsteadOfStoppingTheCleanup()
    {
        PdfDocument source = PdfDocument.Open(Build([Catalog, Pages,
            Page("/Font << /H 5 0 R /Unused 5 0 R >>"),
            Stream("not zlib data", "/Filter /FlateDecode"), Helvetica]));

        Assert.Empty(PdfOptimizer.UnusedResourcePages(source));
    }

    [Fact]
    public void TwoLayersWithTheSameDictionaryStayTwoLayersOnOnePage()
    {
        PdfDocument source = PdfDocument.Open(Build([
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [6 0 R 7 0 R] /D << /OFF [7 0 R] /Order [6 0 R 7 0 R] >> >> >>",
            Pages, Page("/Properties << /A 6 0 R /B 7 0 R >>"), "null", "null",
            "<< /Type /OCG /Name (Layer 1) >>", "<< /Type /OCG /Name (Layer 1) >>"],
            contents: "/OC /A BDC 0 0 100 200 re f EMC /OC /B BDC 100 0 100 200 re f EMC"));
        Assert.Equal(5000, DarkPixels(source));

        PdfDocument output = PdfDocument.Open(Reduce(source).Data);

        Assert.Equal(5000, DarkPixels(output));
        PdfDictionary properties = Category(output, Resources(output, 0), "Properties");
        Assert.True(properties.ContainsKey(Name("A")));
        Assert.True(properties.ContainsKey(Name("B")));
    }

    [Fact]
    public void TwoLayersWithTheSameDictionaryOnTwoPagesReduceAndStayApart()
    {
        PdfDocument source = PdfDocument.Open(Build([
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [6 0 R 7 0 R] /D << /OFF [7 0 R] /Order [6 0 R 7 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> /Properties << /L 6 0 R >> >> /Contents 8 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R >> /Properties << /L 7 0 R >> >> /Contents 9 0 R >>",
            Helvetica, "<< /Type /OCG /Name (Layer 1) >>", "<< /Type /OCG /Name (Layer 1) >>",
            Stream("/OC /L BDC 0 0 200 200 re f EMC"), Stream("/OC /L BDC 0 0 200 200 re f EMC")]));

        PdfDocument output = PdfDocument.Open(Reduce(source).Data);

        Assert.Equal(10000, DarkPixels(output, 0));
        Assert.Equal(0, DarkPixels(output, 1));
    }

    [Fact]
    public void TwoIdenticalMembershipDictionariesAreNotMerged()
    {
        PdfDocument source = PdfDocument.Open(Build([
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [6 0 R] /D << /Order [6 0 R] >> >> >>",
            Pages, Page("/Properties << /A 7 0 R /B 8 0 R >>"), "null", "null",
            "<< /Type /OCG /Name (Layer) >>", "<< /Type /OCMD /OCGs [6 0 R] >>", "<< /Type /OCMD /OCGs [6 0 R] >>"],
            contents: "/OC /A BDC 0 0 10 10 re f EMC /OC /B BDC 20 0 10 10 re f EMC"));

        PdfDocument output = PdfDocument.Open(Reduce(source).Data);

        PdfDictionary properties = Category(output, Resources(output, 0), "Properties");
        Assert.NotEqual(Assert.IsType<PdfIndirectReference>(properties[Name("A")]).ObjectNumber,
            Assert.IsType<PdfIndirectReference>(properties[Name("B")]).ObjectNumber);
    }

    [Fact]
    public void TwoIdenticalPlainPropertyListsStillMerge()
    {
        PdfDocument source = PdfDocument.Open(Build([Catalog, Pages,
            Page("/Properties << /A 6 0 R /B 7 0 R >>"), "null", "null",
            "<< /Note (same) >>", "<< /Note (same) >>"],
            contents: "/Span /A BDC 0 0 10 10 re f EMC /Span /B BDC 20 0 10 10 re f EMC"));

        PdfDocument output = PdfDocument.Open(Reduce(source).Data);

        Assert.Single(Category(output, Resources(output, 0), "Properties"));
    }

    [Fact]
    public void ATaggedDocumentWithAnUnusedFontReducesWithItsOperatorsIntact()
    {
        PdfDocument source = PdfDocument.Open(Tagged());
        byte[] content = Content(source, 0);

        PdfOptimizationResult result = Reduce(source);
        PdfDocument output = PdfDocument.Open(result.Data);

        Assert.Contains(PdfOptimizationChangeKind.PruneUnusedPageResources, result.Changes);
        Assert.Equal(content, Content(output, 0));
        Assert.False(Category(output, Resources(output, 0), "Font").ContainsKey(Name("Unused")));
        Assert.True(PdfPageTree.Read(output).Catalog.ContainsKey(Name("StructTreeRoot")));
    }

    [Fact]
    public void SanitizingATaggedDocumentWithAnUnusedFontFinishes()
    {
        PdfDocument source = PdfDocument.Open(Tagged());
        byte[] content = Content(source, 0);

        PdfOptimizationResult result = PdfOptimizer.CreatePlan(source, new PdfOptimizationOptions
        {
            RemoveMetadata = true,
            PruneUnusedPageResources = true,
            PruneUnreachableObjects = true
        }).Apply();

        Assert.Equal(content, Content(PdfDocument.Open(result.Data), 0));
    }

    // Each pass used to rewrite references one step, so a merged object could be left pointing at an
    // object the same pass removed.
    [Theory]
    [InlineData("chain", 6, 7, 5, 6)]
    [InlineData("swap", 5, 6, 6, 5)]
    public void ResourcesMergedAcrossCategoriesAllLandOnOneSurvivor(
        string shape, int firstState, int firstProperty, int secondState, int secondProperty)
    {
        Assert.NotEmpty(shape);
        PdfDocument source = PdfDocument.Open(Build([Catalog,
            "<< /Type /Pages /Kids [3 0 R 8 0 R] /Count 2 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ExtGState << /G {firstState} 0 R >> /Properties << /P {firstProperty} 0 R >> >> /Contents 4 0 R >>",
            Stream("/G gs /Span /P BDC 0 0 10 10 re f EMC"), "<< >>", "<< >>", "<< >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ExtGState << /G {secondState} 0 R >> /Properties << /P {secondProperty} 0 R >> >> /Contents 4 0 R >>"]));
        int before = DarkPixels(source);

        PdfDocument output = PdfDocument.Open(Reduce(source).Data);

        Assert.Equal(before, DarkPixels(output));
        Assert.Equal(2, PdfPageTree.Read(output).Pages.Count);
    }

    [Fact]
    public void ThousandsOfPagesSharingOneImageFingerprintItOnce()
    {
        const int pageCount = 1500;
        var objects = new List<string>
        {
            Catalog,
            $"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(5, pageCount).Select(number => $"{number} 0 R"))}] /Count {pageCount} >>",
            Image(0), Stream(PaintImage)
        };
        objects.AddRange(Enumerable.Repeat(
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /XObject << /Im 3 0 R >> >> /Contents 4 0 R >>",
            pageCount));
        PdfDocument source = PdfDocument.Open(Build(objects));

        var merging = new PdfResourceFingerprints();
        Assert.Equal(0, PdfDocumentWriter.CountDuplicatePageResourceObjects(source, merging));
        Assert.Equal(1, merging.Computed);

        var planning = new PdfResourceFingerprints();
        Assert.Empty(PdfOptimizer.UnusedResourcePages(source, fingerprints: planning));
        Assert.Equal(1, planning.Computed);
    }

    [Fact]
    public void ACrossReferenceEntryNothingUsesNoLongerStopsReduce()
    {
        PdfDocument source = PdfDocument.Open(Build([Catalog, Pages,
            Page("/Font << /H 5 0 R >>"), Stream(Word), Helvetica, "<< /Unused true >>"],
            pointAt: new Dictionary<int, int> { [6] = 5 }));
        byte[] before = Render(source);

        PdfOptimizationResult result = Reduce(source);

        Assert.Contains(result.ReferenceRepairs, repair => repair.TargetObjectNumber == 6);
        Assert.Equal(before, Render(PdfDocument.Open(result.Data)));
    }

    [Fact]
    public void AReferenceToAnUnreadableEntryIsRepairedAndReported()
    {
        PdfDocument source = PdfDocument.Open(Build([Catalog, Pages,
            Page("/Font << /H 5 0 R /Stale 6 0 R >>"), Stream(Word), Helvetica, "<< /Unused true >>"],
            pointAt: new Dictionary<int, int> { [6] = 5 }));
        byte[] before = Render(source);

        PdfOptimizationResult result = Reduce(source);
        PdfDocument output = PdfDocument.Open(result.Data);

        Assert.Contains(result.ReferenceRepairs, repair => repair.TargetObjectNumber == 6 && repair.OwnerObjectNumber == 3);
        Assert.Equal(before, Render(output));
        Assert.False(Category(output, Resources(output, 0), "Font").ContainsKey(Name("Stale")));
    }

    [Fact]
    public void AnUnreadableEntryStaysRefusedWhenRepairsWereNotAskedFor()
    {
        PdfDocument source = PdfDocument.Open(Build([Catalog, Pages,
            Page("/Font << /H 5 0 R >>"), Stream(Word), Helvetica, "<< /Unused true >>"],
            pointAt: new Dictionary<int, int> { [6] = 5 }));

        Assert.ThrowsAny<FormatException>(() => PdfDocumentWriter.Write(source));
    }

    // Only Reduce and Sanitize, which normalize first and then verify, may leave an entry out.
    [Fact]
    public void ARewriteOutsideTheOptimizerStillRefusesAnUnreadableEntry()
    {
        PdfDocument source = PdfDocument.Open(Build([Catalog, Pages,
            Page("/Font << /H 5 0 R >>"), Stream(Word), Helvetica, "<< /Unused true >>"],
            pointAt: new Dictionary<int, int> { [6] = 5 }));

        Assert.ThrowsAny<FormatException>(() => PdfDocumentWriter.Write(source,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true }));
    }

    // Each step of a Reduce reopens its work. A strict reopen could not read what recovery read from
    // the source, and the page's content would then look like damage to be left out.
    [Fact]
    public void ContentOnlyRecoveryCanReadSurvivesAReduceThatPrunes()
    {
        PdfDocument source = PdfDocument.OpenWithCompatibilityRecovery(Build([Catalog,
            "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>",
            Page("/Font << /H 5 0 R /Unused 8 0 R >>"), Stream(Word), Helvetica,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /H 5 0 R >> >> /Contents 7 0 R >>",
            $"<< >>\nstream\n{Word}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>"]));
        int before = DarkPixels(source, 1);
        Assert.True(before > 0);

        PdfDocument output = PdfDocument.Open(Reduce(source).Data);

        Assert.Equal(before, DarkPixels(output, 1));
        Assert.False(Category(output, Resources(output, 0), "Font").ContainsKey(Name("Unused")));
    }

    [Fact]
    public void AFileWithUsageRightsIsRefusedInPlainWords()
    {
        PdfDocument source = PdfDocument.Open(Rights("/Perms << /UR3 5 0 R >>"));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfDocumentWriter.Write(source));

        Assert.Contains("Reader usage rights", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowSignatureInvalidation", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFileWithUsageRightsLosesThemOnlyWhenThatIsAllowed(bool replaceDanglingReferences)
    {
        PdfDocument source = PdfDocument.Open(Rights("/Perms << /UR3 5 0 R >>"));

        Assert.Throws<InvalidOperationException>(() => PdfDocumentWriter.Write(source,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = replaceDanglingReferences }));
        PdfDocument output = PdfDocument.Open(PdfDocumentWriter.Write(source, new PdfDocumentWriteOptions
        {
            ReplaceDanglingReferences = replaceDanglingReferences,
            AllowSignatureInvalidation = true
        }));

        Assert.False(PdfPageTree.Read(output).Catalog.ContainsKey(Name("Perms")));
    }

    // A protected form opens from a decrypted copy, so refusing that write would stop it opening.
    [Fact]
    public void RemovingEncryptionStillWritesAFileWithUsageRights()
    {
        PdfDocument source = PdfDocument.Open(Rights("/Perms << /UR3 5 0 R >>"));

        PdfDocument output = PdfDocument.Open(PdfDocumentWriter.Write(source,
            new PdfDocumentWriteOptions { RemoveEncryption = true }));

        Assert.True(PdfPageTree.Read(output).Catalog.ContainsKey(Name("Perms")));
    }

    [Fact]
    public void TheDeprecatedUsageRightsKeyCountsToo()
    {
        PdfDocument source = PdfDocument.Open(Rights("/Perms << /UR 5 0 R >>"));

        Assert.Throws<InvalidOperationException>(() => PdfDocumentWriter.Write(source));
    }

    [Fact]
    public void AnEmptyPermissionsDictionaryIsNotUsageRights()
    {
        PdfDocument source = PdfDocument.Open(Rights("/Perms << >>"));

        Assert.NotEmpty(PdfDocumentWriter.Write(source));
    }

    [Fact]
    public void ReduceRefusesUsageRightsUnlessAllowedAndThenRemovesThem()
    {
        PdfDocument source = PdfDocument.Open(Rights("/Perms << /UR3 5 0 R >>"));
        PdfOptimizationOptions options = PdfOptimizationOptions.ForDocument(source)
            with { RepairHarmlessArtifacts = true, ReplaceDanglingReferences = true };

        Assert.Throws<InvalidOperationException>(() => PdfOptimizer.CreatePlan(source, options).Apply());
        PdfOptimizationResult result = PdfOptimizer.CreatePlan(source,
            options with { AllowSignatureInvalidation = true }).Apply();

        Assert.False(PdfPageTree.Read(PdfDocument.Open(result.Data)).Catalog.ContainsKey(Name("Perms")));
    }

    private static byte[] Rights(string permissions) => Build([
        $"<< /Type /Catalog /Pages 2 0 R {permissions} >>", Pages,
        Page(""), Stream("0 0 10 10 re f"),
        "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 100 200 100] /Contents <00> "
        + "/Reference [<< /Type /SigRef /TransformMethod /UR3 /TransformParams << /Type /TransformParams /V /2.2 >> >>] >>"]);

    private static byte[] Tagged() => Build([
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> >>", Pages,
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R /Unused 8 0 R >> >> /StructParents 0 /Contents 4 0 R >>",
        Stream("/P << /MCID 0 >> BDC BT /F1 12 Tf 100 600 Td (TEXT) Tj ET EMC"), Helvetica,
        "<< /Type /StructTreeRoot /K [7 0 R] >>", "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>"]);

    // A 200 by 200 page whose lower left quarter is painted only by a route that takes /Im from the
    // page's resources, with a word above it that keeps the page from being empty.
    private static byte[] InheritingFixture(string route) => route switch
    {
        "type3 selected by Tf" => Build([Catalog, Pages,
            Page("/Font << /F1 5 0 R /H 8 0 R >> /XObject << /Im 7 0 R >>"),
            Stream("BT /F1 100 Tf 0 0 Td (A) Tj ET" + Word), Type3WithoutResources,
            Stream("100 0 d0 " + PaintImage), Image(0), Helvetica]),
        "type3 selected by gs" => Build([Catalog, Pages,
            Page("/ExtGState << /G << /Font [5 0 R 100] >> >> /Font << /H 8 0 R >> /XObject << /Im 7 0 R >>"),
            Stream("q /G gs BT 0 0 Td (A) Tj ET Q" + Word), Type3WithoutResources,
            Stream("100 0 d0 " + PaintImage), Image(0), Helvetica]),
        "tiling pattern" => Build([Catalog, Pages,
            Page("/Pattern << /P 6 0 R >> /Font << /H 8 0 R >> /XObject << /Im 7 0 R >>"),
            Stream("/Pattern cs /P scn 0 0 100 100 re f 0 g" + Word), "null",
            Stream(PaintImage, "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 100 100] /XStep 100 /YStep 100"),
            Image(0), Helvetica]),
        "soft mask group" => Build([Catalog, Pages,
            Page("/ExtGState << /M << /SMask << /Type /Mask /S /Luminosity /G 6 0 R >> >> >> /Font << /H 8 0 R >> /XObject << /Im 7 0 R >>"),
            Stream("q /M gs 0 g 0 0 100 100 re f Q" + Word), "null",
            Stream(PaintImage, "/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /CS /DeviceGray >>"),
            Image(255), Helvetica]),
        "annotation appearance" => Build([Catalog, Pages,
            Page("/Font << /H 8 0 R >> /XObject << /Im 7 0 R >>", "/Annots [5 0 R]"),
            Stream("0 g" + Word), "<< /Type /Annot /Subtype /Stamp /Rect [0 0 100 100] /F 4 /AP << /N 6 0 R >> >>",
            Stream(PaintImage, "/Type /XObject /Subtype /Form /BBox [0 0 100 100]"), Image(0), Helvetica]),
        "form drawing a type3 font" => Build([Catalog, Pages,
            Page("/Font << /F1 5 0 R /H 8 0 R >> /XObject << /Im 7 0 R /Fm 9 0 R >>"),
            Stream("/Fm Do" + Word), Type3WithoutResources, Stream("100 0 d0 " + PaintImage), Image(0), Helvetica,
            Stream("BT /F1 100 Tf 0 0 Td (A) Tj ET", "/Type /XObject /Subtype /Form /BBox [0 0 200 200]")]),
        _ => throw new ArgumentOutOfRangeException(nameof(route))
    };

    private static PdfOptimizationResult Reduce(PdfDocument document) => PdfOptimizer.CreatePlan(document,
        PdfOptimizationOptions.ForDocument(document) with { RepairHarmlessArtifacts = true, ReplaceDanglingReferences = true }).Apply();

    private static byte[] Render(PdfDocument document) => new PdfPageRenderer(document)
        .Render(0, new PdfRenderOptions(100, 100) { CacheResult = false }).Pixels.ToArray();

    private static int DarkPixels(PdfDocument document, int pageIndex = 0) => new PdfPageRenderer(document)
        .Render(pageIndex, new PdfRenderOptions(100, 100) { CacheResult = false }).Pixels.ToArray().Chunk(4).Count(IsDark);

    private static bool IsDark(byte[] pixel) => pixel[0] < 128;

    private static byte[] Content(PdfDocument document, int pageIndex) => document.DecodeStream(Assert.IsType<PdfStream>(
        document.Resolve(Assert.IsType<PdfIndirectReference>(PdfPageTree.Read(document).Pages[pageIndex].Dictionary[Name("Contents")]))));

    private static PdfDictionary Resources(PdfDocument document, int pageIndex) => Assert.IsType<PdfDictionary>(
        Resolve(document, PdfPageTree.Read(document).Pages[pageIndex].InheritedValues[Name("Resources")]));

    private static PdfDictionary Category(PdfDocument document, PdfDictionary resources, string category) =>
        Assert.IsType<PdfDictionary>(Resolve(document, resources[Name(category)]));

    private static PdfObject Resolve(PdfDocument document, PdfObject value) =>
        value is PdfIndirectReference reference ? document.Resolve(reference) : value;

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static string Page(string resources, string entries = "") =>
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << {resources} >> /Contents 4 0 R {entries} >>";

    private static string Image(byte fill) => Stream(new string((char)fill, 4),
        "/Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8 /ColorSpace /DeviceGray");

    private static string Stream(string data, string entries = "") =>
        $"<< {entries} /Length {Encoding.Latin1.GetByteCount(data)} >>\nstream\n{data}\nendstream";

    // Object numbers follow list order, and contents, when given, replaces object 4, the page's content
    // stream. An entry in pointAt makes that object's cross-reference entry point at another object.
    private static byte[] Build(IReadOnlyList<string> objects, string? contents = null,
        IReadOnlyDictionary<int, int>? pointAt = null)
    {
        string[] written = [.. objects];
        if (contents is not null) written[3] = Stream(contents);
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < written.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{written[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {written.Length + 1}\n0000000000 65535 f \n");
        for (int index = 0; index < offsets.Count; index++)
        {
            int offset = pointAt is not null && pointAt.TryGetValue(index + 1, out int other)
                ? offsets[other - 1] : offsets[index];
            pdf.Append($"{offset:0000000000} 00000 n \n");
        }
        pdf.Append($"trailer << /Size {written.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
