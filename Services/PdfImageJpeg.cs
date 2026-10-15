using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KillerPDF.Services;

/// <summary>The JPEG encoder Reduce File Size hands the engine for resampled images (#433).</summary>
internal static class PdfImageJpeg
{
    /// <summary>Encodes tightly packed 8-bit gray or RGB samples at the given quality.</summary>
    internal static byte[] Encode(ReadOnlySpan<byte> samples, int width, int height, int components, int quality)
    {
        if (components is not (1 or 3)) throw new ArgumentOutOfRangeException(nameof(components));
        PixelFormat format = components == 1 ? PixelFormats.Gray8 : PixelFormats.Rgb24;
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, format, null,
            samples.ToArray(), width * components);
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
