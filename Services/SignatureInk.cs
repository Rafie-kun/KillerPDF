using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerPDF.Services
{
    /// <summary>
    /// Turns a scanned or photographed signature into ink on a transparent page. Sensitivity
    /// decides how light a pixel can be and still count as ink: lower values keep only the darkest
    /// strokes and read lighter, higher values keep fainter strokes and deepen the ink.
    /// </summary>
    internal static class SignatureInk
    {
        internal const double MinSensitivity = 0;
        internal const double MaxSensitivity = 100;
        internal const double DefaultSensitivity = 50;

        // Width of the luminance ramp between full ink and paper, so stroke edges stay smooth.
        private const double Softness = 0.2;

        /// <summary>Applies the sensitivity to BGRA32 pixels (straight alpha) and returns new pixels.
        /// Paper becomes transparent, ink keeps its hue, and pixels already transparent stay so.</summary>
        internal static byte[] Apply(byte[] bgra, double sensitivity)
        {
            double s = Math.Clamp(sensitivity, MinSensitivity, MaxSensitivity) / MaxSensitivity;
            double cutoff = 0.45 + 0.5 * s;     // lightest luminance that still reads as ink
            double blackPoint = 0.4 * s;        // deepens the ink as sensitivity rises

            var result = new byte[bgra.Length];
            for (int i = 0; i + 3 < bgra.Length; i += 4)
            {
                double b = bgra[i] / 255.0, g = bgra[i + 1] / 255.0, r = bgra[i + 2] / 255.0;
                double luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                double ink = Math.Clamp((cutoff - luminance) / Softness, 0, 1);

                result[i]     = Deepen(b, blackPoint);
                result[i + 1] = Deepen(g, blackPoint);
                result[i + 2] = Deepen(r, blackPoint);
                result[i + 3] = (byte)Math.Round(bgra[i + 3] * ink);
            }
            return result;
        }

        private static byte Deepen(double channel, double blackPoint) =>
            (byte)Math.Round(Math.Clamp((channel - blackPoint) / (1 - blackPoint), 0, 1) * 255);

        /// <summary>Reads any bitmap as BGRA32 pixels with straight alpha.</summary>
        internal static byte[] ReadPixels(BitmapSource source, out int width, out int height)
        {
            BitmapSource bgra = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            width = bgra.PixelWidth;
            height = bgra.PixelHeight;
            var pixels = new byte[width * height * 4];
            bgra.CopyPixels(pixels, width * 4, 0);
            return pixels;
        }

        /// <summary>Builds a frozen BGRA32 bitmap from pixels returned by <see cref="Apply"/>.</summary>
        internal static BitmapSource ToBitmap(byte[] bgra, int width, int height)
        {
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        /// <summary>Scales a large photo down for the live preview so the slider stays responsive.</summary>
        internal static BitmapSource PreviewSource(BitmapSource source, int maxEdge)
        {
            int edge = Math.Max(source.PixelWidth, source.PixelHeight);
            if (edge <= maxEdge) return source;
            double scale = (double)maxEdge / edge;
            var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            scaled.Freeze();
            return scaled;
        }

        /// <summary>Encodes a bitmap as PNG, which keeps the transparent background.</summary>
        internal static byte[] EncodePng(BitmapSource bitmap)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
    }
}
