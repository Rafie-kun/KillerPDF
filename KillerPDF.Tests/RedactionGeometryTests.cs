using System.IO;
using System.Text;
using System.Windows;
using KillerPDF.Services;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using PdfRenderedPage = KillerPdf.Engine.Rendering.PdfRenderedPage;
using Xunit;

namespace KillerPDF.Tests;

public sealed class RedactionGeometryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    [InlineData(-90)]
    public void CanvasAndPageRectanglesRoundTrip(int rotation)
    {
        bool quarter = RedactionGeometry.NormalizeRotation(rotation) is 90 or 270;
        double canvasWidth = quarter ? 400 : 600, canvasHeight = quarter ? 600 : 400;
        var drawn = new Rect(37, 52, 120, 33);

        var (left, bottom, right, top) = RedactionGeometry.CanvasToPage(drawn, 300, 200, canvasWidth, canvasHeight, rotation);
        Rect back = RedactionGeometry.PageToCanvas(left, bottom, right, top, 300, 200, canvasWidth, canvasHeight, rotation);

        Assert.Equal(drawn.X, back.X, 6);
        Assert.Equal(drawn.Y, back.Y, 6);
        Assert.Equal(drawn.Width, back.Width, 6);
        Assert.Equal(drawn.Height, back.Height, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void PageRectangleLandsWhereTheEngineDrawsIt(int rotation)
    {
        // A black box at x 50..90, y 40..70 on a 300 by 200 point page, shown at twice its size.
        PdfDocument document = PdfDocument.Open(Pdf(rotation, "0 g 50 40 40 30 re f"));
        bool quarter = rotation is 90 or 270;
        int width = quarter ? 400 : 600, height = quarter ? 600 : 400;
        PdfRenderedPage page = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(width, height));

        Rect box = RedactionGeometry.PageToCanvas(50, 40, 90, 70, 300, 200, width, height, rotation);

        Assert.True(Dark(page, box.X + box.Width / 2, box.Y + box.Height / 2));
        Assert.True(Dark(page, box.X + 3, box.Y + 3));
        Assert.True(Dark(page, box.Right - 3, box.Bottom - 3));
        Assert.False(Dark(page, box.X - 4, box.Y + box.Height / 2));
        Assert.False(Dark(page, box.Right + 4, box.Y + box.Height / 2));
        Assert.False(Dark(page, box.X + box.Width / 2, box.Y - 4));
        Assert.False(Dark(page, box.X + box.Width / 2, box.Bottom + 4));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void CanvasPointsMapLikeTheCornersOfARectangle(int rotation)
    {
        bool quarter = rotation is 90 or 270;
        double canvasWidth = quarter ? 400 : 600, canvasHeight = quarter ? 600 : 400;
        Rect page = RedactionGeometry.PageToCanvas(50, 40, 90, 70, 300, 200, canvasWidth, canvasHeight, rotation);

        var (x, y) = RedactionGeometry.CanvasPointToPage(
            new Point(page.X + page.Width / 2, page.Y + page.Height / 2), 300, 200, canvasWidth, canvasHeight, rotation);

        Assert.Equal(70, x, 6);
        Assert.Equal(55, y, 6);
    }

    [Fact]
    public void ExactSearchMatchesCoverOnlyTheMatchedCharacters()
    {
        PdfDocument document = PdfDocument.Open(Pdf(0, "BT /F1 12 Tf 20 100 Td (Keep ROTSECRET here) Tj ET",
            "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>"));
        PdfPageContent page = new PdfPageContentReader(document).Read(0);
        var word = page.Words.Single(w => w.Text == "ROTSECRET");

        var single = SearchService.FindExactMatchesOnPage(page, "secret");
        var phrase = SearchService.FindExactMatchesOnPage(page, "SECRET here");

        var match = Assert.Single(single);
        Assert.Equal(word.Letters[3].BoundingBox.Left, match.Left, 6);
        Assert.Equal(word.BoundingBox.Right, match.Right, 6);
        Assert.Equal(2, phrase.Count);
        Assert.Equal(match, phrase[0]);
        Assert.Equal(page.Words.Single(w => w.Text == "here").BoundingBox.Left, phrase[1].Left, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void PointsPerCanvasUnitIsTheSameOnEveryTurn(int rotation)
    {
        // A 300 by 200 point page shown at twice its size is 600 by 400 level and 400 by 600 turned.
        double canvasHeight = rotation is 90 or 270 ? 600 : 400;

        Assert.Equal(0.5, RedactionGeometry.PointsPerCanvasUnit(300, 200, canvasHeight, rotation), 9);
    }

    [Fact]
    public void CanvasRectanglesAreClampedToThePage()
    {
        var (left, bottom, right, top) = RedactionGeometry.CanvasToPage(
            new Rect(-50, -50, 1000, 1000), 300, 200, 600, 400, 0);

        Assert.Equal((0d, 0d, 300d, 200d), (left, bottom, right, top));
    }

    [Fact]
    public void OverlapRequiresSharedArea()
    {
        Assert.True(RedactionGeometry.Overlaps(new Rect(0, 0, 10, 10), new Rect(9, 9, 5, 5)));
        Assert.False(RedactionGeometry.Overlaps(new Rect(0, 0, 10, 10), new Rect(10, 0, 5, 5)));
        Assert.False(RedactionGeometry.Overlaps(Rect.Empty, new Rect(0, 0, 5, 5)));
    }

    private static bool Dark(PdfRenderedPage page, double x, double y)
    {
        int px = (int)x, py = (int)y;
        int offset = (py * page.Width + px) * 4;
        ReadOnlySpan<byte> pixels = page.Pixels.Span;
        return pixels[offset] < 60 && pixels[offset + 1] < 60 && pixels[offset + 2] < 60;
    }

    [Fact]
    public void ApplyRedactionReplacesTheFileWithTheVerifiedResult()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kp-redact-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, Pdf(0, "BT /F1 12 Tf 20 100 Td (Keep HIDEME keep) Tj ET",
            "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>"));
        try
        {
            PdfDocument before = PdfDocument.Open(File.ReadAllBytes(path));
            var letters = new PdfPageContentReader(before).Read(0).Letters;
            string text = string.Concat(letters.Select(letter => letter.Value));
            int start = text.IndexOf("HIDEME", StringComparison.Ordinal);
            var hidden = letters.Skip(start).Take(6).ToArray();
            var region = new PdfRedactionRegion(0, new PdfContentBounds(
                hidden.Min(letter => letter.BoundingBox.Left), hidden.Min(letter => letter.BoundingBox.Bottom),
                hidden.Max(letter => letter.BoundingBox.Right), hidden.Max(letter => letter.BoundingBox.Top)));

            Assert.Empty(PdfEngineIntegration.AnalyzeRedaction(before, [region]).RasterPages);
            PdfEngineIntegration.ApplyRedaction(path, [region], []);

            string after = new PdfPageContentReader(PdfDocument.Open(File.ReadAllBytes(path))).Read(0).Text;
            Assert.DoesNotContain("HIDEME", after);
            Assert.Contains("Keep", after);
            Assert.Contains("keep", after);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void InvertedPageBoxesMapToTheSameRectangle(int rotation)
    {
        bool quarter = rotation is 90 or 270;
        double canvasWidth = quarter ? 400 : 600, canvasHeight = quarter ? 600 : 400;

        Rect ordered = RedactionGeometry.PageToCanvas(50, 40, 90, 70, 300, 200, canvasWidth, canvasHeight, rotation);
        Rect inverted = RedactionGeometry.PageToCanvas(90, 70, 50, 40, 300, 200, canvasWidth, canvasHeight, rotation);

        Assert.Equal(ordered, inverted);
        Assert.True(inverted.Width > 0 && inverted.Height > 0);
    }

    private static byte[] Pdf(int rotation, string content, string resources = "")
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Rotate {rotation} {resources} /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
