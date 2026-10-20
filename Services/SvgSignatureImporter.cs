using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace KillerPDF.Services;

/// <summary>A passive SVG signature: its size, the raw path data it carries and every visible
/// outline, already transformed into the SVG's own units with the view box corner at 0,0.</summary>
internal sealed record SvgSignatureDocument(
    string Source, double Width, double Height, IReadOnlyList<string> Paths,
    IReadOnlyList<SignatureVector> Shapes)
{
    /// <summary>The outlines scaled to fit a signature canvas of at most
    /// <paramref name="maxWidth"/> by <paramref name="maxHeight"/>, and the canvas size used.</summary>
    internal (List<SignatureVector> Vectors, double Width, double Height) Fit(double maxWidth, double maxHeight)
    {
        double scale = Math.Min(maxWidth / Width, maxHeight / Height);
        var matrix = VectorTransform.Scale(scale, scale);
        List<SignatureVector> vectors = [.. Shapes.Select(shape => new SignatureVector
        {
            Data = SvgPathData.Format(SvgPathData.Transform(SvgPathData.Parse(shape.Data), matrix)),
            Fill = shape.Fill,
            Stroke = shape.Stroke,
            StrokeWidth = shape.StrokeWidth * scale,
            EvenOdd = shape.EvenOdd,
            LineCap = shape.LineCap,
            LineJoin = shape.LineJoin
        })];
        return (vectors, Width * scale, Height * scale);
    }
}

/// <summary>Reads passive SVG signatures without resolving external content (#398). Paths and
/// the basic shapes are kept as vector outlines with their fill and stroke, so the signature
/// stays sharp at any size on screen and in the saved PDF.</summary>
internal static class SvgSignatureImporter
{
    // A signature is a few hundred outlines at most. The cap keeps a hostile file from tying up
    // the window while it is read.
    private const int MaxSegments = 500_000;

    internal static SvgSignatureDocument Import(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string source = File.ReadAllText(path);
        using var reader = XmlReader.Create(new StringReader(source), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        XDocument document = XDocument.Load(reader, LoadOptions.None);
        XElement root = document.Root
            ?? throw new InvalidDataException("The SVG document is empty.");
        if (root.Name.LocalName != "svg")
            throw new InvalidDataException("The signature file does not contain an SVG root.");
        if (root.Descendants().Any(element => element.Name.LocalName is
                "script" or "image" or "use" or "foreignObject")
            || root.DescendantsAndSelf().Attributes().Any(attribute =>
                attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                || attribute.Name.LocalName is "href"))
            throw new InvalidDataException("SVG signatures cannot contain active or external content.");

        // Containers nest a few levels deep in real signatures. Deeper trees are refused up
        // front, before the recursive walk, since a stack overflow cannot be caught.
        if (NestsDeeperThan(root, MaxDepth))
            throw new InvalidDataException("The SVG signature is nested too deeply to import.");

        (double minX, double minY, double width, double height) = ViewBox(root);
        string[] paths = [.. root.Descendants()
            .Where(element => element.Name.LocalName == "path")
            .Select(element => (string?)element.Attribute("d"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)];

        var shapes = new List<SignatureVector>();
        int segmentCount = 0;
        Style rootStyle = Style.Root(VectorTransform.Translate(-minX, -minY)).Inherit(root);
        foreach (XElement child in root.Elements())
            Collect(child, rootStyle, (width, height), shapes, ref segmentCount);
        if (shapes.Count == 0)
            throw new InvalidDataException("The SVG signature does not contain any visible paths or shapes.");
        return new SvgSignatureDocument(source, width, height, Array.AsReadOnly(paths), shapes.AsReadOnly());
    }

    // Containers and hidden parts that never paint on their own.
    private static bool IsSkipped(string name) => name is "defs" or "clipPath" or "mask" or "symbol"
        or "marker" or "pattern" or "linearGradient" or "radialGradient" or "filter" or "style"
        or "title" or "desc" or "metadata" or "text";

    private const int MaxDepth = 64;

    // Walks the tree with its own stack, so even a hostile file cannot exhaust the thread's.
    private static bool NestsDeeperThan(XElement root, int limit)
    {
        var pending = new Stack<(XElement Element, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            (XElement element, int depth) = pending.Pop();
            if (depth > limit) return true;
            foreach (XElement child in element.Elements()) pending.Push((child, depth + 1));
        }
        return false;
    }

    private static void Collect(XElement element, Style parent, (double Width, double Height) canvas,
        List<SignatureVector> shapes, ref int segmentCount)
    {
        string name = element.Name.LocalName;
        if (IsSkipped(name)) return;
        Style style = parent.Inherit(element);
        if (style.Hidden) return;
        if (name is "g" or "svg" or "a" or "switch")
        {
            foreach (XElement child in element.Elements()) Collect(child, style, canvas, shapes, ref segmentCount);
            return;
        }
        if (Outline(element, name) is not { Count: > 0 } outline) return;
        segmentCount += outline.Count;
        if (segmentCount > MaxSegments)
            throw new InvalidDataException("The SVG signature is too detailed to import.");
        uint? fill = name == "line" ? null : style.Fill;
        uint? stroke = style.StrokeWidth > 0 ? style.Stroke : null;
        if (fill is null && stroke is null) return;
        if (stroke is null && IsPaper(name, fill, outline, style.Matrix, canvas)) return;
        shapes.Add(new SignatureVector
        {
            Data = SvgPathData.Format(SvgPathData.Transform(outline, style.Matrix)),
            Fill = fill,
            Stroke = stroke,
            StrokeWidth = style.StrokeWidth * style.Matrix.LengthScale,
            EvenOdd = style.EvenOdd,
            LineCap = style.LineCap,
            LineJoin = style.LineJoin
        });
    }

    // A white rectangle behind the whole signature is its paper. Like the paper in a scanned
    // signature it is left out, so the signature does not cover the form line it is placed on.
    private static bool IsPaper(string name, uint? fill, List<VectorSegment> outline,
        VectorTransform matrix, (double Width, double Height) canvas)
    {
        if (name != "rect" || fill is not uint argb || (argb & 0x00FFFFFF) != 0x00FFFFFF) return false;
        var corners = SvgPathData.Transform(outline, matrix).Where(s => s.Verb != VectorVerb.Close).ToList();
        double left = corners.Min(s => s.X), right = corners.Max(s => s.X);
        double top = corners.Min(s => s.Y), bottom = corners.Max(s => s.Y);
        return (right - left) * (bottom - top) >= canvas.Width * canvas.Height * 0.9;
    }

    private static List<VectorSegment>? Outline(XElement element, string name)
    {
        double Attr(string attribute) => ParseLength((string?)element.Attribute(attribute));
        try
        {
            switch (name)
            {
                case "path":
                    string? data = (string?)element.Attribute("d");
                    return string.IsNullOrWhiteSpace(data) ? null : SvgPathData.Parse(data);
                case "rect":
                {
                    double x = Attr("x"), y = Attr("y"), w = Attr("width"), h = Attr("height");
                    if (w <= 0 || h <= 0) return null;
                    return
                    [
                        VectorSegment.MoveTo(x, y), VectorSegment.LineTo(x + w, y),
                        VectorSegment.LineTo(x + w, y + h), VectorSegment.LineTo(x, y + h), VectorSegment.ClosePath
                    ];
                }
                case "circle":
                {
                    double r = Attr("r");
                    return r > 0 ? Ellipse(Attr("cx"), Attr("cy"), r, r) : null;
                }
                case "ellipse":
                {
                    double rx = Attr("rx"), ry = Attr("ry");
                    return rx > 0 && ry > 0 ? Ellipse(Attr("cx"), Attr("cy"), rx, ry) : null;
                }
                case "line":
                    return [VectorSegment.MoveTo(Attr("x1"), Attr("y1")), VectorSegment.LineTo(Attr("x2"), Attr("y2"))];
                case "polyline":
                case "polygon":
                {
                    double[] values = [.. ((string?)element.Attribute("points") ?? "")
                        .Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                        .Select(ParseNumber)];
                    if (values.Length < 4) return null;
                    var outline = new List<VectorSegment> { VectorSegment.MoveTo(values[0], values[1]) };
                    for (int i = 2; i + 1 < values.Length; i += 2)
                        outline.Add(VectorSegment.LineTo(values[i], values[i + 1]));
                    if (name == "polygon") outline.Add(VectorSegment.ClosePath);
                    return outline;
                }
                default:
                    return null;
            }
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("The SVG signature contains invalid shape data. " + error.Message, error);
        }
    }

    private static List<VectorSegment> Ellipse(double cx, double cy, double rx, double ry)
    {
        const double k = 0.5522847498307936;   // a quarter circle as one cubic
        double kx = rx * k, ky = ry * k;
        return
        [
            VectorSegment.MoveTo(cx + rx, cy),
            VectorSegment.CubicTo(cx + rx, cy + ky, cx + kx, cy + ry, cx, cy + ry),
            VectorSegment.CubicTo(cx - kx, cy + ry, cx - rx, cy + ky, cx - rx, cy),
            VectorSegment.CubicTo(cx - rx, cy - ky, cx - kx, cy - ry, cx, cy - ry),
            VectorSegment.CubicTo(cx + kx, cy - ry, cx + rx, cy - ky, cx + rx, cy),
            VectorSegment.ClosePath
        ];
    }

    private static (double MinX, double MinY, double Width, double Height) ViewBox(XElement root)
    {
        string? viewBox = (string?)root.Attribute("viewBox");
        if (!string.IsNullOrWhiteSpace(viewBox))
        {
            double[] values = [.. viewBox.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Select(ParseNumber)];
            if (values.Length == 4 && values[2] > 0 && values[3] > 0)
                return (values[0], values[1], values[2], values[3]);
        }
        double width = ParseLength((string?)root.Attribute("width"));
        double height = ParseLength((string?)root.Attribute("height"));
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("The SVG signature requires a positive viewBox or size.");
        return (0, 0, width, height);
    }

    private static double ParseLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        value = value.Trim();
        int end = 0;
        while (end < value.Length && (char.IsDigit(value[end])
            || value[end] is '.' or '+' or '-' or 'e' or 'E')) end++;
        return end == 0 ? 0 : ParseNumber(value[..end]);
    }

    private static double ParseNumber(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                out double result) || !double.IsFinite(result))
            throw new InvalidDataException("The SVG signature contains an invalid dimension.");
        return result;
    }

    // The presentation attributes a signature uses, inherited down the tree as SVG does. Fill
    // defaults to black and stroke to none. Gradients and other paint servers fall back to black.
    private sealed record Style(
        VectorTransform Matrix, uint? FillPaint, uint? StrokePaint, double StrokeWidth, bool EvenOdd,
        int LineCap, int LineJoin, double FillOpacity, double StrokeOpacity, double Opacity,
        uint CurrentColor, bool Hidden)
    {
        internal static Style Root(VectorTransform matrix) =>
            new(matrix, Black, null, 1, false, 0, 0, 1, 1, 1, Black, false);

        internal uint? Fill => WithAlpha(FillPaint, FillOpacity * Opacity);

        internal uint? Stroke => WithAlpha(StrokePaint, StrokeOpacity * Opacity);

        internal Style Inherit(XElement element)
        {
            Dictionary<string, string> values = Properties(element);
            string? Get(string key) =>
                values.TryGetValue(key, out string? value) && value != "inherit" ? value : null;

            // The outer svg element's place is already set by its view box.
            VectorTransform matrix = element.Parent is null
                ? Matrix
                : Matrix.After(ParseTransform((string?)element.Attribute("transform")));
            uint currentColor = Get("color") is string color && ParseColor(color) is uint parsed
                ? parsed : CurrentColor;
            return new Style(
                matrix,
                Get("fill") is string fill ? Paint(fill, currentColor) : FillPaint,
                Get("stroke") is string stroke ? Paint(stroke, currentColor) : StrokePaint,
                Get("stroke-width") is string width ? Math.Max(0, SafeLength(width, StrokeWidth)) : StrokeWidth,
                Get("fill-rule") is string rule ? rule == "evenodd" : EvenOdd,
                Get("stroke-linecap") switch { "round" => 1, "square" => 2, "butt" => 0, _ => LineCap },
                Get("stroke-linejoin") switch { "round" => 1, "bevel" => 2, "miter" => 0, _ => LineJoin },
                Fraction(Get("fill-opacity"), FillOpacity),
                Fraction(Get("stroke-opacity"), StrokeOpacity),
                Opacity * Fraction(Get("opacity"), 1),   // group opacity multiplies down the tree
                currentColor,
                Hidden || Get("display") == "none" || Get("visibility") is "hidden" or "collapse");
        }
    }

    private const uint Black = 0xFF000000;

    // Presentation attributes, then the style attribute's declarations, which win.
    private static Dictionary<string, string> Properties(XElement element)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (XAttribute attribute in element.Attributes())
            if (attribute.Name.NamespaceName.Length == 0)
                values[attribute.Name.LocalName] = attribute.Value.Trim();
        if ((string?)element.Attribute("style") is string style)
            foreach (string declaration in style.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int colon = declaration.IndexOf(':');
                if (colon <= 0) continue;
                string value = declaration[(colon + 1)..].Replace("!important", "", StringComparison.OrdinalIgnoreCase).Trim();
                values[declaration[..colon].Trim()] = value;
            }
        return values;
    }

    private static uint? WithAlpha(uint? color, double opacity)
    {
        if (color is not uint argb) return null;
        double alpha = (argb >> 24) / 255.0 * Math.Clamp(opacity, 0, 1);
        if (alpha <= 0) return null;
        return ((uint)Math.Round(alpha * 255) << 24) | (argb & 0x00FFFFFF);
    }

    private static double Fraction(string? value, double fallback)
    {
        if (value is null) return fallback;
        bool percent = value.EndsWith('%');
        if (!double.TryParse(percent ? value[..^1] : value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
            return fallback;
        return Math.Clamp(percent ? number / 100 : number, 0, 1);
    }

    private static double SafeLength(string value, double fallback)
    {
        try { return ParseLength(value); }
        catch (InvalidDataException) { return fallback; }
    }

    private static uint? Paint(string value, uint currentColor)
    {
        value = value.Trim();
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase)) return null;
        if (value.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            // A gradient or pattern: use its fallback color when it names one.
            int close = value.IndexOf(')');
            string fallback = close < 0 ? "" : value[(close + 1)..].Trim();
            return fallback.Length > 0 ? Paint(fallback, currentColor) : Black;
        }
        if (value.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) return currentColor;
        return ParseColor(value) ?? Black;
    }

    internal static uint? ParseColor(string value)
    {
        value = value.Trim().ToLowerInvariant();
        if (value.StartsWith('#'))
        {
            string hex = value[1..];
            if (hex.Length is 3 or 4) hex = string.Concat(hex.Select(c => new string(c, 2)));
            if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out uint number)) return null;
            return hex.Length == 6 ? 0xFF000000 | number : (number >> 8) | ((number & 0xFF) << 24);
        }
        if (value.StartsWith("rgb", StringComparison.Ordinal))
        {
            int open = value.IndexOf('('), close = value.IndexOf(')');
            if (open < 0 || close < open) return null;
            string[] parts = value[(open + 1)..close].Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return null;
            uint Channel(string part)
            {
                bool percent = part.EndsWith('%');
                double number = double.TryParse(percent ? part[..^1] : part, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double parsed) ? parsed : 0;
                return (uint)Math.Clamp(Math.Round(percent ? number * 2.55 : number), 0, 255);
            }
            uint alpha = parts.Length > 3 ? (uint)Math.Round(Fraction(parts[3], 1) * 255) : 255;
            return (alpha << 24) | (Channel(parts[0]) << 16) | (Channel(parts[1]) << 8) | Channel(parts[2]);
        }
        return NamedColors.TryGetValue(value, out uint named) ? named : null;
    }

    // The colors signature files are drawn in. Anything else falls back to black.
    private static readonly Dictionary<string, uint> NamedColors = new(StringComparer.Ordinal)
    {
        ["black"] = 0xFF000000, ["white"] = 0xFFFFFFFF, ["gray"] = 0xFF808080, ["grey"] = 0xFF808080,
        ["darkgray"] = 0xFFA9A9A9, ["darkgrey"] = 0xFFA9A9A9, ["dimgray"] = 0xFF696969,
        ["dimgrey"] = 0xFF696969, ["silver"] = 0xFFC0C0C0, ["blue"] = 0xFF0000FF, ["navy"] = 0xFF000080,
        ["darkblue"] = 0xFF00008B, ["mediumblue"] = 0xFF0000CD, ["royalblue"] = 0xFF4169E1,
        ["midnightblue"] = 0xFF191970, ["steelblue"] = 0xFF4682B4, ["red"] = 0xFFFF0000,
        ["darkred"] = 0xFF8B0000, ["maroon"] = 0xFF800000, ["green"] = 0xFF008000,
        ["darkgreen"] = 0xFF006400, ["purple"] = 0xFF800080, ["indigo"] = 0xFF4B0082,
        ["brown"] = 0xFFA52A2A, ["transparent"] = 0x00000000
    };

    private static VectorTransform ParseTransform(string? text)
    {
        VectorTransform result = VectorTransform.Identity;
        if (string.IsNullOrWhiteSpace(text)) return result;
        int index = 0;
        while (true)
        {
            int open = text.IndexOf('(', index);
            if (open < 0) break;
            int close = text.IndexOf(')', open);
            if (close < 0) throw new InvalidDataException("The SVG signature contains an invalid transform.");
            string name = text[index..open].Trim(' ', ',', '\t', '\r', '\n');
            double[] v = [.. text[(open + 1)..close]
                .Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(ParseNumber)];
            double Arg(int i, double fallback) => i < v.Length ? v[i] : fallback;
            double Radians(double degrees) => degrees * Math.PI / 180;
            VectorTransform item = name switch
            {
                "matrix" when v.Length == 6 => new VectorTransform(v[0], v[1], v[2], v[3], v[4], v[5]),
                "translate" when v.Length >= 1 => VectorTransform.Translate(v[0], Arg(1, 0)),
                "scale" when v.Length >= 1 => VectorTransform.Scale(v[0], Arg(1, v[0])),
                "rotate" when v.Length >= 1 => Rotate(Radians(v[0]), Arg(1, 0), Arg(2, 0)),
                "skewX" when v.Length == 1 => new VectorTransform(1, 0, Math.Tan(Radians(v[0])), 1, 0, 0),
                "skewY" when v.Length == 1 => new VectorTransform(1, Math.Tan(Radians(v[0])), 0, 1, 0, 0),
                _ => throw new InvalidDataException("The SVG signature contains an invalid transform.")
            };
            result = result.After(item);
            index = close + 1;
        }
        return result;
    }

    private static VectorTransform Rotate(double radians, double cx, double cy)
    {
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        return VectorTransform.Translate(cx, cy)
            .After(new VectorTransform(cos, sin, -sin, cos, 0, 0))
            .After(VectorTransform.Translate(-cx, -cy));
    }
}
