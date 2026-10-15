using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #426: the scanned look applied when saving a flattened copy.
public sealed class ScanLookTests
{
    private const int Width = 120, Height = 160;

    // White page with a black block in the middle.
    private static byte[] Page()
    {
        var bgra = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                byte value = x is >= 40 and < 80 && y is >= 60 and < 100 ? (byte)0 : (byte)255;
                int i = (y * Width + x) * 4;
                bgra[i] = bgra[i + 1] = bgra[i + 2] = value;
                bgra[i + 3] = 255;
            }
        return bgra;
    }

    private static (byte B, byte G, byte R) Pixel(byte[] bgra, int x, int y)
    {
        int i = (y * Width + x) * 4;
        return (bgra[i], bgra[i + 1], bgra[i + 2]);
    }

    [Fact]
    public void NoEffects_ReturnsThePageUntouched()
    {
        byte[] page = Page();
        Assert.Same(page, ScanLook.Apply(page, Width, Height, 150, new ScanLookOptions(), 0));
    }

    [Fact]
    public void PaperTone_WarmsWhiteAndKeepsInkBlack()
    {
        byte[] toned = ScanLook.Apply(Page(), Width, Height, 150, new ScanLookOptions(PaperTone: true), 0);

        var paper = Pixel(toned, 5, 5);
        Assert.True(paper.R > paper.G && paper.G > paper.B);
        Assert.Equal(((byte)0, (byte)0, (byte)0), Pixel(toned, 60, 80));
    }

    [Fact]
    public void Tilt_KeepsThePageSizeAndMovesTheContent()
    {
        byte[] original = Page();
        byte[] tilted = ScanLook.Apply(original, Width, Height, 150, new ScanLookOptions(Tilt: true), 2);

        Assert.Equal(original.Length, tilted.Length);
        Assert.NotEqual(original, tilted);
        Assert.InRange(Math.Abs(ScanLook.TiltFor(2)), 0.35, 1.45);
    }

    [Fact]
    public void Grain_RepeatsExactlyForThePage()
    {
        var options = new ScanLookOptions(Grain: true);
        Assert.Equal(ScanLook.Apply(Page(), Width, Height, 150, options, 4),
            ScanLook.Apply(Page(), Width, Height, 150, options, 4));
        Assert.NotEqual(ScanLook.Apply(Page(), Width, Height, 150, options, 4),
            ScanLook.Apply(Page(), Width, Height, 150, options, 5));
    }

    [Fact]
    public void EdgeShadow_DarkensTheBindingEdgeMoreThanTheMiddle()
    {
        byte[] shaded = ScanLook.Apply(Page(), Width, Height, 150, new ScanLookOptions(EdgeShadow: true), 0);

        Assert.True(Pixel(shaded, 0, 30).G < 200);
        Assert.Equal(255, Pixel(shaded, 100, 30).G);
    }

    [Fact]
    public void SoftFocus_SoftensAHardEdge()
    {
        byte[] soft = ScanLook.Apply(Page(), Width, Height, 150, new ScanLookOptions(SoftFocus: true), 0);

        byte edge = Pixel(soft, 40, 80).G;
        Assert.InRange(edge, (byte)1, (byte)254);
    }
}
