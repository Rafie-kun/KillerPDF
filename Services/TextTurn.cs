using System.Windows;

namespace KillerPDF.Services;

/// <summary>
/// Geometry for text that reads sideways or upside down on the canvas. A turn is the clockwise angle
/// on screen (0, 90, 180 or 270) from left-to-right reading to the text's own reading direction.
/// Turned text has a reading frame: the canvas turned back by the turn, where the text reads left to
/// right and its lines stack downward, so line grouping and box placement work as they do for level
/// text. A text box's frame origin is the canvas point where its first line starts.
/// </summary>
internal static class TextTurn
{
    internal static int Normalize(int turn) => ((turn % 360) + 360) % 360;

    /// <summary>The turn of text whose baseline runs from <paramref name="start"/> to
    /// <paramref name="end"/> on the canvas, snapped to the nearest quarter. A zero-length baseline
    /// counts as level.</summary>
    internal static int FromBaseline(Point start, Point end)
    {
        double dx = end.X - start.X, dy = end.Y - start.Y;
        if (dx == 0 && dy == 0) return 0;
        if (Math.Abs(dx) >= Math.Abs(dy)) return dx > 0 ? 0 : 180;
        return dy > 0 ? 90 : 270;
    }

    /// <summary>Maps a canvas point into the reading frame of the turn.</summary>
    internal static Point ToReading(Point canvas, int turn) => Normalize(turn) switch
    {
        90 => new Point(canvas.Y, -canvas.X),
        180 => new Point(-canvas.X, -canvas.Y),
        270 => new Point(-canvas.Y, canvas.X),
        _ => canvas
    };

    /// <summary>Maps a reading-frame point back onto the canvas.</summary>
    internal static Point FromReading(Point reading, int turn) => Normalize(turn) switch
    {
        90 => new Point(-reading.Y, reading.X),
        180 => new Point(-reading.X, -reading.Y),
        270 => new Point(reading.Y, -reading.X),
        _ => reading
    };

    internal static Rect ToReading(Rect canvas, int turn) => Normalize(turn) == 0 ? canvas
        : new Rect(ToReading(canvas.TopLeft, turn), ToReading(canvas.BottomRight, turn));

    internal static Rect FromReading(Rect reading, int turn) => Normalize(turn) == 0 ? reading
        : new Rect(FromReading(reading.TopLeft, turn), FromReading(reading.BottomRight, turn));

    /// <summary>The canvas size of a box that is <paramref name="width"/> along its reading
    /// direction and <paramref name="height"/> across its lines.</summary>
    internal static Size Footprint(int turn, double width, double height) =>
        Normalize(turn) is 90 or 270 ? new Size(height, width) : new Size(width, height);

    /// <summary>The offset from a turned box's top-left on the canvas to its frame origin.</summary>
    internal static Vector OriginOffset(int turn, double width, double height) => Normalize(turn) switch
    {
        90 => new Vector(height, 0),
        180 => new Vector(width, height),
        270 => new Vector(0, width),
        _ => new Vector(0, 0)
    };

    /// <summary>The clockwise rotation of the reading frame as a PDF matrix (a, b, c, d) in a
    /// y-down drawing frame, for placing turned text at its frame origin.</summary>
    internal static (double A, double B, double C, double D) Matrix(int turn) => Normalize(turn) switch
    {
        90 => (0, 1, -1, 0),
        180 => (-1, 0, 0, -1),
        270 => (0, -1, 1, 0),
        _ => (1, 0, 0, 1)
    };
}
