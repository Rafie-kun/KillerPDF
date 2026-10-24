namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    // How a reduced plane's cells map to device pixels along one axis. An aligned plane has one
    // cell per pixel whose center lies inside the image, read by pixel position (counted from
    // the far edge when the image is mirrored); other planes are read through unit coordinates.
    private readonly struct PlaneAxis(int first, int count, bool mirrored, bool aligned)
    {
        internal int Cell(int pixel, double unit) => aligned
            ? Math.Clamp(mirrored ? first + count - 1 - pixel : pixel - first, 0, count - 1)
            : Math.Min((int)(unit * count), count - 1);
    }

    // Axis-aligned opaque RGB or process-ink output consumes one source row at a time.
    // Keep the original plane grid and accumulation order; only its storage lifetime
    // changes. Each row worker owns its converter/cache and returns its scratch row.
    private static void PaintReducedImageRows(RasterSurface target, int targetWidth,
        int targetHeight, double scaleX, double scaleY, Matrix inverse, byte[] samples,
        int rowBytes, int sourceWidth, int sourceHeight, int components, int bits,
        double[] decode, ImageColorSpace colorSpace,
        ImageSampleConverter.AreaSpan[]? columns, ImageSampleConverter.AreaSpan[]? rows,
        PdfBinaryAreaSampler.Column[]? binaryColumns, int planeWidth, int planeHeight,
        bool pixelAlignedPlane, PlaneAxis columnAxis, PlaneAxis rowAxis,
        IReadOnlyList<ClipRegion> clips, bool rectangularClips,
        int left, int top, int paintLeft, int paintTop, int paintRight, int paintBottom,
        CancellationToken cancellationToken)
    {
        double unitStepX = inverse.A * (1 / scaleX);
        int firstColumn = planeWidth, endColumn = 0;
        if (pixelAlignedPlane)
        {
            firstColumn = Math.Max(0, paintLeft - left);
            endColumn = Math.Min(planeWidth, paintRight - left);
        }
        else
        {
            // The axis-aligned mapping is the same for every row. Repeat the original
            // additions once to retain exact column selection at fractional boundaries.
            double unitX = inverse.Apply((left + .5) / scaleX,
                (targetHeight - paintTop - .5) / scaleY).X;
            for (int x = left; x < paintRight; x++, unitX += unitStepX)
            {
                if ((x & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (x < paintLeft || unitX < 0 || unitX >= 1) continue;
                int px = columnAxis.Cell(x, unitX);
                firstColumn = Math.Min(firstColumn, px);
                endColumn = Math.Max(endColumn, px + 1);
            }
        }
        if (firstColumn >= endColumn) return;
        byte[] data = target.Data;
        bool targetInk = target.Ink is not null;
        PdfColorTransform? profile = target.InputProfile;
        byte[]? groupAlpha = target.GroupAlpha;
        ForEachRow(paintTop, paintBottom,
            (long)(paintRight - paintLeft) * (paintBottom - paintTop), (rowStart, rowEnd) =>
        {
            byte[] row = RasterBuffers.Rent(checked((endColumn - firstColumn) * 4));
            try
            {
                var converter = new ImageSampleConverter(samples, rowBytes, components,
                    bits, decode, colorSpace, targetInk, profile,
                    areaColumns: columns, areaRows: rows, binaryColumns: binaryColumns);
                int convertedRow = -1;
                for (int y = rowStart; y < rowEnd; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Point first = inverse.Apply((left + .5) / scaleX,
                        (targetHeight - y - .5) / scaleY);
                    if (first.Y < 0 || first.Y >= 1) continue;
                    int py = pixelAlignedPlane ? y - top
                        : rowAxis.Cell(y, 1 - first.Y);
                    if (convertedRow != py)
                    {
                        for (int px = firstColumn; px < endColumn; px++)
                        {
                            uint color = converter.ConvertArea(px, py, planeWidth,
                                planeHeight, sourceWidth, sourceHeight, cancellationToken);
                            WriteInk(row, (px - firstColumn) * 4, color);
                        }
                        convertedRow = py;
                    }
                    double unitX = first.X;
                    int rowOffset = target.Offset(left, y);
                    for (int x = left; x < paintRight; x++, unitX += unitStepX)
                    {
                        if (x < paintLeft || unitX < 0 || unitX >= 1) continue;
                        int px = pixelAlignedPlane ? x - left
                            : columnAxis.Cell(x, unitX);
                        int sourceOffset = (px - firstColumn) * 4;
                        int coverage = rectangularClips ? 255 : ClipCoverage(clips, x, y);
                        if (coverage == 0) continue;
                        if (coverage == 255)
                        {
                            int offset = rowOffset + (x - left) * 4;
                            if (targetInk)
                            {
                                WriteInk(target.Ink!, offset, ReadInk(row, sourceOffset));
                                target.SetAlpha(offset, 255);
                            }
                            else
                            {
                                data[offset] = row[sourceOffset];
                                data[offset + 1] = row[sourceOffset + 1];
                                data[offset + 2] = row[sourceOffset + 2];
                                data[offset + 3] = 255;
                            }
                            groupAlpha?[offset / 4] = 255;
                        }
                        else
                        {
                            Color color = targetInk ? InkColor(ReadInk(row, sourceOffset))
                                : new(row[sourceOffset + 2], row[sourceOffset + 1], row[sourceOffset]);
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
