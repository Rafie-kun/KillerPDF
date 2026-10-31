using System;

namespace KillerPDF.Services
{
    // Convert Scan to Text keeps the page as an image behind the words it replaces, and everything
    // it did not replace is read from that image, so its size decides how sharp those words stay.
    internal static class ScanPixels
    {
        /// <summary>The size to render a scanned page at, or null for the default OCR size. A scan
        /// inside the range renders pixel for pixel, a larger one comes down to the limit, and a
        /// smaller one still goes up for OCR, which reads it better that way.</summary>
        internal static (int Width, int Height)? RenderSize((int Width, int Height)? scan, int ocrSize, int limit)
        {
            if (scan is not { } size) return null;
            int edge = Math.Max(size.Width, size.Height);
            if (edge < ocrSize) return null;
            if (edge <= limit) return size;
            double scale = limit / (double)edge;
            return (Math.Max(1, (int)Math.Round(size.Width * scale)), Math.Max(1, (int)Math.Round(size.Height * scale)));
        }

        /// <summary>The size to store a page's background at: the scan's own when the render went
        /// past it, since upscaled pixels hold nothing new, otherwise the render's.</summary>
        internal static (int Width, int Height) StoredSize(int width, int height, (int Width, int Height)? scan) =>
            scan is { } size && Math.Max(size.Width, size.Height) < Math.Max(width, height) ? size : (width, height);

        /// <summary>Scales BGRA pixels down by averaging the source pixels under each new one.</summary>
        internal static byte[] Shrink(byte[] bgra, int width, int height, int newWidth, int newHeight)
        {
            byte[] result = new byte[newWidth * newHeight * 4];
            for (int y = 0; y < newHeight; y++)
            {
                int top = y * height / newHeight, bottom = Math.Max(top + 1, (y + 1) * height / newHeight);
                for (int x = 0; x < newWidth; x++)
                {
                    int left = x * width / newWidth, right = Math.Max(left + 1, (x + 1) * width / newWidth);
                    int blue = 0, green = 0, red = 0, count = (bottom - top) * (right - left);
                    for (int row = top; row < bottom; row++)
                        for (int column = left; column < right; column++)
                        {
                            int p = (row * width + column) * 4;
                            blue += bgra[p]; green += bgra[p + 1]; red += bgra[p + 2];
                        }
                    int o = (y * newWidth + x) * 4;
                    result[o] = (byte)((blue + count / 2) / count);
                    result[o + 1] = (byte)((green + count / 2) / count);
                    result[o + 2] = (byte)((red + count / 2) / count);
                    result[o + 3] = 255;
                }
            }
            return result;
        }
    }
}
