using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerPDF.Services;

internal sealed record ColorCorrectionSettings(
    double Exposure = 0, double Saturation = 1, double Warmth = 0, double Tint = 0)
{
    public bool IsIdentity => Exposure == 0 && Saturation == 1 && Warmth == 0 && Tint == 0;
}

internal static class ColorCorrection
{
    private static readonly double[] Linear = Enumerable.Range(0, 256).Select(i =>
    {
        double value = i / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }).ToArray();

    private static readonly byte[] Encoded = Enumerable.Range(0, 65536).Select(i =>
    {
        double value = i / 65535.0;
        return (byte)Math.Round(255 * (value <= 0.0031308
            ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055));
    }).ToArray();

    public static BitmapSource Apply(BitmapSource source, ColorCorrectionSettings settings)
    {
        if (settings.IsIdentity) return source;
        if (!double.IsFinite(settings.Exposure) || !double.IsFinite(settings.Saturation)
            || !double.IsFinite(settings.Warmth) || !double.IsFinite(settings.Tint))
            throw new ArgumentOutOfRangeException(nameof(settings));
        double exposure = Math.Pow(2, Math.Clamp(settings.Exposure, -3, 3));
        double saturation = Math.Clamp(settings.Saturation, 0, 2);
        double warmth = Math.Clamp(settings.Warmth, -100, 100) / 100;
        double tint = Math.Clamp(settings.Tint, -100, 100) / 100;
        // Relative white balance gains in linear sRGB. Zero leaves every channel neutral.
        double redGain = exposure * Math.Pow(2, warmth * 0.5 + tint * 0.25);
        double greenGain = exposure * Math.Pow(2, -tint * 0.5);
        double blueGain = exposure * Math.Pow(2, -warmth * 0.5 + tint * 0.25);
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        static byte Encode(double value) => Encoded[(int)Math.Round(Math.Clamp(value, 0, 1) * 65535)];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            double r = Linear[pixels[i + 2]] * redGain;
            double g = Linear[pixels[i + 1]] * greenGain;
            double b = Linear[pixels[i]] * blueGain;
            double luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            pixels[i + 2] = Encode(luminance + (r - luminance) * saturation);
            pixels[i + 1] = Encode(luminance + (g - luminance) * saturation);
            pixels[i] = Encode(luminance + (b - luminance) * saturation);
        }
        var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight,
            converted.DpiX, converted.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
