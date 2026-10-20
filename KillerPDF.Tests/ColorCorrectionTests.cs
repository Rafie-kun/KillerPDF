using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public class ColorCorrectionTests
{
    private static BitmapSource Pixel(byte r, byte g, byte b, byte a = 255)
        => BitmapSource.Create(1, 1, 144, 144, PixelFormats.Bgra32, null, new byte[] { b, g, r, a }, 4);

    private static byte[] Bytes(BitmapSource bitmap)
    {
        var pixels = new byte[4];
        bitmap.CopyPixels(pixels, 4, 0);
        return pixels;
    }

    [Fact]
    public void NeutralReturnsOriginalWithoutChangingPixels()
    {
        var source = Pixel(120, 80, 40, 91);
        Assert.Same(source, ColorCorrection.Apply(source, new()));
    }

    [Theory]
    [InlineData(1, 176)]
    [InlineData(-1, 92)]
    public void ExposureStopsScaleLinearLight(double stops, int expected)
    {
        var result = ColorCorrection.Apply(Pixel(128, 128, 128, 93), new(Exposure: stops));
        Assert.Equal(new byte[] { (byte)expected, (byte)expected, (byte)expected, 93 }, Bytes(result));
        Assert.Equal(144, result.DpiX);
        Assert.Equal(1, result.PixelWidth);
    }

    [Fact]
    public void ZeroSaturationProducesLuminanceGray()
        => Assert.Equal(new byte[] { 127, 127, 127, 255 },
            Bytes(ColorCorrection.Apply(Pixel(255, 0, 0), new(Saturation: 0))));

    [Fact]
    public void IncreasedSaturationMovesChannelsAwayFromGray()
    {
        byte[] result = Bytes(ColorCorrection.Apply(Pixel(160, 100, 80), new(Saturation: 2)));
        Assert.True(result[2] > 160 && result[0] < 80);
    }

    [Theory]
    [InlineData(100, 0, true)]
    [InlineData(-100, 0, false)]
    public void WarmthMovesNeutralTowardRedOrBlue(double warmth, double tint, bool red)
    {
        byte[] result = Bytes(ColorCorrection.Apply(Pixel(128, 128, 128), new(Warmth: warmth, Tint: tint)));
        Assert.True(red ? result[2] > result[0] : result[2] < result[0]);
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(-100, false)]
    public void TintMovesNeutralTowardMagentaOrGreen(double tint, bool magenta)
    {
        byte[] result = Bytes(ColorCorrection.Apply(Pixel(128, 128, 128), new(Tint: tint)));
        Assert.True(magenta ? result[2] > result[1] : result[2] < result[1]);
    }

    [Fact]
    public void StrongAdjustmentsClampAndPreserveAlpha()
    {
        byte[] result = Bytes(ColorCorrection.Apply(Pixel(255, 255, 255, 71), new(Exposure: 3)));
        Assert.Equal(new byte[] { 255, 255, 255, 71 }, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void SaturationKeepsNeutralGray(double saturation)
        => Assert.Equal(new byte[] { 128, 128, 128, 255 },
            Bytes(ColorCorrection.Apply(Pixel(128, 128, 128), new(Saturation: saturation))));

    [Fact]
    public void NonfiniteAdjustmentIsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(() =>
            ColorCorrection.Apply(Pixel(0, 0, 0), new(Exposure: double.NaN)));
}
