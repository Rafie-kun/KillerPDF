using System.Globalization;
using System.Windows;

namespace KillerPDF.Services;

/// <summary>
/// The value, label text and automatic label spot of a measurement, shared by the page overlay,
/// the burn used for printing and exporting, and the save that writes measurement annotations.
/// Canvas units in, with the page's points per canvas unit supplied by the caller.
/// </summary>
internal static class MeasurementGeometry
{
    /// <summary>The fewest points each kind of measurement needs.</summary>
    internal static int MinimumPoints(MeasurementKind kind) => kind switch
    {
        MeasurementKind.Distance => 2,
        MeasurementKind.Perimeter => 2,
        _ => 3,
    };

    /// <summary>True when the measurement has enough distinct points to mean something.</summary>
    internal static bool IsUsable(MeasurementAnnotation measurement)
    {
        var points = measurement.Points;
        if (points.Count < MinimumPoints(measurement.Kind)) return false;
        return measurement.Kind switch
        {
            MeasurementKind.Angle => (points[0] - points[1]).Length > 0.5 && (points[2] - points[1]).Length > 0.5,
            MeasurementKind.Area => Math.Abs(SignedArea(points)) > 0.5,
            _ => PathLength(points, false) > 0.5,
        };
    }

    /// <summary>
    /// The measured value in the measurement's unit (square units for an area, degrees for an
    /// angle), with <paramref name="pointsPerUnit"/> page points per canvas unit.
    /// </summary>
    internal static double Value(MeasurementAnnotation measurement, double pointsPerUnit)
    {
        var points = measurement.Points;
        double scale = pointsPerUnit * measurement.UnitsPerPoint;
        return measurement.Kind switch
        {
            MeasurementKind.Distance => points.Count >= 2 ? (points[1] - points[0]).Length * scale : 0,
            MeasurementKind.Perimeter => PathLength(points, measurement.Closed) * scale,
            MeasurementKind.Area => Math.Abs(SignedArea(points)) * scale * scale,
            MeasurementKind.Angle => points.Count >= 3 ? AngleDegrees(points[0], points[1], points[2]) : 0,
            _ => 0,
        };
    }

    /// <summary>The label text: "12.50 ft", "3' 4\"", "40.00 m²" or "45.0°".</summary>
    internal static string Label(MeasurementAnnotation measurement, double pointsPerUnit, CultureInfo culture)
    {
        double value = Value(measurement, pointsPerUnit);
        return measurement.Kind switch
        {
            MeasurementKind.Area => MeasurementUnits.FormatArea(value, measurement.Unit, measurement.Precision, culture),
            MeasurementKind.Angle => MeasurementUnits.FormatAngle(value, culture),
            _ => MeasurementUnits.FormatLength(value, measurement.Unit, measurement.Precision, culture),
        };
    }

    /// <summary>
    /// Where the label goes when the user has not placed it: above the middle of a distance, at
    /// the middle of an area, beside the longest side of a perimeter, and inside an angle.
    /// </summary>
    internal static Point AutoLabelCenter(MeasurementAnnotation measurement)
    {
        var points = measurement.Points;
        if (points.Count == 0) return default;
        double clearance = measurement.LabelSize * 0.9 + measurement.StrokeWidth;
        switch (measurement.Kind)
        {
            case MeasurementKind.Distance when points.Count >= 2:
                return Beside(points[0], points[1], clearance);
            case MeasurementKind.Perimeter when points.Count >= 2:
            {
                int count = measurement.Closed ? points.Count : points.Count - 1;
                int longest = 0;
                double best = -1;
                for (int index = 0; index < count; index++)
                {
                    double length = (points[(index + 1) % points.Count] - points[index]).Length;
                    if (length > best) { best = length; longest = index; }
                }
                return Beside(points[longest], points[(longest + 1) % points.Count], clearance);
            }
            case MeasurementKind.Area when points.Count >= 3:
                return Centroid(points);
            case MeasurementKind.Angle when points.Count >= 3:
            {
                Vector first = points[0] - points[1], second = points[2] - points[1];
                if (first.Length < 0.001 || second.Length < 0.001) return points[1];
                first.Normalize();
                second.Normalize();
                Vector bisector = first + second;
                if (bisector.Length < 0.001) bisector = new Vector(-first.Y, first.X);
                bisector.Normalize();
                // The label box is wider than it is tall, so its reach along the bisector depends on
                // the bisector's direction. Push it out by that reach so it clears the arc. An angle
                // label is at most six characters ("179.9°" in most languages).
                double size = Math.Max(1, measurement.LabelSize);
                double halfWidth = size * (0.3 + 0.55 * 6 / 2), halfHeight = size * 0.8;
                double reach = Math.Abs(bisector.X) * halfWidth + Math.Abs(bisector.Y) * halfHeight;
                return points[1] + bisector * (ArcRadius(measurement) + measurement.StrokeWidth + size * 0.3 + reach);
            }
            default:
                return points[0];
        }
    }

    /// <summary>The radius of the arc an angle draws at its corner.</summary>
    internal static double ArcRadius(MeasurementAnnotation measurement)
    {
        var points = measurement.Points;
        if (points.Count < 3) return 0;
        double shorter = Math.Min((points[0] - points[1]).Length, (points[2] - points[1]).Length);
        return Math.Clamp(shorter * 0.3, 6, measurement.LabelSize * 2.5);
    }

    /// <summary>The arc an angle draws, from the first arm toward the second, as points.</summary>
    internal static List<Point> ArcPoints(MeasurementAnnotation measurement)
    {
        var points = measurement.Points;
        var arc = new List<Point>();
        if (points.Count < 3) return arc;
        Point corner = points[1];
        double radius = ArcRadius(measurement);
        double start = Math.Atan2(points[0].Y - corner.Y, points[0].X - corner.X);
        double end = Math.Atan2(points[2].Y - corner.Y, points[2].X - corner.X);
        double sweep = end - start;
        while (sweep > Math.PI) sweep -= 2 * Math.PI;
        while (sweep < -Math.PI) sweep += 2 * Math.PI;
        int steps = Math.Max(4, (int)Math.Ceiling(Math.Abs(sweep) / (Math.PI / 24)));
        for (int step = 0; step <= steps; step++)
        {
            double angle = start + sweep * step / steps;
            arc.Add(new Point(corner.X + Math.Cos(angle) * radius, corner.Y + Math.Sin(angle) * radius));
        }
        return arc;
    }

    /// <summary>
    /// The two end ticks of a distance, square to the line, sized like the butt line endings the
    /// saved annotation draws (4 line widths, at least 6 points, 45% each side).
    /// </summary>
    internal static (Point A, Point B)[] DistanceTicks(MeasurementAnnotation measurement, double pointsPerUnit)
    {
        var points = measurement.Points;
        if (measurement.Kind != MeasurementKind.Distance || points.Count < 2) return [];
        Vector along = points[1] - points[0];
        if (along.Length < 0.001) return [];
        along.Normalize();
        var normal = new Vector(-along.Y, along.X);
        double minimum = pointsPerUnit > 0 ? 6 / pointsPerUnit : 6;
        double half = Math.Max(minimum, measurement.StrokeWidth * 4) * 0.45;
        return [(points[0] + normal * half, points[0] - normal * half),
            (points[1] + normal * half, points[1] - normal * half)];
    }

    /// <summary>The label box for text of a given width, centered on the label point.</summary>
    internal static Rect LabelBox(Point center, double textWidth, double size)
    {
        double padding = size * 0.3;
        return new Rect(center.X - textWidth / 2 - padding, center.Y - size / 2 - padding,
            textWidth + 2 * padding, size + 2 * padding);
    }

    internal static double PathLength(IReadOnlyList<Point> points, bool closed)
    {
        double total = 0;
        for (int index = 1; index < points.Count; index++) total += (points[index] - points[index - 1]).Length;
        if (closed && points.Count > 2) total += (points[0] - points[^1]).Length;
        return total;
    }

    internal static double SignedArea(IReadOnlyList<Point> points)
    {
        double sum = 0;
        for (int index = 0; index < points.Count; index++)
        {
            Point current = points[index], next = points[(index + 1) % points.Count];
            sum += current.X * next.Y - next.X * current.Y;
        }
        return sum / 2;
    }

    internal static double AngleDegrees(Point first, Point corner, Point second)
    {
        Vector a = first - corner, b = second - corner;
        double denominator = a.Length * b.Length;
        if (denominator < 1e-9) return 0;
        return Math.Acos(Math.Clamp((a.X * b.X + a.Y * b.Y) / denominator, -1, 1)) * 180 / Math.PI;
    }

    /// <summary>True when a point lies inside a polygon (even-odd).</summary>
    internal static bool Contains(IReadOnlyList<Point> polygon, Point point)
    {
        bool inside = false;
        for (int index = 0, previous = polygon.Count - 1; index < polygon.Count; previous = index++)
        {
            Point a = polygon[index], b = polygon[previous];
            if ((a.Y > point.Y) != (b.Y > point.Y)
                && point.X < (b.X - a.X) * (point.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    // The side of a segment that is up on screen, so a horizontal label sits above its line.
    private static Point Beside(Point start, Point end, double clearance)
    {
        Vector along = end - start;
        Point middle = start + along / 2;
        if (along.Length < 0.001) return new Point(middle.X, middle.Y - clearance);
        var normal = new Vector(-along.Y, along.X);
        normal.Normalize();
        if (normal.Y > 0 || (Math.Abs(normal.Y) < 1e-9 && normal.X > 0)) normal = -normal;
        return middle + normal * clearance;
    }

    private static Point Centroid(IReadOnlyList<Point> points)
    {
        double area = SignedArea(points);
        if (Math.Abs(area) < 1e-6)
            return new Point(points.Average(point => point.X), points.Average(point => point.Y));
        double x = 0, y = 0;
        for (int index = 0; index < points.Count; index++)
        {
            Point current = points[index], next = points[(index + 1) % points.Count];
            double cross = current.X * next.Y - next.X * current.Y;
            x += (current.X + next.X) * cross;
            y += (current.Y + next.Y) * cross;
        }
        return new Point(x / (6 * area), y / (6 * area));
    }
}
