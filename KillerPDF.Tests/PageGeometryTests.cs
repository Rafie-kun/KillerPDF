using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #436: page reversal, upright detection from text, and scan tilt measurement.
public sealed class PageGeometryTests
{
    // White page with rows of word-like dashes whose lines fall to the right by `degrees`.
    private static byte[] TiltedText(int width, int height, double degrees)
    {
        var gray = new byte[width * height];
        Array.Fill(gray, (byte)255);
        double tangent = Math.Tan(degrees * Math.PI / 180);
        for (int line = 60; line < height - 60; line += 28)
            for (int x = 40; x < width - 40; x++)
            {
                if (x % 70 > 55) continue;   // gaps between words
                for (int stroke = 0; stroke < 6; stroke++)
                {
                    int y = (int)Math.Round(line + stroke + x * tangent);
                    if (y >= 0 && y < height) gray[y * width + x] = 0;
                }
            }
        return gray;
    }

    private static byte[] Transpose(byte[] gray, int width, int height)
    {
        var result = new byte[gray.Length];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                result[x * height + y] = gray[y * width + x];
        return result;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(-2.5)]
    [InlineData(7)]
    public void EstimateSkewDegrees_MeasuresTheTiltOfTextLines(double degrees)
    {
        byte[] gray = TiltedText(900, 1100, degrees);

        Assert.InRange(PageGeometry.EstimateSkewDegrees(gray, 900, 1100), degrees - 0.15, degrees + 0.15);
    }

    [Fact]
    public void EstimateSkewDegrees_LeavesAPageWithoutLinesAlone()
    {
        var blank = new byte[400 * 500];
        Array.Fill(blank, (byte)255);
        Assert.Equal(0, PageGeometry.EstimateSkewDegrees(blank, 400, 500));

        var noise = new byte[400 * 500];
        var random = new Random(7);
        for (int i = 0; i < noise.Length; i++) noise[i] = random.Next(10) == 0 ? (byte)0 : (byte)255;
        Assert.Equal(0, PageGeometry.EstimateSkewDegrees(noise, 400, 500));
    }

    [Fact]
    public void LineAxisRatio_TellsAcrossFromUpAndDown()
    {
        byte[] gray = TiltedText(600, 800, 0);

        Assert.True(PageGeometry.LineAxisRatio(gray, 600, 800) > 1.25);
        Assert.True(PageGeometry.LineAxisRatio(Transpose(gray, 600, 800), 800, 600) < 0.8);
    }

    [Theory]
    [InlineData(1, 0, 0)]
    [InlineData(0, 1, 90)]
    [InlineData(-1, 0, 180)]
    [InlineData(0, -1, 270)]
    public void UprightRotation_FollowsTheWayTextRuns(double dx, double dy, int rotation)
    {
        var baselines = Enumerable.Repeat((dx * 40, dy * 40, 5), 10);

        Assert.Equal(rotation, PageGeometry.UprightRotation(baselines));
    }

    [Fact]
    public void UprightRotation_NeedsAClearMajority()
    {
        var mixed = Enumerable.Repeat((40.0, 0.0, 5), 5).Concat(Enumerable.Repeat((0.0, 40.0, 5), 5));
        Assert.Null(PageGeometry.UprightRotation(mixed));
        Assert.Null(PageGeometry.UprightRotation([(40.0, 0.0, 3)]));
    }

    [Theory]
    [InlineData(0, 90, 90)]
    [InlineData(270, 0, 90)]
    [InlineData(90, 0, 270)]
    [InlineData(180, 180, 0)]
    public void TurnBetween_CountsClockwise(int current, int target, int turn)
        => Assert.Equal(turn, PageGeometry.TurnBetween(current, target));

    [Fact]
    public void ClassifyTone_KeepsTheKindOfImage()
    {
        static byte[] Fill(Func<int, (byte B, byte G, byte R)> pixel)
        {
            var bgra = new byte[100 * 100 * 4];
            for (int i = 0; i < 100 * 100; i++)
            {
                var (b, g, r) = pixel(i);
                bgra[i * 4] = b; bgra[i * 4 + 1] = g; bgra[i * 4 + 2] = r; bgra[i * 4 + 3] = 255;
            }
            return bgra;
        }

        Assert.Equal(ScanTone.BlackAndWhite, PageGeometry.ClassifyTone(Fill(i => i % 7 == 0 ? ((byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255))));
        Assert.Equal(ScanTone.Grayscale, PageGeometry.ClassifyTone(Fill(i => ((byte)(i % 256), (byte)(i % 256), (byte)(i % 256)))));
        Assert.Equal(ScanTone.Color, PageGeometry.ClassifyTone(Fill(i => i % 3 == 0 ? ((byte)200, (byte)40, (byte)40) : ((byte)255, (byte)255, (byte)255))));
    }

    [Fact]
    public void PageOrderAfterReverse_TradesOnlyTheChosenPages()
    {
        Assert.Equal([3, 2, 1, 0], PdfEngineIntegration.PageOrderAfterReverse(4, [0, 1, 2, 3]));
        Assert.Equal([0, 3, 2, 1, 4], PdfEngineIntegration.PageOrderAfterReverse(5, [1, 2, 3]));
        Assert.Equal([3, 1, 2, 0], PdfEngineIntegration.PageOrderAfterReverse(4, [3, 0]));
    }

    [Fact]
    public void ReorderPages_ReversesTheDocumentAndItsRotations()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-reverse-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .AddBlankPage(100, 500).AddBlankPage(200, 500).AddBlankPage(300, 500).Build());
            IReadOnlyList<int> order = PdfEngineIntegration.PageOrderAfterReverse(3, [0, 1, 2]);

            PdfEngineIntegration.ReorderPages(path, order);
            var rotations = new Dictionary<int, int> { [0] = 0, [1] = 90, [2] = 180 };
            PdfEngineIntegration.RemapRotationsAfterReorder(rotations, order);

            var pages = PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(path)));
            Assert.Equal([300.0, 200.0, 100.0], pages.Select(page => page.Width));
            Assert.Equal([180, 90, 0], Enumerable.Range(0, 3).Select(index => rotations[index]));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ReorderPages_RejectsAnOrderThatLosesAPage()
    {
        var rotations = new Dictionary<int, int> { [0] = 0, [1] = 0, [2] = 0 };
        Assert.Throws<ArgumentException>(() => PdfEngineIntegration.RemapRotationsAfterReorder(rotations, [0, 0, 1]));
    }
}
