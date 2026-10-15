using System.Globalization;
using System.Text;

namespace KillerPDF.Services;

internal enum VectorVerb { Move, Line, Cubic, Close }

/// <summary>One step of a vector outline. A cubic uses both control points; a move or a line
/// uses only the end point.</summary>
internal readonly record struct VectorSegment(
    VectorVerb Verb, double X1, double Y1, double X2, double Y2, double X, double Y)
{
    internal static VectorSegment MoveTo(double x, double y) => new(VectorVerb.Move, 0, 0, 0, 0, x, y);
    internal static VectorSegment LineTo(double x, double y) => new(VectorVerb.Line, 0, 0, 0, 0, x, y);
    internal static VectorSegment CubicTo(double x1, double y1, double x2, double y2, double x, double y) =>
        new(VectorVerb.Cubic, x1, y1, x2, y2, x, y);
    internal static readonly VectorSegment ClosePath = new(VectorVerb.Close, 0, 0, 0, 0, 0, 0);
}

/// <summary>An SVG-style affine transform: x' = A x + C y + E, y' = B x + D y + F.</summary>
internal readonly record struct VectorTransform(double A, double B, double C, double D, double E, double F)
{
    internal static readonly VectorTransform Identity = new(1, 0, 0, 1, 0, 0);

    internal static VectorTransform Translate(double x, double y) => new(1, 0, 0, 1, x, y);

    internal static VectorTransform Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    /// <summary>This transform applied after <paramref name="inner"/>.</summary>
    internal VectorTransform After(VectorTransform inner) => new(
        A * inner.A + C * inner.B,
        B * inner.A + D * inner.B,
        A * inner.C + C * inner.D,
        B * inner.C + D * inner.D,
        A * inner.E + C * inner.F + E,
        B * inner.E + D * inner.F + F);

    internal (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    /// <summary>How much the transform grows a length on average, for stroke widths.</summary>
    internal double LengthScale => Math.Sqrt(Math.Abs(A * D - B * C));
}

/// <summary>
/// Reads SVG path data (#398) into moves, lines, cubics and closes, so a vector signature can be
/// drawn on screen and written to the PDF with the same outline. Arcs and quadratics become
/// cubics; relative and shorthand commands become absolute ones.
/// </summary>
internal static class SvgPathData
{
    internal static List<VectorSegment> Parse(string data)
    {
        var segments = new List<VectorSegment>();
        var reader = new Reader(data);
        char command = '\0';
        double x = 0, y = 0, startX = 0, startY = 0;
        double lastControlX = 0, lastControlY = 0;
        char previous = '\0';
        bool open = false;

        while (true)
        {
            reader.SkipSeparators();
            if (reader.AtEnd) break;
            if (reader.PeekCommand() is char next)
            {
                command = next;
                reader.Advance();
            }
            else if (command == '\0')
                throw new FormatException("Path data must start with a move command.");
            else if (command is 'Z' or 'z')
                throw new FormatException("Path data has a number after a close command.");

            bool relative = char.IsLower(command);
            double ox = relative ? x : 0, oy = relative ? y : 0;
            char upper = char.ToUpperInvariant(command);
            if (upper != 'M' && upper != 'Z' && !open)
            {
                segments.Add(VectorSegment.MoveTo(x, y));
                startX = x; startY = y;
                open = true;
            }
            switch (upper)
            {
                case 'M':
                    x = ox + reader.Number(); y = oy + reader.Number();
                    segments.Add(VectorSegment.MoveTo(x, y));
                    startX = x; startY = y;
                    open = true;
                    command = relative ? 'l' : 'L';   // further pairs are lines
                    break;
                case 'L':
                    x = ox + reader.Number(); y = oy + reader.Number();
                    segments.Add(VectorSegment.LineTo(x, y));
                    break;
                case 'H':
                    x = ox + reader.Number();
                    segments.Add(VectorSegment.LineTo(x, y));
                    break;
                case 'V':
                    y = oy + reader.Number();
                    segments.Add(VectorSegment.LineTo(x, y));
                    break;
                case 'C':
                {
                    double x1 = ox + reader.Number(), y1 = oy + reader.Number();
                    double x2 = ox + reader.Number(), y2 = oy + reader.Number();
                    x = ox + reader.Number(); y = oy + reader.Number();
                    segments.Add(VectorSegment.CubicTo(x1, y1, x2, y2, x, y));
                    lastControlX = x2; lastControlY = y2;
                    break;
                }
                case 'S':
                {
                    (double x1, double y1) = previous is 'C' or 'S'
                        ? (2 * x - lastControlX, 2 * y - lastControlY) : (x, y);
                    double x2 = ox + reader.Number(), y2 = oy + reader.Number();
                    x = ox + reader.Number(); y = oy + reader.Number();
                    segments.Add(VectorSegment.CubicTo(x1, y1, x2, y2, x, y));
                    lastControlX = x2; lastControlY = y2;
                    break;
                }
                case 'Q':
                {
                    double qx = ox + reader.Number(), qy = oy + reader.Number();
                    double ex = ox + reader.Number(), ey = oy + reader.Number();
                    AddQuadratic(segments, x, y, qx, qy, ex, ey);
                    x = ex; y = ey;
                    lastControlX = qx; lastControlY = qy;
                    break;
                }
                case 'T':
                {
                    (double qx, double qy) = previous is 'Q' or 'T'
                        ? (2 * x - lastControlX, 2 * y - lastControlY) : (x, y);
                    double ex = ox + reader.Number(), ey = oy + reader.Number();
                    AddQuadratic(segments, x, y, qx, qy, ex, ey);
                    x = ex; y = ey;
                    lastControlX = qx; lastControlY = qy;
                    break;
                }
                case 'A':
                {
                    double rx = Math.Abs(reader.Number()), ry = Math.Abs(reader.Number());
                    double angle = reader.Number();
                    bool large = reader.Flag(), sweep = reader.Flag();
                    double ex = ox + reader.Number(), ey = oy + reader.Number();
                    AddArc(segments, x, y, rx, ry, angle, large, sweep, ex, ey);
                    x = ex; y = ey;
                    break;
                }
                case 'Z':
                    if (open) segments.Add(VectorSegment.ClosePath);
                    x = startX; y = startY;
                    open = false;
                    break;
                default:
                    throw new FormatException($"Path data uses an unknown command '{command}'.");
            }
            previous = upper;
        }
        return segments;
    }

    internal static List<VectorSegment> Transform(IEnumerable<VectorSegment> segments, VectorTransform matrix)
    {
        var result = new List<VectorSegment>();
        foreach (VectorSegment segment in segments)
        {
            (double x, double y) = matrix.Apply(segment.X, segment.Y);
            switch (segment.Verb)
            {
                case VectorVerb.Move: result.Add(VectorSegment.MoveTo(x, y)); break;
                case VectorVerb.Line: result.Add(VectorSegment.LineTo(x, y)); break;
                case VectorVerb.Cubic:
                    (double x1, double y1) = matrix.Apply(segment.X1, segment.Y1);
                    (double x2, double y2) = matrix.Apply(segment.X2, segment.Y2);
                    result.Add(VectorSegment.CubicTo(x1, y1, x2, y2, x, y));
                    break;
                default: result.Add(segment); break;
            }
        }
        return result;
    }

    /// <summary>Absolute path data with only M, L, C and Z, which <see cref="Parse"/> reads back.</summary>
    internal static string Format(IEnumerable<VectorSegment> segments)
    {
        var text = new StringBuilder();
        void Add(char verb, params double[] values)
        {
            if (text.Length > 0) text.Append(' ');
            text.Append(verb);
            foreach (double value in values)
            {
                double rounded = Math.Round(value, 3);
                if (rounded == 0) rounded = 0;   // no "-0"
                text.Append(' ').Append(rounded.ToString("0.###", CultureInfo.InvariantCulture));
            }
        }
        foreach (VectorSegment segment in segments)
            switch (segment.Verb)
            {
                case VectorVerb.Move: Add('M', segment.X, segment.Y); break;
                case VectorVerb.Line: Add('L', segment.X, segment.Y); break;
                case VectorVerb.Cubic:
                    Add('C', segment.X1, segment.Y1, segment.X2, segment.Y2, segment.X, segment.Y);
                    break;
                default: Add('Z'); break;
            }
        return text.ToString();
    }

    private static void AddQuadratic(List<VectorSegment> segments,
        double x0, double y0, double qx, double qy, double x, double y) =>
        segments.Add(VectorSegment.CubicTo(
            x0 + 2.0 / 3 * (qx - x0), y0 + 2.0 / 3 * (qy - y0),
            x + 2.0 / 3 * (qx - x), y + 2.0 / 3 * (qy - y), x, y));

    // The SVG arc, from its end points to its center, then drawn as cubics of at most a quarter
    // turn each (SVG 1.1 implementation notes, F.6.5).
    private static void AddArc(List<VectorSegment> segments, double x0, double y0, double rx, double ry,
        double angleDegrees, bool large, bool sweep, double x, double y)
    {
        if (x0 == x && y0 == y) return;
        if (rx == 0 || ry == 0)
        {
            segments.Add(VectorSegment.LineTo(x, y));
            return;
        }
        double phi = angleDegrees * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (x0 - x) / 2, dy = (y0 - y) / 2;
        double x1p = cos * dx + sin * dy, y1p = -sin * dx + cos * dy;
        double lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
        if (lambda > 1)
        {
            double grow = Math.Sqrt(lambda);
            rx *= grow; ry *= grow;
        }
        double numerator = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
        double denominator = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
        double factor = denominator == 0 ? 0 : Math.Sqrt(Math.Max(0, numerator / denominator));
        if (large == sweep) factor = -factor;
        double cxp = factor * rx * y1p / ry, cyp = -factor * ry * x1p / rx;
        double cx = cos * cxp - sin * cyp + (x0 + x) / 2, cy = sin * cxp + cos * cyp + (y0 + y) / 2;

        static double Angle(double ux, double uy, double vx, double vy) =>
            Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
        double theta = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        double delta = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
        if (!sweep && delta > 0) delta -= 2 * Math.PI;
        else if (sweep && delta < 0) delta += 2 * Math.PI;

        int pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-9));
        double step = delta / pieces;
        double kappa = 4.0 / 3 * Math.Tan(step / 4);
        (double X, double Y) Point(double t) =>
            (cx + rx * Math.Cos(t) * cos - ry * Math.Sin(t) * sin, cy + rx * Math.Cos(t) * sin + ry * Math.Sin(t) * cos);
        (double X, double Y) Tangent(double t) =>
            (-rx * Math.Sin(t) * cos - ry * Math.Cos(t) * sin, -rx * Math.Sin(t) * sin + ry * Math.Cos(t) * cos);
        for (int i = 0; i < pieces; i++)
        {
            double t1 = theta + i * step, t2 = t1 + step;
            (double px1, double py1) = Point(t1);
            (double px2, double py2) = i == pieces - 1 ? (x, y) : Point(t2);
            (double tx1, double ty1) = Tangent(t1);
            (double tx2, double ty2) = Tangent(t2);
            segments.Add(VectorSegment.CubicTo(
                px1 + kappa * tx1, py1 + kappa * ty1, px2 - kappa * tx2, py2 - kappa * ty2, px2, py2));
        }
    }

    private sealed class Reader(string text)
    {
        private int _index;

        internal bool AtEnd => _index >= text.Length;

        internal void Advance() => _index++;

        internal void SkipSeparators()
        {
            while (_index < text.Length && (char.IsWhiteSpace(text[_index]) || text[_index] == ',')) _index++;
        }

        internal char? PeekCommand()
        {
            if (AtEnd) return null;
            char c = text[_index];
            return c is 'M' or 'm' or 'L' or 'l' or 'H' or 'h' or 'V' or 'v' or 'C' or 'c' or 'S' or 's'
                or 'Q' or 'q' or 'T' or 't' or 'A' or 'a' or 'Z' or 'z' ? c : null;
        }

        internal double Number()
        {
            SkipSeparators();
            int start = _index;
            if (_index < text.Length && text[_index] is '+' or '-') _index++;
            bool digits = false, dot = false;
            while (_index < text.Length)
            {
                char c = text[_index];
                if (char.IsAsciiDigit(c)) digits = true;
                else if (c == '.' && !dot) dot = true;
                else break;
                _index++;
            }
            if (digits && _index < text.Length && text[_index] is 'e' or 'E')
            {
                int mark = _index++;
                if (_index < text.Length && text[_index] is '+' or '-') _index++;
                if (_index < text.Length && char.IsAsciiDigit(text[_index]))
                    while (_index < text.Length && char.IsAsciiDigit(text[_index])) _index++;
                else _index = mark;
            }
            if (!digits)
                throw new FormatException("Path data is missing a number.");
            if (!double.TryParse(text.AsSpan(start, _index - start), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
                throw new FormatException("Path data contains an invalid number.");
            return value;
        }

        // Arc flags are a single 0 or 1 and may run straight into the next number ("a5 5 0 0110 10").
        internal bool Flag()
        {
            SkipSeparators();
            if (_index < text.Length && text[_index] is '0' or '1') return text[_index++] == '1';
            throw new FormatException("Path data has an invalid arc flag.");
        }
    }
}
