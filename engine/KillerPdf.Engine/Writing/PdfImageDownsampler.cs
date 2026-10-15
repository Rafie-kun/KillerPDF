using System.IO.Compression;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Writing;

/// <summary>Encodes tightly packed 8-bit gray or RGB samples as a baseline JPEG.</summary>
public delegate byte[] PdfJpegEncoder(ReadOnlySpan<byte> samples, int width, int height, int components, int quality);

/// <summary>Choices for reducing the resolution of oversized images.</summary>
public sealed record PdfImageDownsampleOptions
{
    /// <summary>Gets the resolution, in dots per inch at the image's largest placement, that
    /// downsampled images are reduced to.</summary>
    public double TargetDpi { get; init; } = 150;
    /// <summary>Gets the resolution above which an image is downsampled.</summary>
    public double ThresholdDpi { get; init; } = 225;
    /// <summary>Gets the JPEG quality, 1 to 100, used when an encoder is supplied.</summary>
    public int JpegQuality { get; init; } = 80;
    /// <summary>Gets the JPEG encoder. Without one, resampled images are stored losslessly.</summary>
    public PdfJpegEncoder? JpegEncoder { get; init; }

    internal void Validate()
    {
        if (!double.IsFinite(TargetDpi) || TargetDpi is < 36 or > 1200)
            throw new ArgumentOutOfRangeException(nameof(TargetDpi), "The target resolution must be between 36 and 1200 dpi.");
        if (!double.IsFinite(ThresholdDpi) || ThresholdDpi < TargetDpi)
            throw new ArgumentOutOfRangeException(nameof(ThresholdDpi), "The threshold must be at least the target resolution.");
        if (JpegQuality is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(JpegQuality), "JPEG quality must be between 1 and 100.");
    }
}

/// <summary>The downsampled document and how much its images shrank.</summary>
public sealed record PdfImageDownsampleResult(
    ReadOnlyMemory<byte> Data, int ImagesResampled, long ImageBytesBefore, long ImageBytesAfter);

/// <summary>
/// Reduces images drawn at a higher resolution than the page needs (#433), the image part of
/// Reduce File Size. An image is only resampled when every place it is used is known: images
/// that forms, patterns or annotation appearances also use, masked images, and unusual color
/// spaces and encodings keep their full resolution.
/// </summary>
public static class PdfImageDownsampler
{
    private static readonly PdfName ResourcesName = new("Resources"u8);
    private static readonly PdfName XObjectName = new("XObject"u8);
    private static readonly PdfName PatternName = new("Pattern"u8);
    private static readonly PdfName SubtypeName = new("Subtype"u8);
    private static readonly PdfName ImageName = new("Image"u8);
    private static readonly PdfName FormName = new("Form"u8);
    private static readonly PdfName AnnotsName = new("Annots"u8);
    private static readonly PdfName AppearanceName = new("AP"u8);
    private static readonly PdfName WidthName = new("Width"u8);
    private static readonly PdfName HeightName = new("Height"u8);
    private static readonly PdfName BitsName = new("BitsPerComponent"u8);
    private static readonly PdfName ColorSpaceName = new("ColorSpace"u8);
    private static readonly PdfName FilterName = new("Filter"u8);
    private static readonly PdfName DecodeParmsName = new("DecodeParms"u8);
    private static readonly PdfName LengthName = new("Length"u8);
    private static readonly PdfName DecodedLengthName = new("DL"u8);
    private static readonly PdfName FlateName = new("FlateDecode"u8);
    private static readonly PdfName DctName = new("DCTDecode"u8);

    /// <summary>Returns the document with oversized images resampled, or the original bytes
    /// when no image needed it or none came out smaller.</summary>
    public static PdfImageDownsampleResult Downsample(PdfDocument document, PdfImageDownsampleOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Dictionary<int, double> candidates = FindCandidates(document, options, cancellationToken);
        if (candidates.Count == 0) return new PdfImageDownsampleResult(document.Source, 0, 0, 0);

        var builder = new PdfIncrementalUpdateBuilder(document);
        int resampled = 0;
        long before = 0, after = 0;
        foreach ((int objectNumber, double dpi) in candidates.OrderBy(pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (document.Resolve(objectNumber) is not PdfStream image) continue;
            PdfStream? replacement;
            try { replacement = Resample(document, image, options.TargetDpi / dpi, options); }
            catch (Exception error) when (error is FormatException or NotSupportedException
                or PdfFilterException or InvalidOperationException)
            {
                continue;   // an image that does not decode cleanly is left exactly as it was
            }
            if (replacement is null || replacement.EncodedData.Length >= image.EncodedData.Length) continue;
            builder.ReplaceObject(objectNumber, replacement);
            resampled++;
            before += image.EncodedData.Length;
            after += replacement.EncodedData.Length;
        }
        return resampled == 0
            ? new PdfImageDownsampleResult(document.Source, 0, 0, 0)
            : new PdfImageDownsampleResult(builder.Build(), resampled, before, after);
    }

    // Image object numbers drawn above the threshold, each with the lowest resolution any of
    // its placements uses, which is the one that needs the most pixels.
    private static Dictionary<int, double> FindCandidates(PdfDocument document, PdfImageDownsampleOptions options,
        CancellationToken cancellationToken)
    {
        var lowest = new Dictionary<int, double>();
        var excluded = new HashSet<int>();
        PdfPageTree tree = PdfPageTree.Read(document);
        var reader = new PdfPageContentReader(document);
        foreach (PdfPageTreeEntry page in tree.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExcludeAnnotationImages(document, page.Dictionary, excluded);
            if (!page.InheritedValues.TryGetValue(ResourcesName, out PdfObject? resourcesValue)
                || Resolve(document, resourcesValue) is not PdfDictionary resources) continue;
            ExcludePatternImages(document, resources, excluded, 0);

            var images = new Dictionary<string, int>(StringComparer.Ordinal);
            var nestedNames = new HashSet<string>(StringComparer.Ordinal);
            if (resources.TryGetValue(XObjectName, out PdfObject? xObjectsValue)
                && Resolve(document, xObjectsValue) is PdfDictionary xObjects)
            {
                foreach ((PdfName name, PdfObject value) in xObjects)
                {
                    if (Resolve(document, value) is not PdfStream stream) continue;
                    if (IsSubtype(document, stream, ImageName))
                    {
                        if (value is PdfIndirectReference reference) images[name.ValueAsLatin1()] = reference.ObjectNumber;
                    }
                    else if (IsSubtype(document, stream, FormName))
                        ExcludeNested(document, stream.Dictionary, excluded, nestedNames, 0);
                }
            }
            if (images.Count == 0) continue;

            PdfPageContent content;
            try { content = reader.Read(page.Index, cancellationToken); }
            catch (Exception error) when (error is FormatException or NotSupportedException)
            {
                foreach (int objectNumber in images.Values) excluded.Add(objectNumber);
                continue;
            }
            foreach (PdfExtractedImage placement in content.Images)
            {
                if (placement.IsInline || placement.ResourceName is not string name
                    || !images.TryGetValue(name, out int objectNumber)) continue;
                // A form on this page that uses the same name could be the one drawing it.
                if (nestedNames.Contains(name)) { excluded.Add(objectNumber); continue; }
                double dpi = Math.Min(placement.HorizontalDpi ?? 0, placement.VerticalDpi ?? 0);
                if (!(dpi > 0)) { excluded.Add(objectNumber); continue; }
                lowest[objectNumber] = lowest.TryGetValue(objectNumber, out double known) ? Math.Min(known, dpi) : dpi;
            }
        }
        return lowest.Where(pair => !excluded.Contains(pair.Key) && pair.Value > options.ThresholdDpi)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    // Every image a form uses, at any depth, keeps its resolution, and every name the form's
    // resources use is noted so the page's own image under that name is not misjudged.
    private static void ExcludeNested(PdfDocument document, PdfDictionary owner, HashSet<int> excluded,
        HashSet<string> names, int depth)
    {
        if (depth > 16 || !owner.TryGetValue(ResourcesName, out PdfObject? resourcesValue)
            || Resolve(document, resourcesValue) is not PdfDictionary resources) return;
        ExcludePatternImages(document, resources, excluded, depth);
        if (!resources.TryGetValue(XObjectName, out PdfObject? xObjectsValue)
            || Resolve(document, xObjectsValue) is not PdfDictionary xObjects) return;
        foreach ((PdfName name, PdfObject value) in xObjects)
        {
            names.Add(name.ValueAsLatin1());
            if (value is PdfIndirectReference reference) excluded.Add(reference.ObjectNumber);
            if (Resolve(document, value) is PdfStream stream && IsSubtype(document, stream, FormName))
                ExcludeNested(document, stream.Dictionary, excluded, names, depth + 1);
        }
    }

    private static void ExcludePatternImages(PdfDocument document, PdfDictionary resources, HashSet<int> excluded, int depth)
    {
        if (!resources.TryGetValue(PatternName, out PdfObject? patternsValue)
            || Resolve(document, patternsValue) is not PdfDictionary patterns) return;
        foreach (PdfObject pattern in patterns.Values)
            if (Resolve(document, pattern) is PdfStream stream)
                ExcludeNested(document, stream.Dictionary, excluded, [], depth + 1);
    }

    private static void ExcludeAnnotationImages(PdfDocument document, PdfDictionary page, HashSet<int> excluded)
    {
        if (!page.TryGetValue(AnnotsName, out PdfObject? annotsValue)
            || Resolve(document, annotsValue) is not PdfArray annotations) return;
        foreach (PdfObject annotationValue in annotations)
        {
            if (Resolve(document, annotationValue) is not PdfDictionary annotation
                || !annotation.TryGetValue(AppearanceName, out PdfObject? appearanceValue)
                || Resolve(document, appearanceValue) is not PdfDictionary appearances) continue;
            foreach (PdfObject state in appearances.Values)
            {
                PdfObject resolved = Resolve(document, state);
                IEnumerable<PdfObject> streams = resolved is PdfDictionary states ? states.Values : [resolved];
                foreach (PdfObject value in streams)
                    if (Resolve(document, value) is PdfStream stream)
                        ExcludeNested(document, stream.Dictionary, excluded, [], 0);
            }
        }
    }

    // The resampled image, or null when the image is of a kind left alone.
    private static PdfStream? Resample(PdfDocument document, PdfStream image, double scale,
        PdfImageDownsampleOptions options)
    {
        PdfDictionary dictionary = image.Dictionary;
        if (dictionary.ContainsKey(new PdfName("ImageMask"u8)) || dictionary.ContainsKey(new PdfName("SMask"u8))
            || dictionary.ContainsKey(new PdfName("Mask"u8)) || dictionary.ContainsKey(new PdfName("Decode"u8))
            || dictionary.ContainsKey(new PdfName("SMaskInData"u8)))
            return null;
        if (Integer(document, dictionary, WidthName) is not int width || width <= 0
            || Integer(document, dictionary, HeightName) is not int height || height <= 0
            || Integer(document, dictionary, BitsName) is not 8
            || Components(document, dictionary) is not int components) return null;
        bool jpeg;
        switch (SoleFilter(document, dictionary))
        {
            case null: jpeg = false; break;
            case var filter when filter.Equals(FlateName): jpeg = false; break;
            case var filter when filter.Equals(DctName): jpeg = true; break;
            default: return null;
        }

        int newWidth = Math.Max(1, (int)Math.Round(width * scale));
        int newHeight = Math.Max(1, (int)Math.Round(height * scale));
        if (newWidth >= width || newHeight >= height) return null;

        byte[] samples;
        if (jpeg)
        {
            JpegDecodedImage decoded = document.DecodeJpegImage(image, PdfStreamDecoder.DefaultMaximumDecodedBytes, 1);
            if (decoded.Width != width || decoded.Height != height || decoded.Components != components) return null;
            samples = decoded.Samples;
        }
        else
        {
            int expected = checked(width * height * components);
            samples = document.DecodeImageSamples(image, expected, expected);
            if (samples.Length != expected) return null;
        }

        byte[] smaller = AreaAverage(samples, width, height, components, newWidth, newHeight);
        PdfName encoding;
        byte[] data;
        if (options.JpegEncoder is PdfJpegEncoder encoder)
        {
            data = encoder(smaller, newWidth, newHeight, components, options.JpegQuality);
            encoding = DctName;
        }
        else
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
                zlib.Write(smaller);
            data = output.ToArray();
            encoding = FlateName;
        }

        var entries = dictionary.Where(pair => !pair.Key.Equals(FilterName) && !pair.Key.Equals(DecodeParmsName)
                && !pair.Key.Equals(LengthName) && !pair.Key.Equals(DecodedLengthName)
                && !pair.Key.Equals(WidthName) && !pair.Key.Equals(HeightName) && !pair.Key.Equals(BitsName))
            .ToList();
        entries.Add(new(WidthName, new PdfInteger(newWidth)));
        entries.Add(new(HeightName, new PdfInteger(newHeight)));
        entries.Add(new(BitsName, new PdfInteger(8)));
        entries.Add(new(FilterName, encoding));
        return new PdfStream(new PdfDictionary(entries), data);
    }

    /// <summary>Box-filter resampling: each output sample is the mean of the source samples
    /// its area covers, weighted by how much of each it covers.</summary>
    internal static byte[] AreaAverage(byte[] source, int width, int height, int components, int newWidth, int newHeight)
    {
        var result = new byte[checked(newWidth * newHeight * components)];
        double xScale = (double)width / newWidth, yScale = (double)height / newHeight;
        var sums = new double[components];
        for (int y = 0; y < newHeight; y++)
        {
            double top = y * yScale, bottom = top + yScale;
            for (int x = 0; x < newWidth; x++)
            {
                double left = x * xScale, right = left + xScale;
                Array.Clear(sums);
                double area = 0;
                for (int sy = (int)top; sy < Math.Min(height, (int)Math.Ceiling(bottom)); sy++)
                {
                    double coverY = Math.Min(bottom, sy + 1) - Math.Max(top, sy);
                    for (int sx = (int)left; sx < Math.Min(width, (int)Math.Ceiling(right)); sx++)
                    {
                        double weight = coverY * (Math.Min(right, sx + 1) - Math.Max(left, sx));
                        int offset = (sy * width + sx) * components;
                        for (int c = 0; c < components; c++) sums[c] += source[offset + c] * weight;
                        area += weight;
                    }
                }
                int target = (y * newWidth + x) * components;
                for (int c = 0; c < components; c++)
                    result[target + c] = (byte)Math.Clamp((int)Math.Round(sums[c] / area), 0, 255);
            }
        }
        return result;
    }

    // Gray or RGB images only, named directly or through a one or three channel ICC profile.
    private static int? Components(PdfDocument document, PdfDictionary dictionary)
    {
        if (!dictionary.TryGetValue(ColorSpaceName, out PdfObject? value)) return null;
        PdfObject space = Resolve(document, value);
        if (space is PdfName name)
            return name.ValueAsLatin1() switch { "DeviceGray" => 1, "DeviceRGB" => 3, _ => null };
        if (space is PdfArray { Count: 2 } array && Resolve(document, array[0]) is PdfName family
            && family.ValueAsLatin1() == "ICCBased" && Resolve(document, array[1]) is PdfStream profile
            && Integer(document, profile.Dictionary, new PdfName("N"u8)) is int count and (1 or 3))
            return count;
        return null;
    }

    private static PdfName? SoleFilter(PdfDocument document, PdfDictionary dictionary)
    {
        if (!dictionary.TryGetValue(FilterName, out PdfObject? value)) return null;
        PdfObject filter = Resolve(document, value);
        if (filter is PdfArray { Count: 1 } single) filter = Resolve(document, single[0]);
        return filter as PdfName ?? new PdfName("Unsupported"u8);
    }

    private static int? Integer(PdfDocument document, PdfDictionary dictionary, PdfName key) =>
        dictionary.TryGetValue(key, out PdfObject? value) && Resolve(document, value) is PdfInteger integer
            && integer.Value is >= 0 and <= int.MaxValue ? (int)integer.Value : null;

    private static bool IsSubtype(PdfDocument document, PdfStream stream, PdfName subtype) =>
        stream.Dictionary.TryGetValue(SubtypeName, out PdfObject? value)
        && Resolve(document, value) is PdfName name && name.Equals(subtype);

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth > 32) throw new FormatException("An indirect object chain is too deep.");
            value = document.Resolve(reference);
        }
        return value;
    }
}
