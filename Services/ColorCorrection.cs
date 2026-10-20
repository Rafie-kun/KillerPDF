using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerPDF.Services;

internal sealed record ColorCorrectionSettings(
    double Exposure = 0, double Saturation = 1, double Warmth = 0, double Tint = 0)
{
    public bool AutoExposure { get; init; }
    public bool AutoWhiteBalance { get; init; }
    public bool IsIdentity => !AutoExposure && !AutoWhiteBalance
        && Exposure == 0 && Saturation == 1 && Warmth == 0 && Tint == 0;
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
        double exposure = settings.AutoExposure ? 1 : Math.Pow(2, Math.Clamp(settings.Exposure, -3, 3));
        double saturation = Math.Clamp(settings.Saturation, 0, 2);
        double warmth = settings.AutoWhiteBalance ? 0 : Math.Clamp(settings.Warmth, -100, 100) / 100;
        double tint = settings.AutoWhiteBalance ? 0 : Math.Clamp(settings.Tint, -100, 100) / 100;
        // Relative white balance gains in linear sRGB. Zero leaves every channel neutral.
        double redGain = exposure * Math.Pow(2, warmth * 0.5 + tint * 0.25);
        double greenGain = exposure * Math.Pow(2, -tint * 0.5);
        double blueGain = exposure * Math.Pow(2, -warmth * 0.5 + tint * 0.25);
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = checked(converted.PixelWidth * 4);
        var pixels = new byte[checked(stride * converted.PixelHeight)];
        converted.CopyPixels(pixels, stride, 0);
        if (settings.AutoWhiteBalance || settings.AutoExposure)
        {
            var (r, g, b) = AutomaticGains(pixels, settings.AutoWhiteBalance, settings.AutoExposure);
            redGain *= r;
            greenGain *= g;
            blueGain *= b;
        }
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

    private static (double R, double G, double B) AutomaticGains(byte[] pixels, bool whiteBalance, bool exposure)
    {
        double rGain = 1, gGain = 1, bGain = 1;
        int step = Math.Max(1, pixels.Length / 4 / 262144) * 4;
        if (whiteBalance)
        {
            double r = 0, g = 0, b = 0;
            int count = 0;
            for (int i = 0; i < pixels.Length; i += step)
            {
                if (pixels[i + 3] < 128) continue;
                double pr = Linear[pixels[i + 2]], pg = Linear[pixels[i + 1]], pb = Linear[pixels[i]];
                if (Math.Max(pr, Math.Max(pg, pb)) >= 0.98 || Math.Min(pr, Math.Min(pg, pb)) <= 0.005) continue;
                r += pr; g += pg; b += pb; count++;
            }
            if (count > 0)
            {
                double neutral = (r + g + b) / 3;
                rGain = Math.Clamp(neutral / r, 0.5, 2);
                gGain = Math.Clamp(neutral / g, 0.5, 2);
                bGain = Math.Clamp(neutral / b, 0.5, 2);
            }
        }
        if (exposure)
        {
            var histogram = new int[4096];
            int count = 0;
            for (int i = 0; i < pixels.Length; i += step)
            {
                if (pixels[i + 3] < 128) continue;
                double value = 0.2126 * Linear[pixels[i + 2]] * rGain
                    + 0.7152 * Linear[pixels[i + 1]] * gGain + 0.0722 * Linear[pixels[i]] * bGain;
                histogram[(int)Math.Round(Math.Clamp(value, 0, 1) * 4095)]++;
                count++;
            }
            int cumulative = 0;
            for (int i = 0; count > 0 && i < histogram.Length; i++)
            {
                cumulative += histogram[i];
                if (cumulative < Math.Ceiling(count * 0.95)) continue;
                double highlight = i / 4095.0;
                // Protect blank paper and avoid amplifying an entirely black page.
                double gain = highlight <= 0.001 || highlight >= 0.98
                    ? 1 : Math.Clamp(0.8 / highlight, 0.125, 8);
                rGain *= gain; gGain *= gain; bGain *= gain;
                break;
            }
        }
        return (rGain, gGain, bGain);
    }
}
