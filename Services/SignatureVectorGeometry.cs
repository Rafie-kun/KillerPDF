using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using WpfPath = System.Windows.Shapes.Path;

namespace KillerPDF.Services;

/// <summary>Draws a vector signature (#398) on screen from the same outlines the PDF gets.</summary>
internal static class SignatureVectorGeometry
{
    // Placing, moving and resizing redraw the signature on every mouse move, so each outline is
    // read once and kept with it.
    private static readonly ConditionalWeakTable<SignatureVector, Geometry> Cache = new();

    /// <summary>One shape per outline, drawn at <paramref name="scale"/> with its top left at
    /// <paramref name="left"/>, <paramref name="top"/>.</summary>
    internal static IEnumerable<WpfPath> Shapes(IEnumerable<SignatureVector> vectors,
        double left, double top, double scale)
    {
        var transform = new MatrixTransform(scale, 0, 0, scale, left, top);
        transform.Freeze();
        foreach (SignatureVector vector in vectors)
        {
            if (OutlineOf(vector) is not Geometry geometry) continue;
            Brush? fill = BrushFor(vector.Fill);
            Brush? stroke = vector.StrokeWidth > 0 ? BrushFor(vector.Stroke) : null;
            if (fill is null && stroke is null) continue;
            yield return new WpfPath
            {
                Data = geometry,
                Fill = fill,
                Stroke = stroke,
                StrokeThickness = vector.StrokeWidth,
                StrokeStartLineCap = Cap(vector.LineCap),
                StrokeEndLineCap = Cap(vector.LineCap),
                StrokeLineJoin = vector.LineJoin switch { 1 => PenLineJoin.Round, 2 => PenLineJoin.Bevel, _ => PenLineJoin.Miter },
                RenderTransform = transform,
                IsHitTestVisible = false
            };
        }
    }

    private static PenLineCap Cap(int cap) => cap switch { 1 => PenLineCap.Round, 2 => PenLineCap.Square, _ => PenLineCap.Flat };

    private static Brush? BrushFor(uint? argb)
    {
        if (argb is not uint color || color >> 24 == 0) return null;
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)(color >> 24), (byte)((color >> 16) & 0xFF), (byte)((color >> 8) & 0xFF), (byte)(color & 0xFF)));
        brush.Freeze();
        return brush;
    }

    private static Geometry? OutlineOf(SignatureVector vector)
    {
        if (Cache.TryGetValue(vector, out Geometry? cached)) return cached;
        List<VectorSegment> segments;
        try { segments = SvgPathData.Parse(vector.Data); }
        catch (FormatException) { return null; }
        var geometry = new PathGeometry { FillRule = vector.EvenOdd ? FillRule.EvenOdd : FillRule.Nonzero };
        PathFigure? figure = null;
        foreach (VectorSegment segment in segments)
            switch (segment.Verb)
            {
                case VectorVerb.Move:
                    figure = new PathFigure { StartPoint = new Point(segment.X, segment.Y), IsFilled = true };
                    geometry.Figures.Add(figure);
                    break;
                case VectorVerb.Line:
                    figure?.Segments.Add(new LineSegment(new Point(segment.X, segment.Y), true));
                    break;
                case VectorVerb.Cubic:
                    figure?.Segments.Add(new BezierSegment(new Point(segment.X1, segment.Y1),
                        new Point(segment.X2, segment.Y2), new Point(segment.X, segment.Y), true));
                    break;
                default:
                    if (figure is not null) figure.IsClosed = true;
                    break;
            }
        geometry.Freeze();
        Cache.AddOrUpdate(vector, geometry);
        return geometry;
    }
}
