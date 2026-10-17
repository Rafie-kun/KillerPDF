using System.Globalization;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>The kind of value a measurement annotation shows.</summary>
public enum PdfMeasurementKind
{
    /// <summary>A straight distance between two points (a dimension line).</summary>
    Distance,
    /// <summary>The length around a path (a dimension polyline).</summary>
    Perimeter,
    /// <summary>The area inside a polygon (a dimension polygon).</summary>
    Area,
    /// <summary>The angle at the middle point of a three-point polyline.</summary>
    Angle
}

/// <summary>One measurement annotation saved in a page's /Annots array.</summary>
public sealed record PdfMeasurementAnnotationInfo
{
    /// <summary>Gets the zero-based page index.</summary>
    public required int PageIndex { get; init; }
    /// <summary>Gets the zero-based position in the page /Annots array.</summary>
    public required int AnnotationIndex { get; init; }
    /// <summary>Gets what the annotation measures.</summary>
    public required PdfMeasurementKind Kind { get; init; }
    /// <summary>
    /// Gets the measured points in default user space. A closed path does not repeat its first
    /// point at the end; <see cref="Closed"/> says it closes.
    /// </summary>
    public required IReadOnlyList<PdfMeasurementPoint> Points { get; init; }
    /// <summary>Gets whether a perimeter path returns to its first point.</summary>
    public bool Closed { get; init; }
    /// <summary>Gets the annotation's own scale, or null when it has no /Measure dictionary.</summary>
    public PdfMeasurementProfile? Profile { get; init; }
    /// <summary>Gets the shown value text from /Contents.</summary>
    public string? Contents { get; init; }
    /// <summary>Gets the stroke color, when the annotation records one.</summary>
    public PdfRgbColor? Color { get; init; }
    /// <summary>Gets the fill color of an area, when the annotation records one.</summary>
    public PdfRgbColor? FillColor { get; init; }
    /// <summary>Gets the stroke width in points.</summary>
    public double LineWidth { get; init; } = 1;
    /// <summary>Gets the constant opacity from /CA.</summary>
    public double Opacity { get; init; } = 1;
    /// <summary>Gets the label's center point, when the annotation records where its label sits.</summary>
    public PdfMeasurementPoint? CaptionCenter { get; init; }
    /// <summary>Gets the label's font size, when the annotation records one.</summary>
    public double? CaptionFontSize { get; init; }
}

/// <summary>
/// Reads the measurement annotations and page scales saved in a document, so a viewer can show
/// and edit them again.
/// </summary>
public static class PdfMeasurementAnnotationReader
{
    private const string CaptionKey = "KillerPDFCaption";

    /// <summary>
    /// Returns every dimension line, polyline, and polygon in page and annotation order. A
    /// three-point dimension polyline with no scale of its own is an angle.
    /// </summary>
    public static IReadOnlyList<PdfMeasurementAnnotationInfo> Read(PdfDocument document)
    {
        RequireDecrypted(document, "measurements");
        var results = new List<PdfMeasurementAnnotationInfo>();
        foreach (PdfPageTreeEntry page in PdfPageTree.Read(document).Pages)
        {
            if (!page.Dictionary.TryGetValue(Name("Annots"), out PdfObject? annotationsValue)
                || Resolve(document, annotationsValue) is not PdfArray annotations)
                continue;
            for (int index = 0; index < annotations.Count; index++)
            {
                if (Resolve(document, annotations[index]) is not PdfDictionary annotation)
                    continue;
                PdfMeasurementAnnotationInfo? info = ReadAnnotation(
                    document, annotation, page.Index, index);
                if (info is not null) results.Add(info);
            }
        }
        return Array.AsReadOnly(results.ToArray());
    }

    /// <summary>
    /// Returns the rectilinear scales in each page's /VP viewports. A viewport that covers the
    /// whole media box becomes the page's scale; a smaller one becomes a regional scale. When
    /// several whole-page viewports overlap, the last one wins, as ISO 32000 12.9.1 specifies.
    /// </summary>
    public static IReadOnlyList<PdfMeasurementProfileAssignment> ReadPageScales(PdfDocument document)
    {
        RequireDecrypted(document, "page scales");
        var assignments = new List<PdfMeasurementProfileAssignment>();
        foreach (PdfPageTreeEntry page in PdfPageTree.Read(document).Pages)
        {
            if (!page.Dictionary.TryGetValue(Name("VP"), out PdfObject? viewportsValue)
                || Resolve(document, viewportsValue) is not PdfArray viewports)
                continue;
            PdfContentBounds? media = Box(document,
                page.Dictionary.TryGetValue(Name("MediaBox"), out PdfObject? mediaValue)
                    ? mediaValue
                    : page.InheritedValues.GetValueOrDefault(Name("MediaBox")));
            PdfMeasurementProfileAssignment? wholePage = null;
            var regions = new List<PdfMeasurementProfileAssignment>();
            foreach (PdfObject item in viewports)
            {
                if (Resolve(document, item) is not PdfDictionary viewport
                    || !viewport.TryGetValue(Name("Measure"), out PdfObject? measure)
                    || !viewport.TryGetValue(Name("BBox"), out PdfObject? boxValue)
                    || Box(document, boxValue) is not PdfContentBounds bounds)
                    continue;
                string name = Text(document, viewport, "Name") ?? string.Empty;
                PdfMeasurementProfile? profile = ReadProfile(document, measure, name);
                if (profile is null) continue;
                if (media is PdfContentBounds mediaBox && Covers(bounds, mediaBox))
                    wholePage = new PdfMeasurementProfileAssignment(profile, page.Index);
                else
                    regions.Add(new PdfMeasurementProfileAssignment(profile, page.Index, bounds));
            }
            if (wholePage is not null) assignments.Add(wholePage);
            // Later viewports take precedence where regions overlap, so keep the array order
            // reversed for a first-match lookup.
            regions.Reverse();
            assignments.AddRange(regions);
        }
        return Array.AsReadOnly(assignments.ToArray());
    }

    private static PdfMeasurementAnnotationInfo? ReadAnnotation(
        PdfDocument document, PdfDictionary annotation, int pageIndex, int annotationIndex)
    {
        string? subtype = NameValue(document, annotation, "Subtype");
        string? intent = NameValue(document, annotation, "IT");
        annotation.TryGetValue(Name("Measure"), out PdfObject? measure);
        string scaleName = string.Empty;
        PdfMeasurementProfile? profile = measure is null ? null
            : ReadProfile(document, measure, scaleName);
        PdfMeasurementKind kind;
        List<PdfMeasurementPoint> points;
        bool closed = false;
        switch (subtype)
        {
            case "Line" when intent == "LineDimension" || measure is not null:
                points = Numbers(document, annotation, "L") is { Length: 4 } line
                    ? [new(line[0], line[1]), new(line[2], line[3])] : [];
                if (points.Count != 2 || points[0] == points[1]) return null;
                kind = PdfMeasurementKind.Distance;
                break;
            case "PolyLine" when intent == "PolyLineDimension" || measure is not null:
                points = Vertices(document, annotation);
                if (points.Count < 2) return null;
                if (measure is null && points.Count == 3)
                {
                    if (points[0] == points[1] || points[2] == points[1]) return null;
                    kind = PdfMeasurementKind.Angle;
                    break;
                }
                if (points.Count > 3 && points[0] == points[^1])
                {
                    points.RemoveAt(points.Count - 1);
                    closed = true;
                }
                kind = PdfMeasurementKind.Perimeter;
                break;
            case "Polygon" when intent == "PolygonDimension" || measure is not null:
                points = Vertices(document, annotation);
                if (points.Count > 3 && points[0] == points[^1]) points.RemoveAt(points.Count - 1);
                if (points.Count < 3) return null;
                kind = PdfMeasurementKind.Area;
                closed = true;
                break;
            default:
                return null;
        }
        (PdfMeasurementPoint? captionCenter, double? captionSize) =
            Caption(document, annotation, kind, points);
        return new PdfMeasurementAnnotationInfo
        {
            PageIndex = pageIndex,
            AnnotationIndex = annotationIndex,
            Kind = kind,
            Points = Array.AsReadOnly(points.ToArray()),
            Closed = closed,
            Profile = profile,
            Contents = Text(document, annotation, "Contents"),
            Color = Color(document, annotation, "C"),
            FillColor = kind == PdfMeasurementKind.Area ? Color(document, annotation, "IC") : null,
            LineWidth = LineWidth(document, annotation),
            Opacity = annotation.TryGetValue(Name("CA"), out PdfObject? opacityValue)
                && TryNumber(document, opacityValue, out double opacity)
                ? Math.Clamp(opacity, 0, 1) : 1,
            CaptionCenter = captionCenter,
            CaptionFontSize = captionSize
        };
    }

    // KillerPDF's own [x y size] wins; a line otherwise places a /Cap caption from the standard
    // /CO offset, along the line from its midpoint and then across it.
    private static (PdfMeasurementPoint?, double?) Caption(PdfDocument document,
        PdfDictionary annotation, PdfMeasurementKind kind, List<PdfMeasurementPoint> points)
    {
        if (Numbers(document, annotation, CaptionKey) is { Length: 3 } own && own[2] > 0)
            return (new PdfMeasurementPoint(own[0], own[1]), own[2]);
        if (kind != PdfMeasurementKind.Distance
            || !annotation.TryGetValue(Name("Cap"), out PdfObject? capValue)
            || Resolve(document, capValue) is not PdfBoolean { Value: true })
            return (null, null);
        double[] offset = Numbers(document, annotation, "CO") is { Length: 2 } co ? co : [0, 0];
        double leader = annotation.TryGetValue(Name("LL"), out PdfObject? leaderValue)
            && TryNumber(document, leaderValue, out double length) ? length : 0;
        double dx = points[1].X - points[0].X, dy = points[1].Y - points[0].Y;
        double size = Math.Sqrt(dx * dx + dy * dy);
        double ux = dx / size, uy = dy / size;
        double baseX = (points[0].X + points[1].X) / 2 - uy * leader;
        double baseY = (points[0].Y + points[1].Y) / 2 + ux * leader;
        return (new PdfMeasurementPoint(
            baseX + offset[0] * ux - offset[1] * uy,
            baseY + offset[0] * uy + offset[1] * ux), null);
    }

    /// <summary>
    /// Reads a rectilinear /Measure dictionary into a profile. The first /X format converts
    /// points to the drawing unit and the first /D format converts that unit to the distance
    /// unit, so the profile scale is their product. A feet format followed by an inches format
    /// reads as the "ft-in" unit.
    /// </summary>
    internal static PdfMeasurementProfile? ReadProfile(
        PdfDocument document, PdfObject measureValue, string name)
    {
        if (Resolve(document, measureValue) is not PdfDictionary measure
            || NameValue(document, measure, "Subtype") is string subtype && subtype != "RL")
            return null;
        List<PdfDictionary> scale = Formats(document, measure, "X");
        if (scale.Count == 0) return null;
        List<PdfDictionary> distance = Formats(document, measure, "D");
        double conversion = Conversion(document, scale[0]) ?? double.NaN;
        double distanceConversion = distance.Count > 0
            ? Conversion(document, distance[0]) ?? double.NaN : 1;
        double unitsPerPoint = conversion * distanceConversion;
        if (!double.IsFinite(unitsPerPoint) || unitsPerPoint <= 0) return null;
        List<PdfDictionary> shown = distance.Count > 0 ? distance : scale;
        string? first = Text(document, shown[0], "U")?.Trim();
        string? second = shown.Count > 1 ? Text(document, shown[1], "U")?.Trim() : null;
        bool feetAndInches = first == "ft" && second == "in";
        string unit = feetAndInches ? "ft-in" : string.IsNullOrWhiteSpace(first) ? "pt" : first;
        int precision = Precision(document, feetAndInches ? shown[1] : shown[0]);
        string ratio = Text(document, measure, "R")?.Trim() ?? string.Empty;
        string profileName = ratio.Length > 0 ? ratio
            : name.Trim().Length > 0 ? name.Trim()
            : string.Create(CultureInfo.InvariantCulture,
                $"1 pt = {unitsPerPoint:G6} {unit}");
        return new PdfMeasurementProfile(profileName, unitsPerPoint, unit, precision);
    }

    private static List<PdfDictionary> Formats(PdfDocument document, PdfDictionary measure, string key)
    {
        var formats = new List<PdfDictionary>();
        if (measure.TryGetValue(Name(key), out PdfObject? value)
            && Resolve(document, value) is PdfArray array)
            foreach (PdfObject item in array)
                if (Resolve(document, item) is PdfDictionary format) formats.Add(format);
        return formats;
    }

    private static double? Conversion(PdfDocument document, PdfDictionary format) =>
        format.TryGetValue(Name("C"), out PdfObject? value) && TryNumber(document, value, out double number)
            ? number : null;

    // /F /D with a power-of-ten /D denominator gives decimal places; rounding, truncating, and
    // fractions show whole numbers or fractions, so they read as no decimal places.
    private static int Precision(PdfDocument document, PdfDictionary format)
    {
        string style = NameValue(document, format, "F") ?? "D";
        if (style != "D") return 0;
        if (!format.TryGetValue(Name("D"), out PdfObject? value)
            || !TryNumber(document, value, out double denominator) || denominator < 1)
            return 2;
        return Math.Clamp((int)Math.Round(Math.Log10(denominator)), 0, 12);
    }

    private static List<PdfMeasurementPoint> Vertices(PdfDocument document, PdfDictionary annotation)
    {
        var points = new List<PdfMeasurementPoint>();
        if (Numbers(document, annotation, "Vertices") is not { } values) return points;
        for (int index = 0; index + 1 < values.Length; index += 2)
            points.Add(new PdfMeasurementPoint(values[index], values[index + 1]));
        return points;
    }

    private static double LineWidth(PdfDocument document, PdfDictionary annotation)
    {
        if (annotation.TryGetValue(Name("BS"), out PdfObject? borderValue)
            && Resolve(document, borderValue) is PdfDictionary border
            && border.TryGetValue(Name("W"), out PdfObject? widthValue)
            && TryNumber(document, widthValue, out double width) && width >= 0)
            return width;
        if (Numbers(document, annotation, "Border") is { Length: >= 3 } legacy && legacy[2] >= 0)
            return legacy[2];
        return 1;
    }

    private static bool Covers(PdfContentBounds bounds, PdfContentBounds page) =>
        bounds.Left <= page.Left + 1 && bounds.Bottom <= page.Bottom + 1
        && bounds.Right >= page.Right - 1 && bounds.Top >= page.Top - 1;

    private static PdfContentBounds? Box(PdfDocument document, PdfObject? value)
    {
        if (value is null || Resolve(document, value) is not PdfArray array || array.Count != 4)
            return null;
        double[] numbers = new double[4];
        for (int index = 0; index < 4; index++)
            if (!TryNumber(document, array[index], out numbers[index])) return null;
        var bounds = new PdfContentBounds(Math.Min(numbers[0], numbers[2]),
            Math.Min(numbers[1], numbers[3]), Math.Max(numbers[0], numbers[2]),
            Math.Max(numbers[1], numbers[3]));
        return bounds.Width > 0 && bounds.Height > 0 ? bounds : null;
    }

    private static double[]? Numbers(PdfDocument document, PdfDictionary dictionary, string key)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value)
            || Resolve(document, value) is not PdfArray array)
            return null;
        double[] numbers = new double[array.Count];
        for (int index = 0; index < array.Count; index++)
            if (!TryNumber(document, array[index], out numbers[index])) return null;
        return numbers;
    }

    private static PdfRgbColor? Color(PdfDocument document, PdfDictionary annotation, string key)
    {
        if (Numbers(document, annotation, key) is not { } numbers
            || numbers.Any(value => value is < 0 or > 1))
            return null;
        return numbers.Length switch
        {
            1 => new PdfRgbColor(numbers[0], numbers[0], numbers[0]),
            3 => new PdfRgbColor(numbers[0], numbers[1], numbers[2]),
            4 => new PdfRgbColor((1 - numbers[0]) * (1 - numbers[3]),
                (1 - numbers[1]) * (1 - numbers[3]), (1 - numbers[2]) * (1 - numbers[3])),
            _ => null
        };
    }

    private static string? NameValue(PdfDocument document, PdfDictionary dictionary, string key) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value)
            && Resolve(document, value) is PdfName name ? name.ValueAsLatin1() : null;

    private static string? Text(PdfDocument document, PdfDictionary dictionary, string key) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value)
            && Resolve(document, value) is PdfString text
            ? PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, $"A measurement /{key} value")
            : null;

    private static bool TryNumber(PdfDocument document, PdfObject value, out double number)
    {
        switch (Resolve(document, value))
        {
            case PdfInteger integer:
                number = integer.Value;
                return true;
            case PdfReal real when double.IsFinite(real.Value):
                number = real.Value;
                return true;
            default:
                number = 0;
                return false;
        }
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("A measurement reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static void RequireDecrypted(PdfDocument document, string what)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsDecrypted)
            throw new InvalidOperationException($"Authenticate the document before reading {what}.");
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
