namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    // Axis-aligned opaque RGB output consumes one converted source row at a time.
    // Keep the original plane grid and accumulation order; only its storage lifetime
    // changes. Each row worker owns its converter/cache and returns its scratch row.
    private static void PaintReducedRgbRows(RasterSurface target, int targetWidth,
        int targetHeight, double scaleX, double scaleY, Matrix inverse, byte[] samples,
        int rowBytes, int sourceWidth, int sourceHeight, int components, int bits,
        double[] decode, ImageColorSpace colorSpace,
        ImageSampleConverter.AreaSpan[]? columns, ImageSampleConverter.AreaSpan[]? rows,
        PdfBinaryAreaSampler.Column[]? binaryColumns, int planeWidth, int planeHeight,
        bool pixelAlignedPlane, IReadOnlyList<ClipRegion> clips, bool rectangularClips,
        int left, int top, int paintLeft, int paintTop, int paintRight, int paintBottom,
        CancellationToken cancellationToken)
    {
        double unitStepX = inverse.A * (1 / scaleX);
        byte[] data = target.Data;
        byte[]? groupAlpha = target.GroupAlpha;
        ForEachRow(paintTop, paintBottom,
            (long)(paintRight - paintLeft) * (paintBottom - paintTop), (rowStart, rowEnd) =>
        {
            byte[] row = RasterBuffers.Rent(checked(planeWidth * 4));
            try
            {
                var converter = new ImageSampleConverter(samples, rowBytes, components,
                    bits, decode, colorSpace, targetInk: false, targetProfile: null,
                    areaColumns: columns, areaRows: rows, binaryColumns: binaryColumns);
                int convertedRow = -1;
                for (int y = rowStart; y < rowEnd; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Point first = inverse.Apply((left + .5) / scaleX,
                        (targetHeight - y - .5) / scaleY);
                    if (first.Y < 0 || first.Y >= 1) continue;
                    int py = pixelAlignedPlane ? y - top
                        : Math.Min((int)((1 - first.Y) * planeHeight), planeHeight - 1);
                    if (convertedRow != py)
                    {
                        for (int px = 0; px < planeWidth; px++)
                        {
                            uint color = converter.ConvertArea(px, py, planeWidth,
                                planeHeight, sourceWidth, sourceHeight, cancellationToken);
                            WriteInk(row, px * 4, color);
                        }
                        convertedRow = py;
                    }
                    double unitX = first.X;
                    int rowOffset = target.Offset(left, y);
                    for (int x = left; x < paintRight; x++, unitX += unitStepX)
                    {
                        if (x < paintLeft || unitX < 0 || unitX >= 1) continue;
                        int px = pixelAlignedPlane ? x - left
                            : Math.Min((int)(unitX * planeWidth), planeWidth - 1);
                        int sourceOffset = px * 4;
                        int coverage = rectangularClips ? 255 : ClipCoverage(clips, x, y);
                        if (coverage == 0) continue;
                        if (coverage == 255)
                        {
                            int offset = rowOffset + (x - left) * 4;
                            data[offset] = row[sourceOffset];
                            data[offset + 1] = row[sourceOffset + 1];
                            data[offset + 2] = row[sourceOffset + 2];
                            data[offset + 3] = 255;
                            if (groupAlpha is not null) groupAlpha[offset / 4] = 255;
                        }
                        else
                        {
                            Color color = new(row[sourceOffset + 2], row[sourceOffset + 1],
                                row[sourceOffset]);
                            SetPixel(target, targetWidth, x, y, color, coverage / 255d,
                                RendererBlendMode.Normal);
                        }
                    }
                }
            }
            finally { RasterBuffers.Return(row); }
        }, null, cancellationToken);
    }
}
