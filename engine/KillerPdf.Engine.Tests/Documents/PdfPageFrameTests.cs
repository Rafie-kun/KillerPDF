using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Rendering;
using System.Text;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

/// <summary>
/// A crop box can reach past the media box. The viewer, the renderer, the reader and the redaction
/// rewriter must then all use the part of the crop box the media box holds.
/// </summary>
public sealed class PdfPageFrameTests
{
    private const string Helvetica = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>";
    private const string SecretLine = "BT /F1 12 Tf 100 600 Td (SECRETTEXT) Tj ET\nBT /F1 12 Tf 100 300 Td (PUBLICTEXT) Tj ET";

    public static TheoryData<string, double, double> CropsPastTheMedia => new()
    {
        // Past the media box on every side: the shape of the fixture from the review.
        { "/MediaBox [0 0 612 792] /CropBox [-60 -60 672 852]", 0, 0 },
        // The shape of GWG180-184_16Bit_Images_README.pdf.
        { "/MediaBox [0 0 612 792] /CropBox [-8.5 -8.5 620.5 800.5]", 0, 0 },
        // The shape of evince-LINK-516-0.pdf: the media box starts inside a crop box at the origin.
        { "/MediaBox [50 40 562 752] /CropBox [0 0 612 792]", 50, 40 }
    };

    [Theory]
    [MemberData(nameof(CropsPastTheMedia))]
    public void AViewerFrameRegionRemovesTheWordAndTheCoverLandsOnIt(string boxes, double left, double bottom)
    {
        PdfDocument document = PdfDocument.Open(SinglePage(SecretLine, boxes));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(98 - left, 598 - bottom, 190 - left, 612 - bottom));

        PdfContentRedactionPlan plan = PdfContentRedaction.Analyze(document, [region]);
        Assert.Equal(10, Assert.Single(plan.Pages).RemovedGlyphs);

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region]);

        PdfDocument output = PdfDocument.Open(result.Document);
        Assert.True(result.Verification.Succeeded);
        string text = new PdfPageContentReader(output).Read(0).Text;
        Assert.DoesNotContain("SECRETTEXT", text, StringComparison.Ordinal);
        Assert.Contains("PUBLICTEXT", text, StringComparison.Ordinal);
        Assert.False(ContainsText(output, "SECRETTEXT"));
        PdfPageInformation page = Assert.Single(PdfPageInformation.Read(output));
        PdfRenderedPage rendered = new PdfPageRenderer(output).Render(0,
            new PdfRenderOptions((int)Math.Round(page.Width), (int)Math.Round(page.Height)) { CacheResult = false });
        Assert.True(IsBlack(rendered, (int)(130 - left), rendered.Height - (int)(605 - bottom)),
            "The cover is not where the region was marked.");
    }

    [Theory]
    [MemberData(nameof(CropsPastTheMedia))]
    public void SearchRectanglesReaderAndRendererAgree(string boxes, double left, double bottom)
    {
        PdfDocument document = PdfDocument.Open(SinglePage(SecretLine, boxes));
        PdfPageInformation information = Assert.Single(PdfPageInformation.Read(document));
        PdfPageContent content = new PdfPageContentReader(document).Read(0);

        Assert.Equal(information.Width, content.Width);
        Assert.Equal(information.Height, content.Height);
        PdfRedactionMatch match = Assert.Single(PdfRedactionSearch.Find([content], new PdfRedactionSearchOptions
        {
            Kind = PdfRedactionSearchKind.ExactText,
            Query = "SECRETTEXT"
        }).Matches);
        Assert.InRange(match.Bounds.Left, 100 - left, 101 - left);
        Assert.InRange(match.Bounds.Bottom, 590 - bottom, 601 - bottom);

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions((int)Math.Round(information.Width), (int)Math.Round(information.Height)) { CacheResult = false });
        (int inkLeft, int inkRight, int inkTop, int inkBottom) = InkExtent(rendered, 0, rendered.Height / 2);
        Assert.InRange(inkLeft, match.Bounds.Left - 1, match.Bounds.Left + 3);
        Assert.InRange(inkRight, match.Bounds.Right - 6, match.Bounds.Right + 1);
        Assert.InRange(rendered.Height - inkBottom, match.Bounds.Bottom - 1, match.Bounds.Top + 1);
        Assert.InRange(rendered.Height - inkTop, match.Bounds.Bottom - 1, match.Bounds.Top + 1);
    }

    [Theory]
    [MemberData(nameof(CropsPastTheMedia))]
    public void RemovingCoveredTextUsesTheSameFrame(string boxes, double left, double bottom)
    {
        PdfDocument document = PdfDocument.Open(SinglePage(SecretLine, boxes));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(98 - left, 598 - bottom, 190 - left, 612 - bottom));

        byte[]? changed = PdfContentRedaction.RemoveCoveredText(document, [region]);

        Assert.NotNull(changed);
        string text = new PdfPageContentReader(PdfDocument.Open(changed)).Read(0).Text;
        Assert.DoesNotContain("SECRETTEXT", text, StringComparison.Ordinal);
        Assert.Contains("PUBLICTEXT", text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CropsPastTheMedia))]
    public void ARasterPageIsDrawnToTheSameFrame(string boxes, double left, double bottom)
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "0 0 1 rg 200 500 100 100 re f 0 g\n" + SecretLine, boxes));
        var region = new PdfRedactionRegion(0, new PdfContentBounds(98 - left, 598 - bottom, 190 - left, 612 - bottom));
        PdfPageInformation information = Assert.Single(PdfPageInformation.Read(document));
        var options = new PdfRenderOptions((int)Math.Round(information.Width), (int)Math.Round(information.Height)) { CacheResult = false };
        PdfRenderedPage before = new PdfPageRenderer(document).Render(0, options);

        PdfContentRedactionResult result = PdfContentRedaction.Apply(document, [region],
            new PdfContentRedactionOptions { ApprovedRasterPages = [0], RasterDpi = 72 });

        PdfRenderedPage after = new PdfPageRenderer(PdfDocument.Open(result.Document)).Render(0, options);
        int x = (int)(250 - left);
        foreach (int y in new[] { after.Height - (int)(505 - bottom), after.Height - (int)(595 - bottom) })
            Assert.Equal(Pixel(before, x, y), Pixel(after, x, y));
        Assert.Equal(Pixel(before, x + 60, after.Height - (int)(550 - bottom)),
            Pixel(after, x + 60, after.Height - (int)(550 - bottom)));
    }

    [Fact]
    public void ACropBoxOutsideTheMediaBoxKeepsItsOwnFrame()
    {
        PdfDocument document = PdfDocument.Open(SinglePage(
            "BT /F1 12 Tf 720 720 Td (SECRETTEXT) Tj ET", "/MediaBox [0 0 612 792] /CropBox [700 700 900 900]"));

        PdfPageInformation information = Assert.Single(PdfPageInformation.Read(document));
        PdfPageContent content = new PdfPageContentReader(document).Read(0);

        Assert.Equal((700d, 700d, 200d, 200d),
            (information.Left, information.Bottom, information.Width, information.Height));
        Assert.Equal(200, content.Width);
        Assert.InRange(content.Letters[0].BoundingBox.Left, 20, 21);
        PdfContentRedactionResult result = PdfContentRedaction.Apply(document,
            [new PdfRedactionRegion(0, new PdfContentBounds(15, 15, 120, 35))]);
        Assert.True(result.Verification.Succeeded);
        Assert.Empty(new PdfPageContentReader(PdfDocument.Open(result.Document)).Read(0).Letters);
    }

    [Fact]
    public void ADamagedCropBoxReadsOnTheMediaBoxUnderRecovery()
    {
        byte[] source = SinglePage(SecretLine, "/MediaBox [10 20 622 812] /CropBox [5 5 5 5]");
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(source);

        PdfPageInformation information = Assert.Single(PdfPageInformation.Read(document));
        PdfPageContent content = new PdfPageContentReader(document).Read(0);

        Assert.Equal(information.Width, content.Width);
        Assert.Equal(information.Height, content.Height);
        Assert.InRange(content.Letters[0].BoundingBox.Left, 100 - information.Left, 101 - information.Left);
    }

    private static PdfPageContent ReadUnderRecovery(string contentsEntry, params string[] extraObjects)
    {
        byte[] source = CreatePdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /XObject << /Fm 5 0 R >> >> {contentsEntry} >>",
            Helvetica,
            .. extraObjects
        ]);
        return new PdfPageContentReader(PdfDocument.OpenWithCompatibilityRecovery(source)).Read(0);
    }

    private static string FlateStream(byte[] encoded, string entries = "") =>
        $"<< /Filter /FlateDecode {entries} /Length {encoded.Length} >>\nstream\n{Encoding.Latin1.GetString(encoded)}\nendstream";

    private static byte[] Compressed(string content)
    {
        using var output = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(Encoding.Latin1.GetBytes(content));
        return output.ToArray();
    }

    private const string FormEntries = "/Type /XObject /Subtype /Form /BBox [0 0 200 200]";

    [Fact]
    public void AStreamThatCannotInflateIsReportedRatherThanReadAsEmpty()
    {
        PdfPageContent page = ReadUnderRecovery("/Contents 6 0 R", "null", FlateStream("not-flate-xx"u8.ToArray()));

        Assert.Empty(page.Letters);
        Assert.Contains(PdfPageContentReader.UndecodableStreamDiagnostic, page.Diagnostics);
    }

    [Fact]
    public void AFormThatCannotInflateIsReportedRatherThanReadAsEmpty()
    {
        PdfPageContent page = ReadUnderRecovery("/Contents 6 0 R",
            FlateStream("not-flate-xx"u8.ToArray(), FormEntries), Stream("/Fm Do"));

        Assert.Contains(PdfPageContentReader.UndecodableStreamDiagnostic, page.Diagnostics);
    }

    [Fact]
    public void AContentEntryThatIsNoStreamIsReported()
    {
        PdfPageContent page = ReadUnderRecovery("/Contents 6 0 R", "null", "<< /NotAStream true >>");

        Assert.Contains(PdfPageContentReader.UndecodableStreamDiagnostic, page.Diagnostics);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnEmptyStreamIsNotReported(bool compressed)
    {
        PdfPageContent page = ReadUnderRecovery("/Contents 6 0 R", "null", FlateStream(compressed ? Compressed("") : []));

        Assert.DoesNotContain(PdfPageContentReader.UndecodableStreamDiagnostic, page.Diagnostics);
    }

    [Fact]
    public void AnEmptyCompressedFormIsNotReported()
    {
        PdfPageContent page = ReadUnderRecovery("/Contents 6 0 R", FlateStream(Compressed(""), FormEntries), Stream("/Fm Do"));

        Assert.DoesNotContain(PdfPageContentReader.UndecodableStreamDiagnostic, page.Diagnostics);
    }

    private static (byte, byte, byte) Pixel(PdfRenderedPage page, int x, int y)
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

    // The bounds of the dark pixels in a band of rows.
    private static (int Left, int Right, int Top, int Bottom) InkExtent(PdfRenderedPage page, int firstRow, int lastRow)
    {
        int left = int.MaxValue, right = -1, top = int.MaxValue, bottom = -1;
        for (int y = firstRow; y < lastRow; y++)
            for (int x = 0; x < page.Width; x++)
                if (Pixel(page, x, y).Item1 < 128)
                {
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y);
                    bottom = Math.Max(bottom, y);
                }
        return (left, right, top, bottom);
    }

    private static bool ContainsText(PdfDocument document, string text)
    {
        foreach (var entry in document.CrossReferences.Values)
            if (entry.Type is not KillerPdf.Engine.CrossReference.PdfCrossReferenceEntryType.Free
                && document.Resolve(entry.ObjectNumber) is PdfStream stream
                && Encoding.Latin1.GetString(document.DecodeStream(stream)).Contains(text, StringComparison.Ordinal)) return true;
        return false;
    }

    private static string Stream(string content) =>
        $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream";

    private static byte[] SinglePage(string content, string boxes) => CreatePdf([
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R {boxes} /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
        Helvetica,
        $"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}\nendstream"
    ]);

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
