using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using System.Text;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

/// <summary>
/// Type 3 glyphs, tiling patterns, soft-mask groups and annotation appearances without resources of
/// their own paint with the resources of whatever draws them, so those entries must outlive a redaction
/// elsewhere on the page.
/// </summary>
public sealed class PdfRedactionResourceScopeTests
{
    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
    private const string Word = " BT /H 12 Tf 10 150 Td (SECRET) Tj ET";
    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string Pages = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
    private const string TypeThreeWithoutResources = "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 100 100] " +
        "/FontMatrix [0.01 0 0 0.01 0 0] /Encoding << /Type /Encoding /Differences [65 /A] >> " +
        "/FirstChar 65 /LastChar 65 /Widths [100] /CharProcs << /A 6 0 R >> >>";
    private const string DrawsTheImage = "q 100 0 0 100 0 0 cm /Im Do Q";

    public static TheoryData<string> Routes =>
    [
        "type3-Tf", "type3-gsFont", "tiling-noRes", "smask-noRes", "annot-noRes", "form-noRes-nestedT3", "form-ownRes-T3"
    ];

    [Theory]
    [MemberData(nameof(Routes))]
    public void InkPaintedThroughInheritedResourcesOutlivesARedactionElsewhere(string route)
    {
        byte[] source = Route(route, retiredImage: false);
        int before = Ink(PdfDocument.Open(source));
        Assert.True(before > 9000, $"The fixture paints {before} dark pixels.");
        PdfDocument document = PdfDocument.Open(source);
        var region = new PdfRedactionRegion(0, new PdfContentBounds(5, 145, 100, 165));

        Assert.Empty(PdfContentRedaction.Analyze(document, [region]).RasterPages);
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.True(result.Verification.Succeeded);
        Assert.Equal(before, Ink(output));
        Assert.DoesNotContain("SECRET", new PdfPageContentReader(output).Read(0).Text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public void ARetiredImageOnThePageStillLeaves(string route)
    {
        byte[] source = Route(route, retiredImage: true);
        int before = Ink(PdfDocument.Open(source));
        PdfDocument document = PdfDocument.Open(source);
        var region = new PdfRedactionRegion(0, new PdfContentBounds(5, 145, 200, 195));

        Assert.True(HasImageOfWidth(document, 3));
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.True(result.Verification.Succeeded);
        Assert.Equal(before, Ink(output));
        Assert.False(HasImageOfWidth(output, 3), "The retired image is still in the file.");
    }

    [Theory]
    // Everything here brings its own resources, or draws with none.
    [InlineData("/Font << /H 8 0 R /T3 << /Type /Font /Subtype /Type3 /Resources << >> /FontBBox [0 0 1 1] /FontMatrix [1 0 0 1 0 0] /CharProcs << >> >> >>")]
    [InlineData("/Font << /H 8 0 R >> /Pattern << /S << /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 1 1] /Function 5 0 R >> >> >>")]
    [InlineData("/Font << /H 8 0 R >> /ExtGState << /G << /CA 0.5 >> >>")]
    public void ResourcesNothingInheritsAreStillPruned(string resources)
    {
        PdfDocument document = PdfDocument.Open(CreatePdf([
            Catalog, Pages,
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << {resources} /XObject << /Unused 7 0 R >> >> /Contents 4 0 R >>",
            Stream("0 g" + Word), "<< /FunctionType 2 /Domain [0 1] /N 1 >>", "null", Image(0), Helvetica
        ]));

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(5, 145, 100, 165))]);

        Assert.False(HasImageOfWidth(PdfDocument.Open(result.Document), 2), "An unused image stayed in the file.");
    }

    [Fact]
    public void ARedactedNamedDescriptionLeavesWhileTheRestOfTheResourcesStay()
    {
        byte[] source = CreatePdf([
            Catalog, Pages,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 5 0 R /H 8 0 R >> " +
                "/XObject << /Im 7 0 R >> /Properties << /Sensitive 9 0 R /Other 10 0 R >> >> /Contents 4 0 R >>",
            Stream("BT /F1 100 Tf 0 0 Td (A) Tj ET BT /H 12 Tf 10 150 Td /Span /Sensitive BDC (SECRET) Tj EMC ET"),
            TypeThreeWithoutResources, Stream("100 0 d0 /Span /Other BDC " + DrawsTheImage + " EMC"), Image(0), Helvetica,
            "<< /ActualText (NAMEDSECRET) >>",
            "<< /MCID 7 /Lang (en) >>"
        ]);
        int before = Ink(PdfDocument.Open(source));
        PdfDocument document = PdfDocument.Open(source);
        var region = new PdfRedactionRegion(0, new PdfContentBounds(5, 145, 100, 165));

        Assert.Empty(PdfContentRedaction.Analyze(document, [region]).RasterPages);
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.True(result.Verification.Succeeded);
        Assert.Equal(before, Ink(output));
        string objects = string.Concat(output.CrossReferences.Values.Where(entry => entry.Type is PdfCrossReferenceEntryType.InUse
            or PdfCrossReferenceEntryType.Compressed).Select(entry => output.Resolve(entry.ObjectNumber))
            .OfType<PdfDictionary>().Select(item => Encoding.Latin1.GetString(PdfObjectWriter.Write(item))));
        Assert.DoesNotContain("NAMEDSECRET", objects, StringComparison.Ordinal);
        Assert.Contains("/Other", objects, StringComparison.Ordinal);
    }

    // Dark pixels in the corner the route paints, well away from the regions.
    private static int Ink(PdfDocument document)
    {
        PdfRenderedPage page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(200, 200) { CacheResult = false });
        ReadOnlySpan<byte> pixels = page.Pixels.Span;
        int dark = 0;
        for (int y = 100; y < 200; y++)
            for (int x = 0; x < 100; x++)
                if (pixels[(y * 200 + x) * 4] < 128) dark++;
        return dark;
    }

    private static byte[] Route(string name, bool retiredImage)
    {
        // Each route paints the lower left quarter of the page, and the redactions take a line above it.
        string retired = retiredImage ? " /Gone 10 0 R" : string.Empty;
        string gone = retiredImage ? " q 20 0 0 20 150 160 cm /Gone Do Q" : string.Empty;
        string Page(string resources, string extra = "") =>
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << {resources} >> /Contents 4 0 R {extra} >>";
        string dark = Image(0), white = Image(255), goneImage = Image(0, 3);
        List<string> objects = name switch
        {
            "type3-Tf" =>
            [
                Catalog, Pages, Page($"/Font << /F1 5 0 R /H 8 0 R >> /XObject << /Im 7 0 R{retired} >>"),
                Stream("BT /F1 100 Tf 0 0 Td (A) Tj ET" + Word + gone), TypeThreeWithoutResources,
                Stream("100 0 d0 " + DrawsTheImage), dark, Helvetica, "null"
            ],
            "type3-gsFont" =>
            [
                Catalog, Pages, Page($"/ExtGState << /G << /Font [5 0 R 100] >> >> /Font << /H 8 0 R >> /XObject << /Im 7 0 R{retired} >>"),
                Stream("q /G gs BT 0 0 Td (A) Tj ET Q" + Word + gone), TypeThreeWithoutResources,
                Stream("100 0 d0 " + DrawsTheImage), dark, Helvetica, "null"
            ],
            "tiling-noRes" =>
            [
                Catalog, Pages, Page($"/Pattern << /P 6 0 R >> /Font << /H 8 0 R >> /XObject << /Im 7 0 R{retired} >>"),
                Stream("/Pattern cs /P scn 0 0 100 100 re f 0 g" + Word + gone), "null",
                Stream(DrawsTheImage, "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 100 100] /XStep 100 /YStep 100"),
                dark, Helvetica, "null"
            ],
            "smask-noRes" =>
            [
                Catalog, Pages,
                Page($"/ExtGState << /M << /SMask << /Type /Mask /S /Luminosity /G 6 0 R >> >> >> /Font << /H 8 0 R >> /XObject << /Im 7 0 R{retired} >>"),
                Stream("q /M gs 0 g 0 0 100 100 re f Q" + Word + gone), "null",
                Stream(DrawsTheImage, "/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Group << /S /Transparency /CS /DeviceGray >>"),
                white, Helvetica, "null"
            ],
            "annot-noRes" =>
            [
                Catalog, Pages, Page($"/Font << /H 8 0 R >> /XObject << /Im 7 0 R{retired} >>", "/Annots [5 0 R]"),
                Stream("0 g" + Word + gone), "<< /Type /Annot /Subtype /Stamp /Rect [0 0 100 100] /F 4 /AP << /N 6 0 R >> >>",
                Stream(DrawsTheImage, "/Type /XObject /Subtype /Form /BBox [0 0 100 100]"), dark, Helvetica, "null"
            ],
            "form-noRes-nestedT3" =>
            [
                Catalog, Pages, Page($"/Font << /F1 5 0 R /H 8 0 R >> /XObject << /Im 7 0 R /Fm 9 0 R{retired} >>"),
                Stream("/Fm Do" + Word + gone), TypeThreeWithoutResources, Stream("100 0 d0 " + DrawsTheImage), dark, Helvetica,
                Stream("BT /F1 100 Tf 0 0 Td (A) Tj ET", "/Type /XObject /Subtype /Form /BBox [0 0 200 200]")
            ],
            "form-ownRes-T3" =>
            [
                // The form holds both the word and the glyphs, so its own resources are what get rebuilt.
                Catalog, Pages, Page($"/XObject << /Fm 9 0 R{retired} >>"),
                Stream("/Fm Do" + gone), TypeThreeWithoutResources, Stream("100 0 d0 " + DrawsTheImage), dark, Helvetica,
                Stream("BT /F1 100 Tf 0 0 Td (A) Tj ET" + Word, "/Type /XObject /Subtype /Form /BBox [0 0 200 200] " +
                    "/Resources << /Font << /F1 5 0 R /H 8 0 R >> /XObject << /Im 7 0 R >> >>")
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
        if (retiredImage) objects.Add(goneImage);
        return CreatePdf(objects);
    }

    private static string Image(byte value, int width = 2) => Stream(
        new string((char)value, width * 2),
        $"/Type /XObject /Subtype /Image /Width {width} /Height 2 /BitsPerComponent 8 /ColorSpace /DeviceGray");

    private static bool HasImageOfWidth(PdfDocument document, int width)
    {
        foreach (var entry in document.CrossReferences.Values)
            if (entry.Type is PdfCrossReferenceEntryType.InUse or PdfCrossReferenceEntryType.Compressed
                && document.Resolve(entry.ObjectNumber) is PdfStream stream
                && stream.Dictionary.TryGetValue(new PdfName("Width"u8), out PdfObject? value)
                && value is PdfInteger number && number.Value == width) return true;
        return false;
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
