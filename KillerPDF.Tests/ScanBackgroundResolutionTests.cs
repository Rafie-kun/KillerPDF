using System;
using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// Words OCR is unsure of stay in the background image, so a background smaller than the scan blurs
// them, and one larger than the scan only makes the file bigger.
public class ScanBackgroundResolutionTests
{
    [Fact]
    public void ALetterPageAt300DpiRendersPixelForPixel() =>
        Assert.Equal((2550, 3300), ScanPixels.RenderSize((2550, 3300), 2600, 3600));

    [Fact]
    public void AHugeScanComesDownToTheLimit() =>
        Assert.Equal((2782, 3600), ScanPixels.RenderSize((5100, 6600), 2600, 3600));

    [Fact]
    public void ASmallScanStillGoesUpForOcr() =>
        Assert.Null(ScanPixels.RenderSize((958, 400), 2600, 3600));

    [Fact]
    public void APageWithNoScanSizeRendersAsBefore() =>
        Assert.Null(ScanPixels.RenderSize(null, 2600, 3600));

    [Fact]
    public void ABackgroundRenderedPastTheScanGoesBackToTheScansSize() =>
        Assert.Equal((958, 400), ScanPixels.StoredSize(2600, 1085, (958, 400)));

    [Fact]
    public void ABackgroundAtTheScansSizeStaysThere()
    {
        Assert.Equal((2550, 3300), ScanPixels.StoredSize(2550, 3300, (2550, 3300)));
        Assert.Equal((2782, 3600), ScanPixels.StoredSize(2782, 3600, (5100, 6600)));
        Assert.Equal((2600, 2000), ScanPixels.StoredSize(2600, 2000, null));
    }

    [Fact]
    public void ShrinkingAveragesThePixelsUnderEachNewOne()
    {
        byte[] bgra = [0, 0, 0, 255, 100, 200, 40, 255];
        Assert.Equal(new byte[] { 50, 100, 20, 255 }, ScanPixels.Shrink(bgra, 2, 1, 1, 1));
    }

    [Fact]
    public void TheHalfSizeBackgroundIsGone()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Features", "Ocr", "OcrController.cs"));
        Assert.DoesNotContain("Halve", source, StringComparison.Ordinal);
        Assert.Contains("ScanPixels.StoredSize", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        string? folder = AppContext.BaseDirectory;
        while (folder is not null && !File.Exists(Path.Combine(folder, "KillerPDF.csproj"))) folder = Path.GetDirectoryName(folder);
        return folder ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }
}
