using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using System.Text;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfContentRedactionTests
{
    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    [Theory]
    [InlineData("ActualText", false)]
    [InlineData("Alt", false)]
    [InlineData("E", false)]
    [InlineData("ActualText", true)]
    [InlineData("Alt", true)]
    [InlineData("E", true)]
    [InlineData("Contents", false)]
    [InlineData("Contents", true)]
    public void RedactionRemovesAffectedNamedPropertyDescriptions(string key, bool direct)
    {
        string property = $"<< /{key} (NAMEDPROPERTYSECRET) /MCID 0 >>";
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> /Properties << /Sensitive {(direct ? property : "6 0 R")} >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 20 100 Td /Span /Sensitive BDC (VISIBLESECRET) Tj EMC ET"),
            property
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(0, 80, 199, 130))]);

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.True(result.Verification.Succeeded);
        Assert.DoesNotContain("VISIBLESECRET", new PdfPageContentReader(output).Read(0).Text);
        AssertDescriptionAbsent(output, "NAMEDPROPERTYSECRET");
    }

    [Fact]
    public void OpaqueResourceConsumersRequireRasterApprovalWhenNamedDescriptionsAreRedacted()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> /Properties << /Sensitive 6 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream("/Opaque unknown BT /F1 12 Tf 20 100 Td /Span /Sensitive BDC (SECRET) Tj EMC ET"),
            "<< /ActualText (NAMEDPROPERTYSECRET) >>"
        ]));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(0, 80, 199, 130));

        Assert.Equal([0], PdfContentRedaction.Analyze(document, [region]).RasterPages);
        Assert.Throws<InvalidOperationException>(() => PdfContentRedaction.Apply(document, [region]));
    }

    [Theory]
    [InlineData("ActualText", false)]
    [InlineData("Alt", false)]
    [InlineData("E", false)]
    [InlineData("ActualText", true)]
    [InlineData("Alt", true)]
    [InlineData("E", true)]
    [InlineData("Contents", false)]
    [InlineData("Contents", true)]
    public void RedactionRemovesDescriptionsOfRemovedVectors(string key, bool raster)
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            $"/Figure << /{key} (VECTORSECRET) >> BDC 0 0 1 rg 20 20 10 10 re f EMC",
            "/MediaBox [0 0 100 100]"));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(10, 10, 40, 40))],
            new PdfContentRedactionOptions { ApprovedRasterPages = raster ? [0] : [], RasterDpi = 72 });

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.True(result.Verification.Succeeded);
        Assert.DoesNotContain(new PdfPageContentReader(output).Read(0).Paths,
            path => path.BoundingBox == new PdfContentBounds(20, 20, 30, 30));
        AssertDescriptionAbsent(output, "VECTORSECRET");
    }

    private static void AssertDescriptionAbsent(PdfDocument document, string secret)
    {
        foreach (var entry in document.CrossReferences.Values)
        {
            if (entry.Type is not (PdfCrossReferenceEntryType.InUse or PdfCrossReferenceEntryType.Compressed))
                continue;
            Inspect(document.Resolve(entry.ObjectNumber));
        }

        void Inspect(PdfObject value)
        {
            switch (value)
            {
                case PdfString text:
                    Assert.DoesNotContain(secret, Encoding.Latin1.GetString(text.Bytes.Span));
                    break;
                case PdfDictionary dictionary:
                    foreach (PdfObject child in dictionary.Values) Inspect(child);
                    break;
                case PdfArray array:
                    foreach (PdfObject child in array) Inspect(child);
                    break;
                case PdfStream stream:
                    Inspect(stream.Dictionary);
                    Assert.DoesNotContain(secret, Encoding.Latin1.GetString(
                        KillerPdf.Engine.Filters.PdfStreamDecoder.Decode(stream, document.Resolve)));
                    break;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RedactionRemovesDescriptionsOfRemovedOrPaintedImages(bool partial, bool raster = false)
    {
        PdfDocument document = PdfDocument.Open(ImagePage(
            "/Figure << /Alt (IMAGESECRET) >> BDC q 100 0 0 100 20 20 cm /Im1 Do Q EMC", 0));
        var bounds = partial ? new PdfContentBounds(20, 20, 70, 120)
            : new PdfContentBounds(10, 10, 130, 130);

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, bounds)],
            new PdfContentRedactionOptions { ApprovedRasterPages = raster ? [0] : [], RasterDpi = 72 });

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        AssertDescriptionAbsent(output, "IMAGESECRET");
        Assert.Equal(raster || partial ? 1 : 0, new PdfPageContentReader(output).Read(0).Images.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedactionRemovesDescriptionsOfRemovedInlineImages(bool raster)
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "/Figure << /Alt (INLINESECRET) >> BDC q 10 0 0 10 20 20 cm "
            + "BI /W 1 /H 1 /CS /RGB /BPC 8 ID abc EI Q EMC",
            "/MediaBox [0 0 100 100]"));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(10, 10, 40, 40))],
            new PdfContentRedactionOptions { ApprovedRasterPages = raster ? [0] : [], RasterDpi = 72 });

        Assert.True(result.Verification.Succeeded);
        AssertDescriptionAbsent(PdfDocument.Open(result.Document), "INLINESECRET");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedactionPropagatesChangedFormContentToEnclosingDescriptions(bool ownResources)
    {
        const string resources = "/Font << /F1 4 0 R >> /Properties << /Sensitive 7 0 R >>";
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << {resources} /XObject << /Fm 5 0 R >> >> /Contents 6 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 20 100 Td /Span /Sensitive BDC (VISIBLESECRET) Tj EMC ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 200 200] "
                + (ownResources ? $"/Resources << {resources} >>" : "")),
            Stream("/Figure << /Alt (PARENTSECRET) >> BDC /Fm Do EMC"),
            "<< /ActualText (CHILDSECRET) >>"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(0, 80, 199, 130))]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        AssertDescriptionAbsent(output, "CHILDSECRET");
        AssertDescriptionAbsent(output, "PARENTSECRET");
    }

    [Fact]
    public void DefaultVerificationRejectsRetiredDescriptionsRetainedOutsideResources()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R /RetainedDescription 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> /Properties << /Sensitive 6 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 20 100 Td /Span /Sensitive BDC (SECRET) Tj EMC ET"),
            "<< /ActualText (NAMEDPROPERTYSECRET) >>"
        ]));

        var error = Assert.Throws<PdfRedactionVerificationException>(() => PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(0, 80, 199, 130))]));

        Assert.Contains(error.Report.Findings, finding => finding.Code == "RetainedDescription");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedNamedDescriptionsSurviveOnlyForUnaffectedUses(bool affectedFirst)
    {
        const string affected = "/Span /Shared BDC BT /F1 12 Tf 20 100 Td (REMOVE) Tj ET EMC ";
        const string unaffected = "/Span /Shared BDC BT /F1 12 Tf 20 20 Td (KEEP) Tj ET EMC ";
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> /Properties << /Shared 6 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream(affectedFirst ? affected + unaffected : unaffected + affected),
            "<< /ActualText (SHAREDDESCRIPTION) >>"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(0, 80, 199, 130))]);

        PdfDocument output = PdfDocument.Open(result.Document);
        PdfObject ResolveOutput(PdfObject value) => value is PdfIndirectReference reference
            ? output.Resolve(reference) : value;
        var catalog = (PdfDictionary)ResolveOutput(output.Trailer[new PdfName("Root"u8)]);
        var pages = (PdfDictionary)ResolveOutput(catalog[new PdfName("Pages"u8)]);
        var kids = (PdfArray)ResolveOutput(pages[new PdfName("Kids"u8)]);
        var page = (PdfDictionary)ResolveOutput(kids[0]);
        var stream = (PdfStream)ResolveOutput(page[new PdfName("Contents"u8)]);
        var instructions = PdfContentStreamReader.Read(KillerPdf.Engine.Filters.PdfStreamDecoder.Decode(stream));
        var marked = instructions.Where(instruction => instruction.Operator == "BDC").ToArray();
        Assert.Equal(2, marked.Length);
        Assert.Single(marked, instruction => instruction.Operands[1] is PdfName);
        var sanitized = Assert.IsType<PdfDictionary>(Assert.Single(marked,
            instruction => instruction.Operands[1] is PdfDictionary).Operands[1]);
        Assert.False(sanitized.ContainsKey(new PdfName("ActualText"u8)));
        var resources = (PdfDictionary)ResolveOutput(page[new PdfName("Resources"u8)]);
        var properties = (PdfDictionary)ResolveOutput(resources[new PdfName("Properties"u8)]);
        var description = (PdfDictionary)ResolveOutput(properties[new PdfName("Shared"u8)]);
        Assert.Equal("SHAREDDESCRIPTION", Encoding.Latin1.GetString(
            ((PdfString)description[new PdfName("ActualText"u8)]).Bytes.Span));
        Assert.Equal("SHAREDDESCRIPTION", new PdfPageContentReader(output).Read(0).Text.Trim());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RedactionRemovesDescriptionsOfRemovedShadings(bool raster)
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Shading << /S1 5 0 R >> >> /Contents 4 0 R >>",
            Stream("/Figure << /Alt (SHADINGSECRET) >> BDC /S1 sh EMC"),
            "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 100 0] /Extend [true true] /Function << /FunctionType 2 /Domain [0 1] /C0 [1 0 0] /C1 [0 0 1] /N 1 >> >>"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(0, 0, 100, 100))],
            new PdfContentRedactionOptions { ApprovedRasterPages = raster ? [0] : [], RasterDpi = 72 });

        Assert.True(result.Verification.Succeeded);
        AssertDescriptionAbsent(PdfDocument.Open(result.Document), "SHADINGSECRET");
    }

    [Fact]
    public void RasterRedactionPreservesIndirectPropertyValuesWithoutKeepingDescriptions()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> /Properties << /Sensitive 6 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 20 100 Td /Span /Sensitive BDC (SECRET) Tj EMC ET"),
            "<< /ActualText (REFERENCESECRET) /Lang 7 0 R >>",
            "(en-US)"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(0, 80, 199, 130))],
            new PdfContentRedactionOptions { ApprovedRasterPages = [0], RasterDpi = 72 });

        Assert.True(result.Verification.Succeeded);
        AssertDescriptionAbsent(PdfDocument.Open(result.Document), "REFERENCESECRET");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultVerificationRequiresUnaffectedUseForInheritedDirectDescriptions(bool used)
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 /Resources << /Font << /F1 4 0 R >> /Properties << /Sensitive << /Alt (INHERITEDSECRET) >> >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 20 100 Td /Span /Sensitive BDC (SECRET) Tj EMC ET"),
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] {(used ? "/Contents 7 0 R" : "")} >>",
            Stream("/Figure << /Alt (INHERITEDSECRET) >> BDC BT /F1 12 Tf 20 20 Td (KEEP) Tj ET EMC")
        ]));

        if (used)
        {
            PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
                [new PdfRedactionRegion(0, new PdfContentBounds(0, 80, 199, 130))]);
            Assert.True(result.Verification.Succeeded);
            Assert.Equal("KEEP", new PdfPageContentReader(PdfDocument.Open(result.Document)).Read(1).Text.Trim());
            return;
        }
        var error = Assert.Throws<PdfRedactionVerificationException>(() => PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(0, 80, 199, 130))]));

        Assert.Contains(error.Report.Findings, finding => finding.Code == "RetainedDescription");
    }

    [Theory]
    [InlineData("ActualText")]
    [InlineData("Alt")]
    [InlineData("E")]
    public void RemovedVectorsLoseStructureDescriptionsWhileUnaffectedSiblingsKeepTheirs(string key)
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R >>",
            Stream("/Figure << /MCID 0 >> BDC 20 20 10 10 re f EMC /Figure << /MCID 1 >> BDC 70 70 10 10 re f EMC"),
            "<< /Type /StructTreeRoot /K [6 0 R 7 0 R] >>",
            $"<< /Type /StructElem /S /Figure /P 5 0 R /Pg 3 0 R /{key} (STRUCTURESECRET) /K 0 >>",
            "<< /Type /StructElem /S /Figure /P 5 0 R /Pg 3 0 R /Alt (KEPTDESCRIPTION) /K 1 >>"
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(10, 10, 40, 40))]);

        PdfDocument output = PdfDocument.Open(result.Document);
        AssertDescriptionAbsent(output, "STRUCTURESECRET");
        PdfObject ResolveOutput(PdfObject value) => value is PdfIndirectReference reference
            ? output.Resolve(reference) : value;
        var catalog = (PdfDictionary)ResolveOutput(output.Trailer[new PdfName("Root"u8)]);
        var structure = (PdfDictionary)ResolveOutput(catalog[new PdfName("StructTreeRoot"u8)]);
        var kids = (PdfArray)ResolveOutput(structure[new PdfName("K"u8)]);
        var kept = (PdfDictionary)ResolveOutput(kids[1]);
        Assert.Equal("KEPTDESCRIPTION", Encoding.Latin1.GetString(
            ((PdfString)kept[new PdfName("Alt"u8)]).Bytes.Span));
    }

    [Fact]
    public void PartialVectorRedactionCannotPublishRecoverableGeometryWithoutRasterApproval()
    {
        PdfDocument document = PdfDocument.Open(SinglePage("1 0 0 rg 0 0 100 100 re f",
            "/MediaBox [0 0 100 100]"));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(25, 25, 75, 75));
        PdfContentRedactionResult? result = null;

        Exception? error = Record.Exception(() => result = PdfContentRedaction.Apply(document, [region]));

        if (result is not null)
        {
            // Inspect original geometry independently of whether an opaque cover hides it.
            PdfPageContent output = new PdfPageContentReader(PdfDocument.Open(result.Document)).Read(0);
            Assert.DoesNotContain(output.Paths, path => path.BoundingBox == new PdfContentBounds(0, 0, 100, 100));
        }
        Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("without replacing them with an image", error.Message);
    }

    [Fact]
    public void StrokeWidthCanRequireRasterizationWhenTheCenterlineMissesTheRegion()
    {
        PdfDocument document = PdfDocument.Open(SinglePage("20 w 0 20 m 100 20 l S",
            "/MediaBox [0 0 100 100]"));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(25, 25, 75, 75));
        PdfRenderedPage original = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(100, 100));
        Assert.True(IsBlack(original, 50, 72));

        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(document, [region]);

        Assert.Equal([0], plan.RasterPages);
        Assert.Contains("Vector", Assert.Single(plan.Pages).RasterReasons);
    }

    [Fact]
    public void ClipOnlyPathsOverlappingTheRegionDoNotForceRasterization()
    {
        // A page-sized clip rectangle (re W n) paints nothing, so it is not recoverable geometry.
        PdfDocument document = PdfDocument.Open(SinglePage(
            "q 0 0 100 100 re W n 1 0 0 rg 0 0 20 20 re f Q",
            "/MediaBox [0 0 100 100]"));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(25, 25, 75, 75));

        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(document, [region]);
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.Empty(plan.RasterPages);
        Assert.DoesNotContain("Vector", Assert.Single(plan.Pages).RasterReasons);
        Assert.Equal(PdfRedactionPageMethod.Content, Assert.Single(result.Plan.Pages).Method);
        Assert.True(result.Verification.Succeeded);
        PdfRenderedPage rendered = new PdfPageRenderer(PdfDocument.Open(result.Document)).Render(0, new PdfRenderOptions(100, 100));
        Assert.True(IsBlack(rendered, 50, 50));
    }

    [Fact]
    public void VerifierRejectsVectorGeometryHiddenByAnOtherwiseValidCover()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "1 0 0 rg 0 0 100 100 re f q 0 0 0 rg 25 25 50 50 re f Q",
            "/MediaBox [0 0 100 100]"));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(25, 25, 75, 75));

        PdfRedactionVerificationReport report = PdfContentRedaction.Verify(document.Source, [region], []);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Findings, finding => finding.Code == "VectorInRegion");
    }

    [Fact]
    public void FullyCoveredVectorCoordinatesAreRemovedRatherThanLeftUnpainted()
    {
        PdfDocument document = PdfDocument.Open(SinglePage("35 35 m 65 35 l 50 65 l h f",
            "/MediaBox [0 0 100 100]"));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(25, 25, 75, 75));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        PdfPageContent output = new PdfPageContentReader(PdfDocument.Open(result.Document)).Read(0);
        Assert.DoesNotContain(output.Paths, path => path.Segments.Any(segment => segment.Operator == "m"));
        Assert.Equal(PdfRedactionPageMethod.Content, Assert.Single(result.Plan.Pages).Method);
    }

    [Theory]
    [InlineData("0 0 100 100 re f")]
    [InlineData("0 50 m 25 100 75 0 100 50 c f")]
    [InlineData("0 0 100 100 re 40 40 20 20 re f*")]
    [InlineData("0 0 100 100 re W n 0 50 m 100 50 l S")]
    [InlineData("20 w 0 20 m 100 20 l S")]
    [InlineData("2 0 0 2 0 0 cm 10 w 0 10 m 50 10 l S")]
    [InlineData("0 0 100 100 re f", 90)]
    [InlineData("0 0 100 100 re f", 180)]
    [InlineData("0 0 100 100 re f", 270)]
    [InlineData("0 0 m 100 0 l 100 100 l 0 100 l h 20 20 m 20 80 l 80 80 l 80 20 l h f")]
    [InlineData("0 50 m 10 100 20 0 30 50 c 40 100 50 0 60 50 c 70 100 80 0 90 50 c 100 100 100 0 100 50 c 90 0 80 100 70 50 c 60 0 50 100 40 50 c 30 0 20 100 10 50 c 0 0 0 100 0 50 c h f")]
    public void ApprovedVectorFallbackRemovesOriginalPaths(string content, int rotation = 0)
    {
        PdfDocument document = PdfDocument.Open(SinglePage("1 0 0 rg 1 0 0 RG " + content,
            $"/MediaBox [0 0 100 100] /Rotate {rotation}"));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(25, 25, 75, 75));
        Assert.Equal([0], PdfContentRedaction.Analyze(document, [region]).RasterPages);

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ApprovedRasterPages = [0], RasterDpi = 72 });

        PdfDocument output = PdfDocument.Open(result.Document);
        PdfPageContent page = new PdfPageContentReader(output).Read(0);
        Assert.All(page.Paths, path => Assert.Equal(region.Bounds, path.BoundingBox));
        Assert.Single(page.Images);
        Assert.True(result.Verification.Succeeded);

        // Remove only the appended cover in a test copy before checking the remaining picture.
        var contents = (PdfIndirectReference)PdfPageTree.Read(output).Pages[0].Dictionary[
            new PdfName("Contents"u8)];
        var stream = (PdfStream)output.Resolve(contents);
        var instructions = PdfContentStreamReader.Read(output.DecodeStream(stream));
        Assert.Equal(new[] { "q", "rg", "re", "f", "Q" }, instructions.TakeLast(5).Select(op => op.Operator));
        byte[] uncoveredBytes = new PdfIncrementalUpdateBuilder(output).ReplaceObject(contents.ObjectNumber,
            new PdfStream(new PdfDictionary([]), PdfContentStreamWriter.Write(instructions.SkipLast(5)))).Build();
        PdfDocument uncovered = PdfDocument.Open(uncoveredBytes);
        Assert.Empty(new PdfPageContentReader(uncovered).Read(0).Paths);
        PdfRenderedPage rendered = new PdfPageRenderer(uncovered).Render(0, new PdfRenderOptions(100, 100));
        Assert.True(IsBlack(rendered, 50, 50));
        PdfRenderedPage original = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(100, 100));
        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 100; x++)
                if (x < 23 || x > 77 || y < 23 || y > 77)
                    Assert.Equal(Pixel(original, x, y), Pixel(rendered, x, y));
    }

    [Fact]
    public void RestoredStrokeWidthDoesNotForceUnnecessaryRasterization()
    {
        PdfDocument document = PdfDocument.Open(SinglePage("q 100 w Q 0 10 m 100 10 l S",
            "/MediaBox [0 0 100 100]"));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(25, 25, 75, 75));

        Assert.Empty(PdfContentRedaction.Analyze(document, [region]).RasterPages);
        Assert.True(PdfContentRedaction.Apply(document, [region]).Verification.Succeeded);
    }

    [Fact]
    public void RemovesOnlyTheGlyphsUnderTheRegionAndKeepsTheRestInPlace()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 12 Tf 72 700 Td (Account SECRET42 closed) Tj ET"));
        PdfRedactionRegion region = Region(document, 0, "SECRET42");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ProhibitedText = ["SECRET42"] });

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Single(output.CrossReferences.Sections);
        Assert.True(result.Verification.Succeeded);
        PdfRedactionPageAnalysis page = Assert.Single(result.Plan.Pages);
        Assert.Equal(PdfRedactionPageMethod.Content, page.Method);
        Assert.Equal(8, page.RemovedGlyphs);
        string text = Letters(output, 0);
        Assert.Contains("Account", text);
        Assert.Contains("closed", text);
        Assert.DoesNotContain("SECRET", text);
        AssertKeptLettersUnmoved(document, output, 0, [region]);
        Assert.False(PdfContentRedaction.Verify(document.Source, [region], []).Succeeded);
    }

    [Fact]
    public void KeepsSpacingForKerningCharacterAndWordSpacingAndQuoteOperators()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 10 Tf 2 Tc 3 Tw 14 TL 50 600 Td [(Name: ) -120 (HIDDEN) 40 ( value)] TJ " +
            "(second HIDDEN line) ' 1 0.5 (third HIDDEN row) \" ET"));
        PdfRedactionRegion[] regions = [Region(document, 0, "HIDDEN", 0),
            Region(document, 0, "HIDDEN", 1), Region(document, 0, "HIDDEN", 2)];

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, regions,
            new PdfContentRedactionOptions { ProhibitedText = ["HIDDEN"] });

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.DoesNotContain("HIDDEN", Letters(output, 0));
        Assert.Contains("value", Letters(output, 0));
        Assert.Contains("row", Letters(output, 0));
        AssertKeptLettersUnmoved(document, output, 0, regions);
    }

    [Fact]
    public void HandlesCroppedRotatedPagesInReaderCoordinates()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 14 Tf 150 400 Td (TOP SECRET DATA) Tj ET",
            "/MediaBox [0 0 612 792] /CropBox [100 50 500 700] /Rotate 90"));
        PdfRedactionRegion region = Region(document, 0, "SECRET");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        PdfDocument output = PdfDocument.Open(result.Document);
        string text = Letters(output, 0);
        Assert.Contains("TOP", text);
        Assert.Contains("DATA", text);
        Assert.DoesNotContain("SECRET", text);
        AssertKeptLettersUnmoved(document, output, 0, [region]);

        // The crop box is 400 by 650 points; a quarter turn shows it 650 wide and 400 tall.
        PdfRenderedPage rendered = new PdfPageRenderer(output).Render(0, new PdfRenderOptions(650, 400));
        double cx = (region.Bounds.Left + region.Bounds.Right) / 2;
        double cy = (region.Bounds.Bottom + region.Bounds.Top) / 2;
        Assert.True(IsBlack(rendered, (int)cy, (int)cx));
        Assert.False(IsBlack(rendered, 10, 10));
    }

    [Fact]
    public void RedactionDoesNotRetainOriginalFormBytesThroughInheritedResources()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /Resources << /XObject << /Fm1 6 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R >>",
            Helvetica,
            Stream("q 1 0 0 1 100 500 cm /Fm1 Do Q"),
            Stream("BT /F1 12 Tf 10 10 Td (Inherited FORMSECRET text) Tj ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 400 100] /Resources << /Font << /F1 4 0 R >> >>")
        ]));
        PdfRedactionRegion region = Region(document, 0, "FORMSECRET");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.DoesNotContain("FORMSECRET", Letters(output, 0));
        Assert.Contains("Inherited", Letters(output, 0));
        PdfRedactionVerificationReport objectReport = PdfContentRedaction.Verify(
            result.Document, [region], [], ["FORMSECRET"]);
        Assert.True(objectReport.Succeeded, string.Join("; ", objectReport.Findings.Select(finding => finding.Message)));
    }

    [Fact]
    public void RefusesOriginalFormsRetainedOnlyByAnUnusedSiblingResource()
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /Resources << /XObject << /Fm1 6 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 500] /Contents 7 0 R >>",
            Stream("q 1 0 0 1 100 500 cm /Fm1 Do Q"),
            Stream("BT /F1 12 Tf 10 10 Td (Inherited FORMSECRET text) Tj ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 400 100] /Resources << /Font << /F1 8 0 R >> >>"),
            Stream(""),
            Helvetica
        ]));
        PdfRedactionRegion region = Region(document, 0, "FORMSECRET");

        PdfRedactionVerificationException error = Assert.Throws<PdfRedactionVerificationException>(() =>
            PdfContentRedaction.Apply(document, [region]));

        Assert.Contains(error.Report.Findings, finding => finding.Code == "RetainedSourceStream");
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void RefusesOriginalImagesRetainedOnlyByAnUnusedSiblingResource(bool partlyCovered, bool inherited)
    {
        // Inherited: the Pages node carries the image. Shared: both pages name one indirect XObject dictionary.
        string pages = inherited
            ? "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /Resources << /XObject << /Im1 6 0 R >> >> >>"
            : "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>";
        string resources = inherited ? string.Empty : "/Resources << /XObject 9 0 R >>";
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            pages,
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] {resources} /Contents 5 0 R >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] {resources} /Contents 7 0 R >>",
            Stream(partlyCovered ? "q 300 0 0 300 50 50 cm /Im1 Do Q" : "q 20 0 0 20 100 100 cm /Im1 Do Q"),
            SecretImage("/SMask 8 0 R"),
            Stream(""),
            MaskImage(),
            "<< /Im1 6 0 R >>"
        ]));
        var region = new PdfRedactionRegion(0, partlyCovered
            ? new PdfContentBounds(60, 210, 120, 300) : new PdfContentBounds(90, 90, 130, 130));

        PdfRedactionVerificationException error = Assert.Throws<PdfRedactionVerificationException>(() =>
            PdfContentRedaction.Apply(document, [region]));

        Assert.Contains(error.Report.Findings, finding => finding.Code == "RetainedSourceStream");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesAnInvokedInheritedImageAndItsMaskOnAnUntouchedSibling(bool partlyCovered)
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /Resources << /XObject << /Im1 6 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Contents 7 0 R >>",
            Stream(partlyCovered ? "q 300 0 0 300 50 50 cm /Im1 Do Q" : "q 20 0 0 20 100 100 cm /Im1 Do Q"),
            SecretImage("/SMask 8 0 R"),
            Stream("q 300 0 0 300 50 50 cm /Im1 Do Q"),
            MaskImage()
        ]));
        var region = new PdfRedactionRegion(0, partlyCovered
            ? new PdfContentBounds(60, 210, 120, 300) : new PdfContentBounds(90, 90, 130, 130));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        PdfExtractedImage sibling = Assert.Single(new PdfPageContentReader(output).Read(1).Images);
        Assert.Equal("Im1", sibling.ResourceName);
        Assert.Equal(PdfPageInformation.Read(document)[1], PdfPageInformation.Read(output)[1]);
    }

    [Theory]
    [InlineData("pattern")]
    [InlineData("type3")]
    [InlineData("appearance")]
    public void PreservesARetiredImageStillDrawnBySiblingPatternsGlyphsOrAppearances(string use)
    {
        // Page 1 drops the image; page 2 draws the same object through a tiling pattern, a Type 3 glyph or a stamp appearance.
        (string page, string content, string extra, string extra2) = use switch
        {
            "pattern" => ("/Resources << /Pattern << /P1 8 0 R >> >>",
                "q /Pattern cs /P1 scn 50 50 200 200 re f Q",
                Stream("q 40 0 0 40 0 0 cm /Im1 Do Q",
                    "/PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 40 40] /XStep 40 /YStep 40 /Resources << /XObject << /Im1 6 0 R >> >>"),
                "<< >>"),
            "type3" => ("/Resources << /Font << /T3 8 0 R >> >>",
                "BT /T3 200 Tf 50 50 Td (a) Tj ET",
                "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /square 9 0 R >> "
                    + "/Encoding << /Type /Encoding /Differences [97 /square] >> /FirstChar 97 /LastChar 97 /Widths [1000] /Resources << /XObject << /Im1 6 0 R >> >> >>",
                Stream("1000 0 d0 q 1000 0 0 1000 0 0 cm /Im1 Do Q")),
            _ => ("/Annots [8 0 R]",
                "",
                "<< /Type /Annot /Subtype /Stamp /Rect [50 50 250 250] /F 4 /AP << /N 9 0 R >> >>",
                Stream("q 200 0 0 200 0 0 cm /Im1 Do Q",
                    "/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /XObject << /Im1 6 0 R >> >>"))
        };
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << /XObject << /Im1 6 0 R >> >> /Contents 5 0 R >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] {page} /Contents 7 0 R >>",
            Stream("q 20 0 0 20 100 100 cm /Im1 Do Q"),
            SecretImage(""),
            Stream(content),
            extra,
            extra2
        ]));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(90, 90, 130, 130));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Empty(new PdfPageContentReader(output).Read(0).Images);
        Assert.True(HasSourceImage(output));
        PdfRenderedPage rendered = new PdfPageRenderer(output).Render(1, new PdfRenderOptions(400, 400, includeAnnotations: true));
        (byte red, byte green, byte blue) = Pixel(rendered, 100, 200);
        Assert.True(red < 250 || green < 250 || blue < 250, "Page 2 no longer draws the shared image.");
    }

    private static string SecretImage(string entries) =>
        $"<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 {entries} /Length 12 >>\nstream\n"
            + Encoding.Latin1.GetString([255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255]) + "\nendstream";

    private static string MaskImage() =>
        "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Length 4 >>\nstream\n"
            + Encoding.Latin1.GetString([255, 255, 255, 255]) + "\nendstream";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesAnInvokedInheritedFormOnAnUntouchedSibling(bool softMask)
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /Resources << /XObject << /Fm1 6 0 R >> /ExtGState << /GS1 << /SMask << /S /Luminosity /G 6 0 R >> >> >> >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 500] /Contents 7 0 R >>",
            Stream("q 1 0 0 1 100 500 cm /Fm1 Do Q"),
            Stream("BT /F1 12 Tf 10 10 Td (Inherited FORMSECRET text) Tj ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 400 100] /Group << /S /Transparency /CS /DeviceGray >> /Resources << /Font << /F1 8 0 R >> >>"),
            Stream(softMask ? "q /GS1 gs 0 0 400 500 re f Q" : "q 1 0 0 1 40 200 cm /Fm1 Do Q"),
            Helvetica
        ]));
        PdfRedactionRegion region = Region(document, 0, "FORMSECRET");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.DoesNotContain("FORMSECRET", Letters(output, 0));
        if (!softMask) Assert.Contains("FORMSECRET", Letters(output, 1));
        Assert.Equal(PdfPageInformation.Read(document)[1], PdfPageInformation.Read(output)[1]);
        AssertKeptLettersUnmoved(document, output, 1, []);
    }

    [Fact]
    public void RewritesSharedFormsOnlyForTheRedactedPage()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm1 6 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm1 6 0 R >> >> /Contents 5 0 R >>",
            Stream("q 1 0 0 1 100 500 cm /Fm1 Do Q"),
            Stream("BT /F1 12 Tf 10 10 Td (Shared FORMSECRET text) Tj ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 400 100] /Resources << /Font << /F1 7 0 R >> >>"),
            Helvetica
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfRedactionRegion region = Region(document, 0, "FORMSECRET");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.DoesNotContain("FORMSECRET", Letters(output, 0));
        Assert.Contains("Shared", Letters(output, 0));
        Assert.Contains("FORMSECRET", Letters(output, 1));
        AssertKeptLettersUnmoved(document, output, 0, [region]);
    }

    [Fact]
    public void ImageFullyInsideARegionIsRemovedAndThePageStaysEditable()
    {
        PdfDocument document = PdfDocument.Open(ImagePage(
            "BT /F1 12 Tf 72 700 Td (Caption) Tj ET q 20 0 0 20 100 100 cm /Im1 Do Q", rotate: 0));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(90, 90, 130, 130))]);

        PdfRedactionPageAnalysis page = Assert.Single(result.Plan.Pages);
        Assert.Equal(PdfRedactionPageMethod.Content, page.Method);
        Assert.Equal(1, page.RemovedImages);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Empty(new PdfPageContentReader(output).Read(0).Images);
        Assert.False(HasSourceImage(output));
        Assert.Contains("Caption", Letters(output, 0));
    }

    [Fact]
    public void PartlyCoveredImageThatCannotBePaintedNeedsApprovalAndKeepsItsSearchableTextOutsideTheRegion()
    {
        // A color-key mask keeps the image out of pixel painting, so the page needs the image fallback.
        PdfDocument document = PdfDocument.Open(ImagePage(
            "q 300 0 0 300 50 50 cm /Im1 Do Q BT 3 Tr /F1 12 Tf 230 100 Td (SCANNED SSN 123) Tj ET",
            rotate: 90, imageEntries: "/Mask [0 0 0 0 0 0]"));
        PdfRedactionRegion region = Region(document, 0, "123");

        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(document, [region]);
        Assert.Equal([0], plan.RasterPages);
        Assert.Contains("Image", Assert.Single(plan.Pages).RasterReasons);
        Assert.Throws<InvalidOperationException>(() => PdfContentRedaction.Apply(document, [region]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ApprovedRasterPages = [0], RasterDpi = 72, ProhibitedText = ["123"] });

        PdfDocument output = PdfDocument.Open(result.Document);
        PdfPageContent content = new PdfPageContentReader(output).Read(0);
        Assert.Equal(PdfContentRedaction.RedactionImageName, Assert.Single(content.Images).ResourceName);
        Assert.Contains("SCANNED", Letters(output, 0));
        Assert.DoesNotContain("123", Letters(output, 0));
        Assert.False(HasSourceImage(output));
        AssertKeptLettersUnmoved(document, output, 0, [region]);

        // Quarter turn: unrotated (x, y) shows at pixel (y, x) on this 400 by 400 page.
        PdfRenderedPage rendered = new PdfPageRenderer(output).Render(0, new PdfRenderOptions(400, 400));
        double cx = (region.Bounds.Left + region.Bounds.Right) / 2;
        double cy = (region.Bounds.Bottom + region.Bounds.Top) / 2;
        Assert.True(IsBlack(rendered, (int)cy, (int)cx));
        (byte red, byte green, byte blue) = Pixel(rendered, 275, 125);
        Assert.True(red > 200 && green < 60 && blue < 60, $"Expected red, got {red},{green},{blue}.");
    }

    [Fact]
    public void PartlyCoveredImageHasOnlyItsCoveredPixelsPaintedAndThePageStaysText()
    {
        PdfDocument document = PdfDocument.Open(ImagePage(
            "q 300 0 0 300 50 50 cm /Im1 Do Q BT /F1 12 Tf 60 30 Td (Caption) Tj ET", rotate: 0));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(60, 210, 120, 300));

        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(document, [region]);
        Assert.Empty(plan.RasterPages);
        Assert.Equal(1, Assert.Single(plan.Pages).RedactedImages);
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        PdfExtractedImage image = Assert.Single(new PdfPageContentReader(output).Read(0).Images);
        Assert.StartsWith("KPRI", image.ResourceName);
        Assert.Contains("Caption", Letters(output, 0));
        // The top-left pixel was red and is now black; the other three keep their colors.
        PdfRenderedPage rendered = new PdfPageRenderer(output).Render(0, new PdfRenderOptions(400, 400));
        Assert.True(IsBlack(rendered, 90, 150));
        (byte red, byte green, byte blue) = Pixel(rendered, 275, 125);
        Assert.True(green > 200 && red < 60 && blue < 60, $"Expected green, got {red},{green},{blue}.");
        (red, green, blue) = Pixel(rendered, 125, 275);
        Assert.True(blue > 200 && red < 60 && green < 60, $"Expected blue, got {red},{green},{blue}.");
        Assert.Contains(PdfContentRedaction.Verify(document.Source, [region], [], expectMetadataCleared: false).Findings,
            finding => finding.Code == "ImageInRegion");
    }

    [Fact]
    public void PaintsTheSoftMaskOpaqueUnderThePaintedPixels()
    {
        // The top-left pixel is fully transparent, so only an opaque mask lets the fill show there.
        PdfDocument document = PdfDocument.Open(CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>",
            Stream("q 300 0 0 300 50 50 cm /Im1 Do Q"),
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 6 0 R /Length 12 >>\nstream\n"
                + Encoding.Latin1.GetString([255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255]) + "\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceGray /BitsPerComponent 8 /Decode [0 1] /Length 4 >>\nstream\n"
                + Encoding.Latin1.GetString([0, 255, 255, 255]) + "\nendstream"
        ]));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(60, 210, 120, 300));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        Assert.Empty(result.Plan.RasterPages);
        PdfRenderedPage rendered = new PdfPageRenderer(PdfDocument.Open(result.Document))
            .Render(0, new PdfRenderOptions(400, 400));
        Assert.True(IsBlack(rendered, 90, 150));
        (byte red, byte green, byte blue) = Pixel(rendered, 275, 125);
        Assert.True(green > 200 && red < 60 && blue < 60, $"Expected green, got {red},{green},{blue}.");
    }

    [Fact]
    public void RemovesIntersectingAnnotationsMarkedReplacementTextAndMetadata()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R 7 0 R] >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td /Span << /ActualText (SECRETWORD) >> BDC (SECRETWORD) Tj EMC ( visible) Tj ET"),
            "<< /Type /Annot /Subtype /Link /Rect [70 690 160 715] /A << /S /URI /URI (https://secretlink.example) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [300 100 350 120] /A << /S /URI /URI (https://kept.example) >> >>",
            "<< /Title (Confidential title) >>"
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects, info: 8));
        PdfRedactionRegion region = Region(document, 0, "SECRETWORD");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ProhibitedText = ["SECRETWORD", "secretlink", "Confidential title"] });

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Equal(1, Assert.Single(result.Plan.Pages).RemovedAnnotations);
        Assert.Contains("visible", Letters(output, 0));
        Assert.Single(PdfLinkReader.ReadPage(output, 0));
        Assert.False(output.Trailer.ContainsKey(new PdfName("Info"u8)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SearchFindsAndRemovesTextShownThroughFieldsAndAnnotations(bool cropped)
    {
        // Field values and FreeText notes are drawn from their own appearances, not the page content,
        // so a search of page words alone cannot see them.
        string crop = cropped ? "/CropBox [50 40 562 752]" : string.Empty;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] {crop} /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R 7 0 R 8 0 R] >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Plain visible text) Tj ET"),
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (ssn) /V (123-45-6789) /Rect [100 600 300 620] /P 3 0 R /F 4 >>",
            "<< /Type /Annot /Subtype /FreeText /Contents (Call SECRETNOTE today) /DA (/F1 10 Tf 0 g) /Rect [100 500 300 540] /F 4 >>",
            "<< /Type /Annot /Subtype /Text /Contents (Unrelated note) /Rect [400 100 420 120] >>"
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfPageContent page = new PdfPageContentReader(document).Read(0);
        var ssn = new PdfRedactionSearchOptions { Kind = PdfRedactionSearchKind.SocialSecurityNumber };
        var note = new PdfRedactionSearchOptions { Kind = PdfRedactionSearchKind.ExactText, Query = "SECRETNOTE" };
        Assert.Empty(PdfRedactionSearch.Find([page], ssn).Matches);
        Assert.Empty(PdfRedactionSearch.Find([page], note).Matches);

        PdfRedactionMatch field = Assert.Single(PdfRedactionSearch.FindInAnnotations(document, ssn).Matches);
        PdfRedactionMatch freeText = Assert.Single(PdfRedactionSearch.FindInAnnotations(document, note).Matches);
        Assert.Equal(PdfRedactionTargetKind.FormField, field.TargetKind);
        Assert.Equal(PdfRedactionTargetKind.Comment, freeText.TargetKind);
        PdfRedactionRegion[] regions = [new(field.PageIndex, field.Bounds), new(freeText.PageIndex, freeText.Bounds)];

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, regions,
            new PdfContentRedactionOptions { ProhibitedText = ["123-45-6789", "SECRETNOTE"] });

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Equal("Unrelated note", Assert.Single(PdfCommentReader.Read(output)).Contents);
        Assert.Empty(PdfFormWidgetReader.ReadPage(output, 0));
        Assert.Contains("visible", Letters(output, 0));
    }

    [Fact]
    public void FieldRedactionDropsTheXfaPacketThatMirrorsTheFieldValue()
    {
        // A hybrid form keeps every field value in its XFA datasets as well, so the packet goes with the field.
        PdfDocument document = PdfDocument.Open(HybridFormPdf());
        var search = new PdfRedactionSearchOptions { Kind = PdfRedactionSearchKind.ExactText, Query = "SECRETWORD" };
        PdfRedactionMatch field = Assert.Single(PdfRedactionSearch.FindInAnnotations(document, search).Matches);

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [new(field.PageIndex, field.Bounds)],
            new PdfContentRedactionOptions { ProhibitedText = ["SECRETWORD"] });

        Assert.True(result.Verification.Succeeded);
        Assert.True(result.Plan.RemovesXfaForm);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.False(AcroForm(output).ContainsKey(new PdfName("XFA"u8)));
        Assert.DoesNotContain("SECRETWORD", Encoding.Latin1.GetString(PdfDocumentWriterRaw(output)));
    }

    [Fact]
    public void VerifierReportsAnXfaPacketLeftInTheOutput()
    {
        PdfRedactionVerificationReport report = PdfContentRedaction.Verify(HybridFormPdf(),
            [new PdfRedactionRegion(0, new PdfContentBounds(100, 600, 300, 620))], [], expectMetadataCleared: false);

        Assert.Contains(report.Findings, finding => finding.Code == "XfaFormData");
    }

    private static byte[] HybridFormPdf() => CreatePdf([
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] /XFA [(datasets) 7 0 R] >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R] >>",
        Helvetica,
        Stream("BT /F1 12 Tf 72 700 Td (Plain visible text) Tj ET"),
        "<< /Type /Annot /Subtype /Widget /FT /Tx /T (ssn) /V (SECRETWORD) /Rect [100 600 300 620] /P 3 0 R /F 4 >>",
        Stream("<xfa:datasets xmlns:xfa=\"http://www.xfa.org/schema/xfa-data/1.0/\"><xfa:data><form><ssn>SECRETWORD</ssn></form></xfa:data></xfa:datasets>")
    ]);

    private static PdfDictionary AcroForm(PdfDocument document)
    {
        PdfObject acroForm = PdfPageTree.Read(document).Catalog[new PdfName("AcroForm"u8)];
        if (acroForm is PdfIndirectReference reference) acroForm = document.Resolve(reference);
        return Assert.IsType<PdfDictionary>(acroForm);
    }

    [Fact]
    public void RemovingOneWidgetOfAMultiWidgetFieldClearsTheValueOnItsParent()
    {
        // The value of a field with several widgets lives on the field, not on the widget that is removed.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [7 0 R 8 0 R] >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Patient NAMEHERE) Tj ET"),
            "<< /FT /Tx /T (ssn) /V (PARENTSECRET) /DV (PARENTSECRET) /Kids [7 0 R 8 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /P 3 0 R /Rect [140 690 260 712] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 6 0 R /P 3 0 R /Rect [300 100 400 120] >>"
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfRedactionRegion region = Region(document, 0, "NAMEHERE");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ProhibitedText = ["PARENTSECRET"] });

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        PdfFormWidgetInfo remaining = Assert.Single(PdfFormWidgetReader.ReadPage(output, 0));
        Assert.Equal("ssn", remaining.FieldName);
        Assert.Equal(string.Empty, remaining.Value);
        Assert.Equal(string.Empty, remaining.DefaultValue);
    }

    [Theory]
    [InlineData("[0 0 0 0]")]
    [InlineData("[100 100 100 100]")]
    public void SearchProposesZeroSizeHiddenFieldsThroughAOnePointAreaThatRemovesThem(string rect)
    {
        // Hidden and calculated fields often carry an empty rectangle, and they hold values the page never shows.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R] >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Plain visible text) Tj ET"),
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (ssn) /V (123-45-6789) /Rect {rect} /P 3 0 R /F 2 >>"
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        var ssn = new PdfRedactionSearchOptions { Kind = PdfRedactionSearchKind.SocialSecurityNumber };

        PdfRedactionMatch field = Assert.Single(PdfRedactionSearch.FindInAnnotations(document, ssn).Matches);

        Assert.Equal(PdfRedactionTargetKind.FormField, field.TargetKind);
        Assert.True(field.Bounds.Width > 0 && field.Bounds.Height > 0, "The proposed area must have a size.");
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [new(field.PageIndex, field.Bounds)],
            new PdfContentRedactionOptions { ProhibitedText = ["123-45-6789"] });
        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Empty(PdfFormWidgetReader.ReadPage(output, 0));
        Assert.DoesNotContain("123-45-6789", Encoding.Latin1.GetString(PdfDocumentWriterRaw(output)));
    }

    [Fact]
    public void RemovesAnOrphanWidgetOfTheRedactedPageThatIsMissingFromAnnots()
    {
        // A widget that only the field tree knows cannot be placed, so it leaves with its value instead of blocking the page.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Patient NAMEHERE) Tj ET"),
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (orphan) /P 3 0 R /V (SECRETWORD) >>"
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfRedactionRegion region = Region(document, 0, "NAMEHERE");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ProhibitedText = ["SECRETWORD"] });

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        PdfObject fields = AcroForm(output)[new PdfName("Fields"u8)];
        if (fields is PdfIndirectReference reference) fields = output.Resolve(reference);
        Assert.Empty(Assert.IsType<PdfArray>(fields));
        Assert.DoesNotContain(PdfContentRedaction.Verify(result.Document, [region], []).Findings,
            finding => finding.Code == "FieldInRegion");
    }

    [Fact]
    public void SearchSkipsAndReportsAMalformedAnnotationInsteadOfFailing()
    {
        // A reversed rectangle names two opposite corners and is normalized; a nonstring /T is skipped and reported.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 8 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R 7 0 R] >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Plain visible text) Tj ET"),
            "<< /Type /Annot /Subtype /FreeText /Contents (Call SECRETNOTE today) /DA (/F1 10 Tf 0 g) /Rect [300 540 100 500] /F 4 >>",
            "<< /Type /Annot /Subtype /Text /Contents (SECRETNOTE in a broken note) /T 5 /Rect [400 100 420 120] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [9 0 R] >>",
            "<< /Type /Annot /Subtype /Text /Contents (Second page SECRETNOTE) /Rect [10 10 30 30] >>"
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        var note = new PdfRedactionSearchOptions { Kind = PdfRedactionSearchKind.ExactText, Query = "SECRETNOTE" };

        PdfRedactionReview review = PdfRedactionSearch.FindInAnnotations(document, note);

        Assert.Equal(["annotation:0:0", "annotation:1:0"], review.Matches.Select(match => match.Id));
        Assert.Equal(new PdfContentBounds(100, 500, 300, 540), review.Matches[0].Bounds);
        Assert.Equal([0], review.UnreadableAnnotationPages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/Rect [70 690 160]")]
    [InlineData("/Rect [70 690 (wide) 715]")]
    [InlineData("/Rect 9 0 R")]
    public void RemovesAnnotationsWithoutAUsableRectangleFromARedactedPage(string rect)
    {
        // An annotation that cannot be placed cannot be shown to be clear of the area, so it leaves the page.
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [6 0 R 7 0 R] >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (SECRETWORD) Tj ( visible) Tj ET"),
            $"<< /Type /Annot /Subtype /Text {rect} /Contents (SECRETWORD note) /T (SECRETWORD author) >>",
            "<< /Type /Annot /Subtype /Link /Rect [300 100 350 120] /A << /S /URI /URI (https://kept.example) >> >>",
            "<< /Title (Plain) >>",
            "(not a rectangle)"
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfRedactionRegion region = Region(document, 0, "SECRETWORD");

        Assert.Contains(PdfContentRedaction.Verify(document.Source, [region], [], expectMetadataCleared: false).Findings,
            finding => finding.Code == "AnnotationInRegion");
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ProhibitedText = ["SECRETWORD"] });

        Assert.True(result.Verification.Succeeded);
        Assert.Equal(1, Assert.Single(result.Plan.Pages).RemovedAnnotations);
        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Single(PdfLinkReader.ReadPage(output, 0));
        PdfObject annots = PdfPageTree.Read(output).Pages[0].Dictionary[new PdfName("Annots"u8)];
        if (annots is PdfIndirectReference reference) annots = output.Resolve(reference);
        Assert.Single(Assert.IsType<PdfArray>(annots));
    }

    [Fact]
    public void StripsAlternateTextFromTaggedContentAndRemovesCoveredFormFields()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /AcroForm << /Fields [8 0 R 9 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [8 0 R 9 0 R] >>",
            Helvetica,
            Stream("/P << /MCID 0 >> BDC BT /F1 12 Tf 72 700 Td (Patient TAGSECRET) Tj ET EMC " +
                "/P << /MCID 1 >> BDC BT /F1 12 Tf 72 600 Td (Public line) Tj ET EMC"),
            "<< /Type /StructTreeRoot /K [7 0 R 10 0 R] >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /Alt (Alt mentions TAGSECRET) /K 0 >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (ssn) /V (FIELDVALUE99) /Rect [140 690 260 712] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (other) /V (keep) /Rect [300 100 400 120] >>",
            "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /Alt (Kept description) /K 1 >>"
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfRedactionRegion region = Region(document, 0, "TAGSECRET");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ProhibitedText = ["TAGSECRET", "FIELDVALUE99"] });

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.Contains("Patient", Letters(output, 0));
        string raw = Encoding.Latin1.GetString(PdfDocumentWriterRaw(output));
        Assert.Contains("Kept description", raw);
        Assert.Contains("(keep)", raw);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(70)]
    public void RemovesCoveredWidgetsBeneathDeepFieldHierarchies(int ancestors)
    {
        PdfDocument document = PdfDocument.Open(DeepFieldPdf(ancestors));
        PdfRedactionRegion region = Region(document, 0, "NAMEHERE");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        string raw = Encoding.Latin1.GetString(PdfDocumentWriterRaw(PdfDocument.Open(result.Document)));
        Assert.DoesNotContain("DEEPSECRET", raw);
        Assert.DoesNotContain("(a0)", raw);
    }

    [Fact]
    public void RefusesFieldHierarchiesTooDeepToPrune()
    {
        PdfDocument document = PdfDocument.Open(DeepFieldPdf(200));
        PdfRedactionRegion region = Region(document, 0, "NAMEHERE");

        var error = Assert.Throws<NotSupportedException>(() => PdfContentRedaction.Apply(document, [region]));
        Assert.Contains("too deep", error.Message);
    }

    [Fact]
    public void VerifierReportsAFieldWidgetLeftOverARedactedArea()
    {
        PdfDocument document = PdfDocument.Open(DeepFieldPdf(1));
        PdfRedactionRegion region = Region(document, 0, "NAMEHERE");

        PdfRedactionVerificationReport report = PdfContentRedaction.Verify(DeepFieldPdf(1), [region], []);

        Assert.Contains(report.Findings, finding => finding.Code == "FieldInRegion");
    }

    // A widget holding DEEPSECRET sits beneath a chain of nonterminal fields and over NAMEHERE.
    private static byte[] DeepFieldPdf(int ancestors)
    {
        int widget = 6 + ancestors;
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [6 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R /Annots [{widget} 0 R] >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Patient NAMEHERE) Tj ET")
        };
        for (int level = 0; level < ancestors; level++)
        {
            string parent = level == 0 ? string.Empty : $"/Parent {5 + level} 0 R ";
            objects.Add($"<< /T (a{level}) {parent}/Kids [{7 + level} 0 R] >>");
        }
        objects.Add($"<< /Type /Annot /Subtype /Widget /FT /Tx /Parent {widget - 1} 0 R /P 3 0 R /V (DEEPSECRET) /Rect [140 690 260 712] >>");
        return CreatePdf(objects);
    }

    [Theory]
    [InlineData("/Filter /KillerUnknownFilter", "HIDDENSECRET")]
    [InlineData("/Filter /FlateDecode", "HIDDENSECRET is not zlib")]
    public void ProhibitedTextVerificationFailsWhenARetainedStreamCannotBeInspected(string entries, string data)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /KillerExtra 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Patient NAMEHERE) Tj ET"),
            Stream(data, entries)
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfRedactionRegion region = Region(document, 0, "NAMEHERE");

        var error = Assert.Throws<PdfRedactionVerificationException>(() => PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ProhibitedText = ["HIDDENSECRET"] }));
        Assert.Contains(error.Report.Findings, finding => finding.Code == "ObjectInspectionFailure");
    }

    [Fact]
    public void ProhibitedTextVerificationFailsWhenAStreamExceedsTheDecodeBudget()
    {
        byte[] plain = Encoding.Latin1.GetBytes("HIDDENSECRET" + new string(' ', 8192));
        using var packed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(plain);
        string data = Encoding.Latin1.GetString(packed.ToArray());
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /KillerExtra 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream("BT /F1 12 Tf 72 700 Td (Patient NAMEHERE) Tj ET"),
            Stream(data, "/Filter /FlateDecode")
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfRedactionRegion region = Region(document, 0, "NAMEHERE");
        int limit = PdfContentRedaction.InspectionDecodeLimit;
        PdfContentRedaction.InspectionDecodeLimit = 1024;
        try
        {
            var error = Assert.Throws<PdfRedactionVerificationException>(() => PdfContentRedaction.Apply(document, [region],
                new PdfContentRedactionOptions { ProhibitedText = ["HIDDENSECRET"] }));
            Assert.Contains(error.Report.Findings, finding => finding.Code == "ObjectInspectionFailure"
                && finding.Message.Contains("limit", StringComparison.OrdinalIgnoreCase));
        }
        finally { PdfContentRedaction.InspectionDecodeLimit = limit; }
    }

    private static byte[] PdfDocumentWriterRaw(PdfDocument document) =>
        KillerPdf.Engine.Writing.PdfDocumentWriter.Write(document, new KillerPdf.Engine.Writing.PdfDocumentWriteOptions());

    [Fact]
    public void RejectsRegionsOutsideTheDocument()
    {
        PdfDocument document = PdfDocument.Open(SinglePage("BT /F1 12 Tf 72 700 Td (x) Tj ET"));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfContentRedaction.Analyze(document,
            [new PdfRedactionRegion(1, new PdfContentBounds(0, 0, 10, 10))]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfContentRedaction.Analyze(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(10, 10, 10, 20))]));
    }

    [Fact]
    public void CoveredTextGoesWhileDrawingsImagesAndNeighborsStay()
    {
        PdfDocument document = PdfDocument.Open(ImagePage(
            "BT /F1 12 Tf 72 300 Td (OLDTEXT) Tj ET BT /F1 12 Tf 72 200 Td (Keep) Tj ET " +
            "q 0 0 1 rg 60 290 120 30 re f Q q 20 0 0 20 100 295 cm /Im1 Do Q", rotate: 0));
        PdfRedactionRegion region = Region(document, 0, "OLD");

        byte[]? updated = PdfContentRedaction.RemoveCoveredText(document, [region]);

        Assert.NotNull(updated);
        PdfDocument output = PdfDocument.Open(updated);
        Assert.True(output.CrossReferences.Sections.Count > 1, "the change is an incremental update");
        // Only glyphs centered under the cover go; the rest of the word keeps its place.
        Assert.Equal("TEXTKeep", Letters(output, 0));
        AssertKeptLettersUnmoved(document, output, 0, [region]);
        Assert.Single(new PdfPageContentReader(output).Read(0).Images);
        PdfRenderedPage rendered = new PdfPageRenderer(output).Render(0, new PdfRenderOptions(400, 400));
        (byte red, byte green, byte blue) = Pixel(rendered, 65, 105);
        Assert.True(blue > 200 && red < 60 && green < 60, $"Expected the blue fill, got {red},{green},{blue}.");
    }

    [Fact]
    public void CoveredTextInsideAFormIsRemovedOnlyForThatPage()
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm1 6 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /XObject << /Fm1 6 0 R >> >> /Contents 5 0 R >>",
            Stream("q 1 0 0 1 100 500 cm /Fm1 Do Q"),
            Stream("BT /F1 12 Tf 10 10 Td (Shared EARLIEREDIT text) Tj ET",
                "/Type /XObject /Subtype /Form /BBox [0 0 400 100] /Resources << /Font << /F1 7 0 R >> >>"),
            Helvetica
        };
        PdfDocument document = PdfDocument.Open(CreatePdf(objects));
        PdfRedactionRegion region = Region(document, 0, "EARLIEREDIT");

        PdfDocument output = PdfDocument.Open(PdfContentRedaction.RemoveCoveredText(document, [region])!);

        Assert.DoesNotContain("EARLIEREDIT", Letters(output, 0));
        Assert.Contains("Shared", Letters(output, 0));
        Assert.Contains("EARLIEREDIT", Letters(output, 1));
        AssertKeptLettersUnmoved(document, output, 0, [region]);
    }

    [Fact]
    public void NothingUnderTheCoverLeavesTheDocumentAlone()
    {
        PdfDocument document = PdfDocument.Open(SinglePage("BT /F1 12 Tf 72 700 Td (Untouched) Tj ET"));

        Assert.Null(PdfContentRedaction.RemoveCoveredText(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(300, 100, 400, 150))]));
    }

    [Fact]
    public void FillsTheAreaWithItsColorAndDrawsTheOverlayTextAsRealText()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 12 Tf 72 700 Td (Account SECRET42 closed) Tj ET"));
        PdfRedactionRegion region = Region(document, 0, "SECRET42") with
        {
            OverlayText = "(b)(6)",
            FillColor = new PdfRgbColor(1, 0, 0)
        };

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ProhibitedText = ["SECRET42"] });

        Assert.True(result.Verification.Succeeded);
        PdfDocument output = PdfDocument.Open(result.Document);
        string text = Letters(output, 0);
        Assert.Contains("(b)(6)", text);
        Assert.Contains("Account", text);
        Assert.DoesNotContain("SECRET", text);
        Assert.All(new PdfPageContentReader(output).Read(0).Letters
            .Where(letter => PdfContentRedaction.Overlaps(letter.BoundingBox, region.Bounds)),
            letter => Assert.Contains(letter.Value, "(b)(6)"));
        PdfRenderedPage rendered = new PdfPageRenderer(output).Render(0, new PdfRenderOptions(612, 792));
        double cy = (region.Bounds.Bottom + region.Bounds.Top) / 2;
        (byte red, byte green, byte blue) = Pixel(rendered, (int)region.Bounds.Left + 1, 792 - (int)cy);
        Assert.True(red > 200 && green < 60 && blue < 60, $"Expected red, got {red},{green},{blue}.");
    }

    [Fact]
    public void OverlayTextReadsUprightOnRotatedPagesAndFallsBackToTheReasonCode()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 12 Tf 110 400 Td (HIDDEN) Tj ET", "/MediaBox [0 0 612 792] /Rotate 90"));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(100, 300, 160, 500), ReasonCode: "(b)(7)(C)");

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        Assert.True(result.Verification.Succeeded);
        PdfExtractedLetter[] overlay = [.. new PdfPageContentReader(PdfDocument.Open(result.Document)).Read(0).Letters];
        Assert.Equal("(b)(7)(C)", string.Concat(overlay.Select(letter => letter.Value)));
        Assert.All(overlay, letter =>
        {
            Assert.Equal(PdfWritingDirection.BottomToTop, letter.WritingDirection);
            Assert.True(PdfContentRedaction.Overlaps(letter.BoundingBox, region.Bounds));
        });
    }

    [Fact]
    public void VerificationRejectsTextThatIsNotTheOverlayText()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 12 Tf 72 700 Td (Account SECRET42 closed) Tj ET"));
        PdfRedactionRegion region = Region(document, 0, "SECRET42") with { OverlayText = "(b)(6)" };

        PdfRedactionVerificationReport report = PdfContentRedaction.Verify(document.Source, [region], [],
            expectMetadataCleared: false);

        Assert.Contains(report.Findings, finding => finding.Code == "TextInRegion");
        Assert.True(PdfContentRedaction.SupportsOverlayText("Caf\u00e9 (b)(6)"));
        Assert.False(PdfContentRedaction.SupportsOverlayText("\u79d8\u5bc6"));
    }

    private static void AssertKeptLettersUnmoved(PdfDocument before, PdfDocument after, int pageIndex,
        IReadOnlyList<PdfRedactionRegion> regions)
    {
        var expected = new PdfPageContentReader(before).Read(pageIndex).Letters
            .Where(letter => !regions.Any(region => PdfContentRedaction.Overlaps(letter.BoundingBox, region.Bounds)))
            .Select(letter => (letter.Value, Math.Round(letter.StartBaseLine.X, 3), Math.Round(letter.StartBaseLine.Y, 3)))
            .ToArray();
        var actual = new PdfPageContentReader(after).Read(pageIndex).Letters
            .Select(letter => (letter.Value, Math.Round(letter.StartBaseLine.X, 3), Math.Round(letter.StartBaseLine.Y, 3)))
            .ToArray();
        Assert.Equal(expected, actual);
    }

    private static PdfRedactionRegion Region(PdfDocument document, int pageIndex, string word, int occurrence = 0)
    {
        IReadOnlyList<PdfExtractedLetter> letters = new PdfPageContentReader(document).Read(pageIndex).Letters;
        string text = string.Concat(letters.Select(letter => letter.Value));
        int start = -1;
        for (int index = 0; index <= occurrence; index++)
            start = text.IndexOf(word, start + 1, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{word}' was not found.");
        var selected = new List<PdfExtractedLetter>();
        int position = 0;
        foreach (PdfExtractedLetter letter in letters)
        {
            if (position < start + word.Length && position + letter.Value.Length > start) selected.Add(letter);
            position += letter.Value.Length;
        }
        return new PdfRedactionRegion(pageIndex, new PdfContentBounds(
            selected.Min(letter => letter.BoundingBox.Left), selected.Min(letter => letter.BoundingBox.Bottom),
            selected.Max(letter => letter.BoundingBox.Right), selected.Max(letter => letter.BoundingBox.Top)));
    }

    private static string Letters(PdfDocument document, int pageIndex) =>
        string.Concat(new PdfPageContentReader(document).Read(pageIndex).Letters.Select(letter => letter.Value));

    private static bool HasSourceImage(PdfDocument document)
    {
        foreach (var entry in document.CrossReferences.Values)
        {
            if (entry.Type is not (PdfCrossReferenceEntryType.InUse
                or PdfCrossReferenceEntryType.Compressed)) continue;
            if (document.Resolve(entry.ObjectNumber) is PdfStream stream
                && stream.Dictionary.TryGetValue(new PdfName("Width"u8), out PdfObject? width)
                && width is PdfInteger { Value: 2 }) return true;
        }
        return false;
    }

    private static (byte Red, byte Green, byte Blue) Pixel(PdfRenderedPage page, int x, int y)
    {
        int offset = (y * page.Width + x) * 4;
        ReadOnlySpan<byte> pixels = page.Pixels.Span;
        return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    private static bool IsBlack(PdfRenderedPage page, int x, int y)
    {
        (byte red, byte green, byte blue) = Pixel(page, x, y);
        return red < 40 && green < 40 && blue < 40;
    }

    private static byte[] SinglePage(string content, string pageEntries = "/MediaBox [0 0 612 792]") =>
        CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R {pageEntries} /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream(content)
        ]);

    private static byte[] ImagePage(string content, int rotate, string imageEntries = "") =>
        CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 400] /Rotate {rotate} /Resources << /Font << /F1 4 0 R >> /XObject << /Im1 6 0 R >> >> /Contents 5 0 R >>",
            Helvetica,
            Stream(content),
            $"<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /ColorSpace /DeviceRGB /BitsPerComponent 8 {imageEntries} /Length 12 >>\nstream\n"
                + Encoding.Latin1.GetString([255, 0, 0, 0, 255, 0, 0, 0, 255, 255, 255, 255]) + "\nendstream"
        ]);

    private static string Stream(string content, string entries = "") =>
        $"<< {entries} /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static byte[] CreatePdf(List<string> objects, int? info = null)
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
        string infoEntry = info is int number ? $" /Info {number} 0 R" : string.Empty;
        pdf.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R{infoEntry} >>\n")
            .Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
