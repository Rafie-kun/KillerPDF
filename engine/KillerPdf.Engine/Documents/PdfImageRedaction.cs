using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using Matrix = KillerPdf.Engine.Parsing.PdfTextContentReader.Matrix;

namespace KillerPdf.Engine.Documents;

/// <summary>
/// Paints over the pixels of an image that lie under redaction regions, so a partly covered
/// image keeps the rest of its picture and the page stays editable.
/// </summary>
/// <remarks>
/// Gray, RGB and CMYK images with 8 bits per component, and 1-bit gray images such as scans, are
/// supported when their decode array is the default one or the inverted one of a 1-bit image. A soft mask is
/// painted opaque under the regions too, so its shape cannot show what was there. Images with a
/// color-key or stencil mask, and JPEG 2000 images, are left to the page image fallback.
/// </remarks>
internal static class PdfImageRedaction
{
    /// <summary>Prefix of the resource names given to images whose pixels were painted over.</summary>
    internal const string ResourcePrefix = "KPRI";

    private static readonly PdfName[] MaskKeys = [Name("ImageMask"), Name("Mask"), Name("SMaskInData")];
    /// <summary>
    /// Entries of an image or form that point at other copies of what it shows, such as an alternate
    /// image or a producer's private editable data. A rewritten copy leaves them behind.
    /// </summary>
    internal static readonly PdfName[] CopyKeys = [Name("Alternates"), Name("OPI"), Name("PieceInfo"), Name("Metadata")];
    private static readonly PdfName[] DroppedKeys = [Name("Filter"), Name("DecodeParms"), Name("Length"), Name("DL"), .. CopyKeys];

    /// <summary>Decoded image samples, one row after another from the top.</summary>
    internal sealed class Samples(PdfStream source, int width, int height, int components, int bits, bool inverted, byte[] data)
    {
        internal PdfStream Source { get; } = source;
        /// <summary>Gets the decoded soft mask, when the image has one.</summary>
        internal Samples? Mask { get; init; }
        internal int Width { get; } = width;
        internal int Height { get; } = height;
        internal int Components { get; } = components;
        internal int Bits { get; } = bits;
        internal bool Inverted { get; } = inverted;
        internal byte[] Data { get; } = data;
        internal int RowBytes => Bits == 1 ? (Width + 7) / 8 : Width * Components;
    }

    /// <summary>One pixel rectangle, end exclusive, and the sample values painted into it.</summary>
    internal readonly record struct Cover(int Left, int Top, int Right, int Bottom, byte[] Fill);

    /// <summary>The pixel rectangles to paint in an image and in its soft mask.</summary>
    internal sealed record Plan(IReadOnlyList<Cover> Image, IReadOnlyList<Cover> Mask)
    {
        /// <summary>Text equal for two plans that paint the same rectangles.</summary>
        internal string Key => string.Join(';', Image.Concat(Mask).Select(cover =>
            $"{cover.Left},{cover.Top},{cover.Right},{cover.Bottom}"));

        /// <summary>This plan and then another, so that the other's paint wins where they meet.</summary>
        internal Plan Then(Plan next) => new([.. Image, .. next.Image], [.. Mask, .. next.Mask]);
    }

    /// <summary>Decodes an image whose pixels can be painted directly, or returns null.</summary>
    internal static Samples? Decode(PdfDocument document, PdfStream image)
    {
        if (MaskKeys.Any(image.Dictionary.ContainsKey)) return null;
        Samples? samples = DecodeSamples(document, image, Components(document, image.Dictionary));
        if (samples is null || !image.Dictionary.TryGetValue(Name("SMask"), out PdfObject? maskValue)) return samples;
        // The soft mask is a gray image of its own; any other kind leaves the image alone.
        if (Resolve(document, maskValue) is not PdfStream mask || MaskKeys.Any(mask.Dictionary.ContainsKey)
            || mask.Dictionary.ContainsKey(Name("SMask"))) return null;
        Samples? alpha = DecodeSamples(document, mask, 1);
        return alpha is null || alpha.Inverted ? null : new Samples(image, samples.Width, samples.Height,
            samples.Components, samples.Bits, samples.Inverted, samples.Data) { Mask = alpha };
    }

    private static Samples? DecodeSamples(PdfDocument document, PdfStream image, int? colorComponents)
    {
        PdfDictionary dictionary = image.Dictionary;
        if (Integer(document, dictionary, "Width") is not int width || width <= 0
            || Integer(document, dictionary, "Height") is not int height || height <= 0
            || Integer(document, dictionary, "BitsPerComponent") is not int bits
            || colorComponents is not int components) return null;
        if (bits != 8 && !(bits == 1 && components == 1)) return null;
        bool inverted = false;
        // A decode array is accepted when it is the default one, or the inverted one of a 1-bit image.
        if (dictionary.TryGetValue(Name("Decode"), out PdfObject? decodeValue))
        {
            if (Resolve(document, decodeValue) is not PdfArray decode || decode.Count != components * 2) return null;
            bool identity = Enumerable.Range(0, components).All(component =>
                IsNumber(document, decode[component * 2], 0) && IsNumber(document, decode[component * 2 + 1], 1));
            if (!identity)
            {
                if (bits != 1 || !IsNumber(document, decode[0], 1) || !IsNumber(document, decode[1], 0)) return null;
                inverted = true;
            }
        }
        string[] filters = Filters(document, dictionary);
        if (filters.Any(filter => filter is "JPXDecode" or "JPX")) return null;
        long expected = bits == 1 ? (long)((width + 7) / 8) * height : (long)width * height * components;
        if (expected > PdfStreamDecoder.DefaultMaximumDecodedBytes) return null;
        try
        {
            byte[] data;
            if (filters.Length > 0 && filters[^1] is "DCTDecode" or "DCT")
            {
                // JPEG CMYK may be stored inverted, so only gray and RGB JPEG images are painted.
                if (components == 4 || bits != 8) return null;
                JpegDecodedImage decoded = document.DecodeJpegImage(image, PdfStreamDecoder.DefaultMaximumDecodedBytes, 1);
                if (decoded.Width != width || decoded.Height != height || decoded.Components != components) return null;
                data = decoded.Samples;
            }
            else data = document.DecodeImageSamples(image, (int)expected, (int)expected);
            if (data.Length < expected) return null;
            if (data.Length > expected) data = data[..(int)expected];
            return new Samples(image, width, height, components, bits, inverted, data);
        }
        catch (Exception error) when (error is PdfFilterException or FormatException or InvalidDataException
            or NotSupportedException or InvalidOperationException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps each region onto the image's pixels through the placement matrix. Regions that miss
    /// the image are skipped; null means the placement cannot be inverted.
    /// </summary>
    internal static Plan? Covers(Matrix placement, Samples image,
        IEnumerable<(PdfContentBounds Bounds, PdfRgbColor Fill)> regions)
    {
        var list = regions.ToList();
        List<Cover>? colors = Covers(placement, image, list, opaque: false);
        if (colors is null) return null;
        List<Cover> alpha = image.Mask is Samples mask ? Covers(placement, mask, list, opaque: true) ?? [] : [];
        return new Plan(colors, alpha);
    }

    private static List<Cover>? Covers(Matrix placement, Samples image,
        List<(PdfContentBounds Bounds, PdfRgbColor Fill)> regions, bool opaque)
    {
        double determinant = placement.A * placement.D - placement.B * placement.C;
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-12) return null;
        var covers = new List<Cover>();
        foreach ((PdfContentBounds bounds, PdfRgbColor fill) in regions)
        {
            double minU = double.MaxValue, minV = double.MaxValue, maxU = double.MinValue, maxV = double.MinValue;
            foreach ((double x, double y) in new[] { (bounds.Left, bounds.Bottom), (bounds.Right, bounds.Bottom),
                (bounds.Left, bounds.Top), (bounds.Right, bounds.Top) })
            {
                double dx = x - placement.E, dy = y - placement.F;
                double u = (placement.D * dx - placement.C * dy) / determinant;
                double v = (-placement.B * dx + placement.A * dy) / determinant;
                minU = Math.Min(minU, u); maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v); maxV = Math.Max(maxV, v);
            }
            int left = Math.Clamp((int)Math.Floor(Math.Clamp(minU, 0, 1) * image.Width), 0, image.Width);
            int right = Math.Clamp((int)Math.Ceiling(Math.Clamp(maxU, 0, 1) * image.Width), 0, image.Width);
            int top = Math.Clamp((int)Math.Floor((1 - Math.Clamp(maxV, 0, 1)) * image.Height), 0, image.Height);
            int bottom = Math.Clamp((int)Math.Ceiling((1 - Math.Clamp(minV, 0, 1)) * image.Height), 0, image.Height);
            if (right > left && bottom > top)
                covers.Add(new Cover(left, top, right, bottom, opaque ? OpaqueSamples(image) : FillSamples(image, fill)));
        }
        return covers;
    }

    /// <summary>
    /// Plans the samples under the covered areas, given in the image's unit square, that a draw does
    /// not show. A sample shows if any of it lies in the clip, so painting the rest cannot change the draw.
    /// </summary>
    /// <param name="placement">The draw's matrix.</param>
    /// <param name="image">The decoded image.</param>
    /// <param name="covered">The covered areas.</param>
    /// <param name="clip">The bounds of what the draw shows.</param>
    /// <param name="shown">Set when a covered sample does lie in the clip.</param>
    internal static Plan HiddenPlan(Matrix placement, Samples image, IReadOnlyList<PdfContentBounds> covered,
        PdfContentBounds clip, out bool shown)
    {
        List<Cover> colors = Hidden(placement, image.Width, image.Height, covered, clip, FillSamples(image, default), out shown);
        List<Cover> alpha = image.Mask is Samples mask
            ? Hidden(placement, mask.Width, mask.Height, covered, clip, OpaqueSamples(mask), out _) : [];
        return new Plan(colors, alpha);
    }

    /// <summary>True when a draw hides a sample under the covered areas of an image this many samples in size.</summary>
    internal static bool HasHidden(Matrix placement, int width, int height, IReadOnlyList<PdfContentBounds> covered,
        PdfContentBounds clip, out bool shown) =>
        Hidden(placement, width, height, covered, clip, [], out shown).Count != 0;

    private static List<Cover> Hidden(Matrix placement, int width, int height, IReadOnlyList<PdfContentBounds> covered,
        PdfContentBounds clip, byte[] fill, out bool shown)
    {
        shown = false;
        var hidden = new List<Cover>();
        var rows = new Dictionary<(int Left, int Right), int>();
        double determinant = placement.A * placement.D - placement.B * placement.C;
        bool invertible = clip.Right > clip.Left && clip.Top > clip.Bottom
            && double.IsFinite(determinant) && Math.Abs(determinant) >= 1e-12;
        bool upright = Math.Abs(placement.B) < 1e-8 && Math.Abs(placement.C) < 1e-8
            || Math.Abs(placement.A) < 1e-8 && Math.Abs(placement.D) < 1e-8;
        // Two boxes tell a sample from the clip: the clip's own box mapped into the unit square, and,
        // when the draw turns the image by anything but a quarter, the sample's box on the page.
        double clipMinU = double.MaxValue, clipMaxU = double.MinValue, clipMinV = double.MaxValue, clipMaxV = double.MinValue;
        if (invertible)
            foreach ((double x, double y) in new[] { (clip.Left, clip.Bottom), (clip.Right, clip.Bottom),
                (clip.Left, clip.Top), (clip.Right, clip.Top) })
            {
                double dx = x - placement.E, dy = y - placement.F;
                double u = (placement.D * dx - placement.C * dy) / determinant;
                double v = (-placement.B * dx + placement.A * dy) / determinant;
                clipMinU = Math.Min(clipMinU, u); clipMaxU = Math.Max(clipMaxU, u);
                clipMinV = Math.Min(clipMinV, v); clipMaxV = Math.Max(clipMaxV, v);
            }
        double toleranceU = PdfContentRedaction.OverlapTolerance / Math.Max(1e-12, Math.Sqrt(placement.A * placement.A + placement.B * placement.B));
        double toleranceV = PdfContentRedaction.OverlapTolerance / Math.Max(1e-12, Math.Sqrt(placement.C * placement.C + placement.D * placement.D));
        double sampleWidth = 1d / width, sampleHeight = 1d / height;
        foreach (PdfContentBounds area in covered)
        {
            int left = Math.Clamp((int)Math.Floor(Math.Clamp(area.Left, 0, 1) * width), 0, width);
            int right = Math.Clamp((int)Math.Ceiling(Math.Clamp(area.Right, 0, 1) * width), 0, width);
            int top = Math.Clamp((int)Math.Floor((1 - Math.Clamp(area.Top, 0, 1)) * height), 0, height);
            int bottom = Math.Clamp((int)Math.Ceiling((1 - Math.Clamp(area.Bottom, 0, 1)) * height), 0, height);
            for (int y = top; y < bottom; y++)
            {
                double v0 = 1 - (y + 1) * sampleHeight, v1 = 1 - y * sampleHeight;
                bool rowShown = invertible && v1 > clipMinV + toleranceV && v0 < clipMaxV - toleranceV;
                int start = -1;
                for (int x = left; x < right; x++)
                {
                    double u0 = x * sampleWidth, u1 = (x + 1) * sampleWidth;
                    bool shows = rowShown && u1 > clipMinU + toleranceU && u0 < clipMaxU - toleranceU
                        && (upright || SampleBoxMeetsClip(placement, u0, v0, u1, v1, clip));
                    if (shows)
                    {
                        shown = true;
                        if (start >= 0) Add(start, x, y);
                        start = -1;
                    }
                    else if (start < 0) start = x;
                }
                if (start >= 0) Add(start, right, y);
            }
        }
        return hidden;

        // One rectangle for each run of rows with the same span, rather than one for each row.
        void Add(int from, int to, int y)
        {
            if (rows.TryGetValue((from, to), out int index) && hidden[index].Bottom == y)
                hidden[index] = hidden[index] with { Bottom = y + 1 };
            else
            {
                rows[(from, to)] = hidden.Count;
                hidden.Add(new Cover(from, y, to, y + 1, fill));
            }
        }
    }

    private static bool SampleBoxMeetsClip(Matrix placement, double u0, double v0, double u1, double v1, PdfContentBounds clip)
    {
        double a = placement.A * u0 + placement.C * v0 + placement.E, b = placement.B * u0 + placement.D * v0 + placement.F;
        double c = placement.A * u1 + placement.C * v0 + placement.E, d = placement.B * u1 + placement.D * v0 + placement.F;
        double e = placement.A * u0 + placement.C * v1 + placement.E, f = placement.B * u0 + placement.D * v1 + placement.F;
        double g = placement.A * u1 + placement.C * v1 + placement.E, h = placement.B * u1 + placement.D * v1 + placement.F;
        double tolerance = PdfContentRedaction.OverlapTolerance;
        return Math.Max(a, Math.Max(c, Math.Max(e, g))) > clip.Left + tolerance
            && Math.Min(a, Math.Min(c, Math.Min(e, g))) < clip.Right - tolerance
            && Math.Max(b, Math.Max(d, Math.Max(f, h))) > clip.Bottom + tolerance
            && Math.Min(b, Math.Min(d, Math.Min(f, h))) < clip.Top - tolerance;
    }

    /// <summary>The image's size in samples, or null when its dictionary does not say.</summary>
    internal static (int Width, int Height)? Size(PdfDocument document, PdfStream image) =>
        Integer(document, image.Dictionary, "Width") is int width and > 0
        && Integer(document, image.Dictionary, "Height") is int height and > 0 ? (width, height) : null;

    /// <summary>True when the image is stored with no filter.</summary>
    internal static bool IsUnfiltered(PdfDocument document, PdfStream image) => Filters(document, image.Dictionary).Length == 0;

    /// <summary>True when the image is stored the way the writer compresses an unfiltered one.</summary>
    internal static bool IsFlate(PdfDocument document, PdfStream image) =>
        Filters(document, image.Dictionary) is ["FlateDecode" or "Fl"];

    /// <summary>
    /// Gets the bytes that identify a stream's content, the same for a byte-identical copy and for
    /// the compressed form the writer can give an unfiltered stream.
    /// </summary>
    internal static byte[] ContentBytes(PdfDocument document, PdfStream stream)
    {
        if (Filters(document, stream.Dictionary).All(filter => filter is "FlateDecode" or "Fl" or "LZWDecode" or "LZW"
            or "ASCII85Decode" or "A85" or "ASCIIHexDecode" or "AHx" or "RunLengthDecode" or "RL"))
            try { return document.DecodeStream(stream); }
            catch (Exception error) when (error is PdfFilterException or FormatException or InvalidDataException
                or NotSupportedException or InvalidOperationException or OverflowException) { }
        return stream.EncodedData.ToArray();
    }

    /// <summary>Text equal for two images of the same size holding the same content.</summary>
    internal static string Identity(PdfDocument document, PdfStream image) => string.Join(':',
        Integer(document, image.Dictionary, "Width"), Integer(document, image.Dictionary, "Height"),
        Integer(document, image.Dictionary, "BitsPerComponent"),
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ContentBytes(document, image))));

    /// <summary>Paints the plan into the image and its soft mask.</summary>
    internal static void Paint(Samples image, Plan plan)
    {
        Paint(image, plan.Image);
        if (image.Mask is Samples mask) Paint(mask, plan.Mask);
    }

    /// <summary>True when the image and its soft mask hold what the plan paints.</summary>
    internal static bool IsPainted(Samples image, Plan plan) =>
        IsPainted(image, plan.Image) && (image.Mask is not Samples mask || IsPainted(mask, plan.Mask));

    private static void Paint(Samples image, IEnumerable<Cover> covers)
    {
        foreach (Cover cover in covers)
            for (int y = cover.Top; y < cover.Bottom; y++)
                for (int x = cover.Left; x < cover.Right; x++)
                    Write(image, x, y, cover.Fill);
    }

    private static bool IsPainted(Samples image, IReadOnlyList<Cover> covers)
    {
        for (int index = 0; index < covers.Count; index++)
        {
            Cover cover = covers[index];
            for (int y = cover.Top; y < cover.Bottom; y++)
                for (int x = cover.Left; x < cover.Right; x++)
                {
                    Cover last = cover;
                    for (int later = index + 1; later < covers.Count; later++)
                        if (Contains(covers[later], x, y)) last = covers[later];
                    if (!Matches(image, x, y, last.Fill)) return false;
                }
        }
        return true;
    }

    /// <summary>
    /// Builds the replacement image stream, Flate compressed, keeping the other image entries and
    /// pointing at the replacement soft mask when there is one.
    /// </summary>
    internal static PdfStream Encode(Samples image, PdfIndirectReference? mask = null)
    {
        var entries = image.Source.Dictionary.Where(entry => !DroppedKeys.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        entries[Name("Filter")] = Name("FlateDecode");
        if (mask is not null) entries[Name("SMask")] = mask;
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(image.Data);
        return new PdfStream(new PdfDictionary(entries), output.ToArray());
    }

    private static bool Contains(Cover cover, int x, int y) =>
        x >= cover.Left && x < cover.Right && y >= cover.Top && y < cover.Bottom;

    // Fully opaque soft-mask samples.
    private static byte[] OpaqueSamples(Samples mask) => [(byte)(mask.Bits == 1 ? 1 : 255)];

    // The sample values that show the fill color in the image's color space.
    private static byte[] FillSamples(Samples image, PdfRgbColor fill)
    {
        double r = Math.Clamp(fill.Red, 0, 1), g = Math.Clamp(fill.Green, 0, 1), b = Math.Clamp(fill.Blue, 0, 1);
        double gray = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        if (image.Bits == 1)
        {
            int bit = gray >= 0.5 ? 1 : 0;
            return [(byte)(image.Inverted ? 1 - bit : bit)];
        }
        static byte Byte(double value) => (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
        switch (image.Components)
        {
            case 1: return [Byte(gray)];
            case 3: return [Byte(r), Byte(g), Byte(b)];
            default:
                double k = 1 - Math.Max(r, Math.Max(g, b));
                if (k >= 1) return [0, 0, 0, 255];
                return [Byte((1 - r - k) / (1 - k)), Byte((1 - g - k) / (1 - k)), Byte((1 - b - k) / (1 - k)), Byte(k)];
        }
    }

    private static void Write(Samples image, int x, int y, byte[] fill)
    {
        int row = y * image.RowBytes;
        if (image.Bits == 1)
        {
            int mask = 0x80 >> (x & 7);
            if (fill[0] != 0) image.Data[row + (x >> 3)] |= (byte)mask;
            else image.Data[row + (x >> 3)] &= (byte)~mask;
            return;
        }
        Array.Copy(fill, 0, image.Data, row + x * image.Components, image.Components);
    }

    private static bool Matches(Samples image, int x, int y, byte[] fill)
    {
        int row = y * image.RowBytes;
        if (image.Bits == 1)
            return ((image.Data[row + (x >> 3)] >> (7 - (x & 7))) & 1) == fill[0];
        int offset = row + x * image.Components;
        for (int component = 0; component < image.Components; component++)
            if (image.Data[offset + component] != fill[component]) return false;
        return true;
    }

    // Gray, RGB or CMYK, named directly or through an ICC profile with as many channels.
    private static int? Components(PdfDocument document, PdfDictionary dictionary)
    {
        if (!dictionary.TryGetValue(Name("ColorSpace"), out PdfObject? value)) return null;
        PdfObject space = Resolve(document, value);
        if (space is PdfArray { Count: 1 } single) space = Resolve(document, single[0]);
        if (space is PdfName name)
            return name.ValueAsLatin1() switch { "DeviceGray" or "G" => 1, "DeviceRGB" or "RGB" => 3, "DeviceCMYK" or "CMYK" => 4, _ => null };
        if (space is PdfArray { Count: 2 } array && Resolve(document, array[0]) is PdfName family
            && family.ValueAsLatin1() == "ICCBased" && Resolve(document, array[1]) is PdfStream profile
            && Integer(document, profile.Dictionary, "N") is int count and (1 or 3 or 4))
            return count;
        return null;
    }

    private static string[] Filters(PdfDocument document, PdfDictionary dictionary)
    {
        if (!dictionary.TryGetValue(Name("Filter"), out PdfObject? value)) return [];
        return Resolve(document, value) switch
        {
            PdfName name => [name.ValueAsLatin1()],
            PdfArray array => [.. array.Select(item => Resolve(document, item) is PdfName name ? name.ValueAsLatin1() : string.Empty)],
            _ => [string.Empty]
        };
    }

    private static int? Integer(PdfDocument document, PdfDictionary dictionary, string key) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value) && Resolve(document, value) is PdfInteger integer
            && integer.Value is >= int.MinValue and <= int.MaxValue ? (int)integer.Value : null;

    private static bool IsNumber(PdfDocument document, PdfObject value, double expected) => Resolve(document, value) switch
    {
        PdfInteger integer => integer.Value == expected,
        PdfReal real => real.Value == expected,
        _ => false
    };

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32) throw new FormatException("Reference nesting limit exceeded.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
