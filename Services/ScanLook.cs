namespace KillerPDF.Services
{
    /// <summary>
    /// Effects that make a flattened page look like a paper copy run through a scanner (#426):
    /// a slight tilt, warm paper, sensor grain, soft focus and the shadow a page edge casts on
    /// the glass. Grayscale and JPEG artifacts come from the flatten options themselves.
    /// </summary>
    internal sealed record ScanLookOptions(
        bool Tilt = false,
        bool PaperTone = false,
        bool Grain = false,
        bool SoftFocus = false,
        bool EdgeShadow = false)
    {
        internal bool Any => Tilt || PaperTone || Grain || SoftFocus || EdgeShadow;
    }

    /// <summary>Applies <see cref="ScanLookOptions"/> to rendered BGRA32 pages. Pure and
    /// deterministic per page, so a page looks the same each time it is flattened.</summary>
    internal static class ScanLook
    {
        // Warm, slightly yellowed office paper.
        private const byte PaperBlue = 222, PaperGreen = 240, PaperRed = 248;

        /// <summary>The tilt, in degrees, a page gets: under a degree and a half either way, and
        /// never so small that it reads as straight.</summary>
        internal static double TiltFor(int pageIndex)
        {
            var random = new Random(unchecked(pageIndex * 7919 + 17));
            double magnitude = 0.35 + random.NextDouble() * 1.1;
            return random.Next(2) == 0 ? -magnitude : magnitude;
        }

        /// <summary>Returns the page with the chosen effects, the same size as the input.
        /// <paramref name="dpi"/> scales blur and grain so they look alike at any resolution.</summary>
        internal static byte[] Apply(byte[] bgra, int width, int height, double dpi, ScanLookOptions options, int pageIndex)
        {
            ArgumentNullException.ThrowIfNull(bgra);
            ArgumentNullException.ThrowIfNull(options);
            if (bgra.Length != width * height * 4) throw new ArgumentException("The BGRA image length or dimensions are invalid.", nameof(bgra));
            if (!options.Any) return bgra;
            byte[] page = (byte[])bgra.Clone();
            double scale = Math.Max(0.25, dpi / 150.0);

            if (options.PaperTone) TonePaper(page);
            if (options.SoftFocus) BoxBlur(page, width, height, Math.Max(1, (int)Math.Round(scale)));
            if (options.Tilt)
                page = Rotate(page, width, height, TiltFor(pageIndex),
                    options.PaperTone ? (PaperBlue, PaperGreen, PaperRed) : ((byte)255, (byte)255, (byte)255));
            if (options.EdgeShadow) ShadeEdge(page, width, height);
            if (options.Grain) AddGrain(page, pageIndex, scale);
            return page;
        }

        // Multiplies the page by the paper color, so white becomes paper and ink stays ink.
        private static void TonePaper(byte[] page)
        {
            for (int i = 0; i < page.Length; i += 4)
            {
                page[i] = (byte)(page[i] * PaperBlue / 255);
                page[i + 1] = (byte)(page[i + 1] * PaperGreen / 255);
                page[i + 2] = (byte)(page[i + 2] * PaperRed / 255);
            }
        }

        // One horizontal and one vertical box pass: a light, even defocus.
        private static void BoxBlur(byte[] page, int width, int height, int radius)
        {
            var line = new byte[Math.Max(width, height) * 4];
            for (int y = 0; y < height; y++)
                BlurRun(page, y * width * 4, 4, width, radius, line);
            for (int x = 0; x < width; x++)
                BlurRun(page, x * 4, width * 4, height, radius, line);
        }

        private static void BlurRun(byte[] page, int start, int step, int count, int radius, byte[] line)
        {
            for (int i = 0; i < count; i++)
                Buffer.BlockCopy(page, start + i * step, line, i * 4, 4);
            for (int channel = 0; channel < 3; channel++)
            {
                int sum = 0, span = 0;
                for (int i = -radius; i <= radius; i++)
                    if (i >= 0 && i < count) { sum += line[i * 4 + channel]; span++; }
                for (int i = 0; i < count; i++)
                {
                    page[start + i * step + channel] = (byte)(sum / span);
                    int leaving = i - radius, entering = i + radius + 1;
                    if (leaving >= 0) { sum -= line[leaving * 4 + channel]; span--; }
                    if (entering < count) { sum += line[entering * 4 + channel]; span++; }
                }
            }
        }

        // Turns the page about its center by bilinear sampling, filling the uncovered corners
        // with the paper color.
        private static byte[] Rotate(byte[] page, int width, int height, double degrees, (byte B, byte G, byte R) fill)
        {
            var result = new byte[page.Length];
            double radians = degrees * Math.PI / 180;
            double cos = Math.Cos(radians), sin = Math.Sin(radians);
            double cx = (width - 1) / 2.0, cy = (height - 1) / 2.0;
            for (int y = 0; y < height; y++)
            {
                double dy = y - cy;
                for (int x = 0; x < width; x++)
                {
                    double dx = x - cx;
                    double sx = cx + cos * dx + sin * dy;
                    double sy = cy - sin * dx + cos * dy;
                    int target = (y * width + x) * 4;
                    int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                    if (x0 < 0 || y0 < 0 || x0 >= width - 1 || y0 >= height - 1)
                    {
                        result[target] = fill.B;
                        result[target + 1] = fill.G;
                        result[target + 2] = fill.R;
                        result[target + 3] = 255;
                        continue;
                    }
                    double fx = sx - x0, fy = sy - y0;
                    int a = (y0 * width + x0) * 4, b = a + 4, c = a + width * 4, d = c + 4;
                    for (int channel = 0; channel < 4; channel++)
                    {
                        double top = page[a + channel] + (page[b + channel] - page[a + channel]) * fx;
                        double bottom = page[c + channel] + (page[d + channel] - page[c + channel]) * fx;
                        result[target + channel] = (byte)Math.Round(top + (bottom - top) * fy);
                    }
                }
            }
            return result;
        }

        // The gray falloff a lifted page edge leaves along the binding side, plus a faint
        // darkening toward every edge where the scanner lid lets light in.
        private static void ShadeEdge(byte[] page, int width, int height)
        {
            double band = Math.Max(4, width * 0.045);
            double rim = Math.Max(4, Math.Min(width, height) * 0.03);
            for (int y = 0; y < height; y++)
            {
                double fromTop = Math.Min(y, height - 1 - y);
                for (int x = 0; x < width; x++)
                {
                    double spine = x < band ? Math.Pow(1 - x / band, 2) * 0.38 : 0;
                    double nearest = Math.Min(fromTop, Math.Min(x, width - 1 - x));
                    double edge = nearest < rim ? (1 - nearest / rim) * 0.1 : 0;
                    double keep = 1 - Math.Min(0.6, spine + edge);
                    if (keep >= 1) continue;
                    int i = (y * width + x) * 4;
                    page[i] = (byte)(page[i] * keep);
                    page[i + 1] = (byte)(page[i + 1] * keep);
                    page[i + 2] = (byte)(page[i + 2] * keep);
                }
            }
        }

        // Even, colorless sensor noise, seeded by the page so it repeats exactly.
        private static void AddGrain(byte[] page, int pageIndex, double scale)
        {
            var random = new Random(unchecked(pageIndex * 104729 + 3));
            int strength = (int)Math.Round(9 / Math.Sqrt(scale)) + 3;
            for (int i = 0; i < page.Length; i += 4)
            {
                int noise = random.Next(-strength, strength + 1);
                page[i] = (byte)Math.Clamp(page[i] + noise, 0, 255);
                page[i + 1] = (byte)Math.Clamp(page[i + 1] + noise, 0, 255);
                page[i + 2] = (byte)Math.Clamp(page[i + 2] + noise, 0, 255);
            }
        }
    }
}
