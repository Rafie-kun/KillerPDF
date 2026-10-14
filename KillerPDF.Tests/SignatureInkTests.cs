using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class SignatureInkTests
{
    private static byte[] Pixel(byte gray, byte alpha = 255) => [gray, gray, gray, alpha];

    [Fact]
    public void Apply_DropsPaperAndKeepsInk()
    {
        byte[] paper = SignatureInk.Apply(Pixel(245), SignatureInk.DefaultSensitivity);
        byte[] ink = SignatureInk.Apply(Pixel(20), SignatureInk.DefaultSensitivity);

        Assert.Equal(0, paper[3]);
        Assert.Equal(255, ink[3]);
        Assert.True(ink[0] <= 20);
    }

    [Fact]
    public void Apply_HigherSensitivityKeepsFainterStrokesAndDarkensThem()
    {
        byte[] faint = Pixel(150);
        byte[] lighter = SignatureInk.Apply(faint, SignatureInk.MinSensitivity);
        byte[] normal = SignatureInk.Apply(faint, SignatureInk.DefaultSensitivity);
        byte[] darker = SignatureInk.Apply(faint, SignatureInk.MaxSensitivity);

        Assert.Equal(0, lighter[3]);
        Assert.True(normal[3] > lighter[3]);
        Assert.Equal(255, darker[3]);
        Assert.True(darker[0] < normal[0]);
        Assert.True(normal[0] < faint[0]);
    }

    [Fact]
    public void Apply_KeepsTransparentPixelsTransparentAndInkHue()
    {
        byte[] transparent = SignatureInk.Apply(Pixel(0, 0), SignatureInk.MaxSensitivity);
        byte[] blueInk = SignatureInk.Apply([160, 40, 20, 255], SignatureInk.DefaultSensitivity);

        Assert.Equal(0, transparent[3]);
        Assert.Equal(255, blueInk[3]);
        Assert.True(blueInk[0] > blueInk[2]);
    }

    [Fact]
    public void Apply_ClampsOutOfRangeSensitivity()
    {
        byte[] faint = Pixel(150);
        Assert.Equal(SignatureInk.Apply(faint, SignatureInk.MinSensitivity), SignatureInk.Apply(faint, -40));
        Assert.Equal(SignatureInk.Apply(faint, SignatureInk.MaxSensitivity), SignatureInk.Apply(faint, 400));
    }

    [Fact]
    public void Bitmaps_RoundTripThroughPixelsAndPng()
    {
        byte[] pixels = [.. Pixel(20), .. Pixel(245)];
        byte[] inked = SignatureInk.Apply(pixels, SignatureInk.DefaultSensitivity);
        var bitmap = SignatureInk.ToBitmap(inked, 2, 1);

        byte[] read = SignatureInk.ReadPixels(bitmap, out int width, out int height);
        byte[] png = SignatureInk.EncodePng(bitmap);

        Assert.Equal((2, 1), (width, height));
        Assert.Equal(inked, read);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4e, 0x47 }, png[..4]);
    }
}
