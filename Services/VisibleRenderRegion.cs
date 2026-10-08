namespace KillerPDF.Services;

internal readonly record struct VisibleRenderRegion(int Left, int Top, int Width, int Height,
    int DisplayLeft, int DisplayTop, int DisplayWidth, int DisplayHeight)
{
    internal static VisibleRenderRegion? Create(int width, int height, int rotation,
        double left, double top, double right, double bottom)
    {
        rotation = ((rotation % 360) + 360) % 360;
        if (width <= 0 || height <= 0 || rotation % 90 != 0
            || !double.IsFinite(left + top + right + bottom)) return null;
        int dw = rotation is 90 or 270 ? height : width;
        int dh = rotation is 90 or 270 ? width : height;
        int x0 = Math.Clamp((int)Math.Floor(Math.Clamp(left, 0, 1) * dw), 0, dw);
        int y0 = Math.Clamp((int)Math.Floor(Math.Clamp(top, 0, 1) * dh), 0, dh);
        int x1 = Math.Clamp((int)Math.Ceiling(Math.Clamp(right, 0, 1) * dw), 0, dw);
        int y1 = Math.Clamp((int)Math.Ceiling(Math.Clamp(bottom, 0, 1) * dh), 0, dh);
        if (x1 <= x0 || y1 <= y0) return null;
        var (l, t, r, b) = rotation switch
        {
            90 => (y0, height - x1, y1, height - x0),
            180 => (width - x1, height - y1, width - x0, height - y0),
            270 => (width - y1, x0, width - y0, x1),
            _ => (x0, y0, x1, y1)
        };
        return new(l, t, r - l, b - t, x0, y0, dw, dh);
    }
}
