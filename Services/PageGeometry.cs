namespace KillerPDF.Services;

/// <summary>What a scanned page holds: color, shades of gray, or only black and white.</summary>
internal enum ScanTone { Color, Grayscale, BlackAndWhite }

/// <summary>
/// Page layout measurements behind Fix Page Orientation and Straighten Scanned Pages (#436):
/// which way a page's text runs and how far a scan is tilted. Pure functions over grayscale
/// pixels and text baselines, so they work without a window or a renderer.
/// </summary>
internal static class PageGeometry
{
    private const byte InkThreshold = 160;

    /// <summary>
    /// Grayscale copy of a BGRA32 image, shrunk so its longer side is at most
    /// <paramref name="maxSide"/>. Each output pixel keeps the darkest sample it covers, so thin
    /// strokes survive the reduction.
    /// </summary>
    internal static byte[] DownscaleGray(ReadOnlySpan<byte> bgra, int width, int height, int maxSide,
        out int grayWidth, out int grayHeight)
    {
        if (width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4)
            throw new ArgumentException("The BGRA image length or dimensions are invalid.", nameof(bgra));
        double factor = Math.Min(1.0, maxSide / (double)Math.Max(width, height));
        grayWidth = Math.Max(1, (int)Math.Round(width * factor));
        grayHeight = Math.Max(1, (int)Math.Round(height * factor));
        var gray = new byte[grayWidth * grayHeight];
        Array.Fill(gray, byte.MaxValue);
        for (int y = 0; y < height; y++)
        {
            int gy = Math.Min(grayHeight - 1, (int)(y * factor));
            for (int x = 0; x < width; x++)
            {
                int offset = (y * width + x) * 4;
                byte luma = (byte)((29 * bgra[offset] + 150 * bgra[offset + 1] + 77 * bgra[offset + 2] + 128) >> 8);
                int index = gy * grayWidth + Math.Min(grayWidth - 1, (int)(x * factor));
                if (luma < gray[index]) gray[index] = luma;
            }
        }
        return gray;
    }

    /// <summary>
    /// How far the text lines of a scan are tilted, in degrees. Positive means the lines fall to
    /// the right (the content is turned clockwise), so rotating by the negative straightens it.
    /// Returns 0 when the page has no clear lines of text, such as a photo or a blank page.
    /// </summary>
    internal static double EstimateSkewDegrees(ReadOnlySpan<byte> gray, int width, int height,
        double maxDegrees = 10)
    {
        if (width <= 0 || height <= 0 || gray.Length < width * height) return 0;
        var ink = InkPoints(gray, width, height, 250_000);
        if (ink.Count < 64) return 0;

        var coarse = new List<double>();
        double best = 0, bestScore = double.NegativeInfinity;
        for (double angle = -maxDegrees; angle <= maxDegrees + 1e-9; angle += 0.5)
        {
            double score = ProfileScore(ink, width, height, angle);
            coarse.Add(score);
            if (score > bestScore) { bestScore = score; best = angle; }
        }

        // Lines of text give one angle a far sharper profile than the rest; anything flatter is
        // not text, and turning it would only damage the page.
        coarse.Sort();
        double median = coarse[coarse.Count / 2];
        if (median <= 0 || bestScore < median * 1.15) return 0;

        double fine = best;
        for (double angle = best - 0.5; angle <= best + 0.5 + 1e-9; angle += 0.05)
        {
            double score = ProfileScore(ink, width, height, angle);
            if (score > bestScore) { bestScore = score; fine = angle; }
        }
        return Math.Round(fine, 2);
    }

    /// <summary>
    /// How strongly the ink lines up in rows compared with columns. Above 1, the text lines run
    /// across the image; below 1, they run up and down it, so the page is turned a quarter.
    /// </summary>
    internal static double LineAxisRatio(ReadOnlySpan<byte> gray, int width, int height)
    {
        if (width <= 0 || height <= 0 || gray.Length < width * height) return 1;
        var rows = new double[height];
        var columns = new double[width];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (gray[y * width + x] < InkThreshold) { rows[y]++; columns[x]++; }
        double rowScore = Contrast(rows), columnScore = Contrast(columns);
        if (rowScore <= 0 && columnScore <= 0) return 1;
        if (columnScore <= 0) return double.PositiveInfinity;
        return rowScore / columnScore;
    }

    /// <summary>
    /// The page rotation (0, 90, 180 or 270, clockwise like the PDF /Rotate entry) that shows
    /// text upright, from text baselines in page space with y pointing up. Each baseline counts
    /// by its weight, its letter count. Returns null without a clear majority.
    /// </summary>
    internal static int? UprightRotation(IEnumerable<(double Dx, double Dy, int Weight)> baselines,
        int minimumWeight = 20, double majority = 0.7)
    {
        var votes = new double[4];
        double total = 0;
        foreach (var (dx, dy, weight) in baselines)
        {
            if (weight <= 0 || (dx == 0 && dy == 0)) continue;
            double degrees = Math.Atan2(dy, dx) * 180 / Math.PI;
            double snapped = Math.Round(degrees / 90) * 90;
            if (Math.Abs(degrees - snapped) > 10) continue;   // slanted text votes for nothing
            votes[(((int)snapped % 360) + 360) % 360 / 90] += weight;
            total += weight;
        }
        if (total < minimumWeight) return null;
        int winner = Array.IndexOf(votes, votes.Max());
        return votes[winner] >= total * majority ? winner * 90 : null;
    }

    /// <summary>
    /// Whether a rendered scan is color, grayscale, or black and white, so a redrawn page keeps
    /// the kind of image it had. Edge pixels softened by rendering do not count as gray.
    /// </summary>
    internal static ScanTone ClassifyTone(ReadOnlySpan<byte> bgra)
    {
        int pixels = bgra.Length / 4;
        if (pixels == 0) return ScanTone.Color;
        int stride = Math.Max(1, pixels / 200_000);
        int sampled = 0, colored = 0, midtone = 0;
        for (int pixel = 0; pixel < pixels; pixel += stride)
        {
            int offset = pixel * 4;
            int blue = bgra[offset], green = bgra[offset + 1], red = bgra[offset + 2];
            sampled++;
            int spread = Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue));
            if (spread > 24) colored++;
            else if (green > 64 && green < 192) midtone++;
        }
        if (colored > sampled * 0.01) return ScanTone.Color;
        return midtone <= sampled * 0.03 ? ScanTone.BlackAndWhite : ScanTone.Grayscale;
    }

    /// <summary>The clockwise quarter turns, as degrees, that take a page from its current
    /// rotation to the target one.</summary>
    internal static int TurnBetween(int current, int target) => (((target - current) % 360) + 360) % 360;

    private static List<(int X, int Y)> InkPoints(ReadOnlySpan<byte> gray, int width, int height, int limit)
    {
        int stride = Math.Max(1, width * height / limit);
        var points = new List<(int, int)>();
        for (int index = 0; index < width * height; index += stride)
            if (gray[index] < InkThreshold) points.Add((index % width, index / width));
        return points;
    }

    // Sum of squared row counts after shearing every ink point by the angle: highest when the
    // shear lines the text up into rows.
    private static double ProfileScore(List<(int X, int Y)> ink, int width, int height, double angle)
    {
        double tangent = Math.Tan(angle * Math.PI / 180);
        int margin = (int)Math.Ceiling(Math.Abs(tangent) * width) + 2;
        var rows = new int[height + 2 * margin];
        foreach (var (x, y) in ink)
        {
            int row = (int)Math.Round(y - x * tangent) + margin;
            if ((uint)row < (uint)rows.Length) rows[row]++;
        }
        double score = 0;
        foreach (int count in rows) score += (double)count * count;
        return score;
    }

    // Variance of a profile relative to its squared mean: how much it alternates between
    // inked lines and the gaps between them.
    private static double Contrast(double[] profile)
    {
        double mean = profile.Average();
        if (mean <= 0) return 0;
        double variance = profile.Sum(value => (value - mean) * (value - mean)) / profile.Length;
        return variance / (mean * mean);
    }
}
