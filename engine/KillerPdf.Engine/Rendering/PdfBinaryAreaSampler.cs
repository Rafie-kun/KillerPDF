using System.Buffers.Binary;
using System.Numerics;

namespace KillerPdf.Engine.Rendering;

internal static class PdfBinaryAreaSampler
{
    // Footprints that do not fall on an even grid are measured in 1/1024 of a sample. The sample
    // array bounds width times height, so coverage products stay far inside a long.
    internal const long RangeUnit = 1024;

    // Source rows Top to Bottom, measured in 1/Unit of a row.
    internal readonly record struct Row(long Top, long Bottom, int First, int Last, long Unit)
    {
        internal long Total => Bottom - Top;

        internal static Row Create(int y, int height, int outputHeight)
        {
            long top = (long)y * height, bottom = (long)(y + 1) * height;
            return new(top, bottom, (int)(top / outputHeight), (int)((bottom - 1) / outputHeight), outputHeight);
        }

        // The source rows under [start, end), given in rows.
        internal static Row FromRange(double start, double end, int height)
        {
            (long top, long bottom) = Bounds(start, end, height);
            int first = (int)Math.Min(top / RangeUnit, height - 1);
            return new(top, bottom, first, bottom > top ? (int)((bottom - 1) / RangeUnit) : first - 1, RangeUnit);
        }
    }

    // Source columns from First to Last, the end ones partly covered, measured in 1/Unit of a
    // column; Total is the footprint's whole width.
    internal readonly record struct Column(int First, int Last, long FirstWeight, long LastWeight, long Unit, long Total)
    {
        internal static Column Create(int x, int width, int outputWidth) =>
            FromBounds((long)x * width, (long)(x + 1) * width, outputWidth);

        // The source columns under [start, end), given in columns.
        internal static Column FromRange(double start, double end, int width)
        {
            (long left, long right) = Bounds(start, end, width);
            return right > left ? FromBounds(left, right, RangeUnit)
                : new((int)Math.Min(left / RangeUnit, width - 1), (int)Math.Min(left / RangeUnit, width - 1), 0, 0, RangeUnit, 0);
        }

        private static Column FromBounds(long left, long right, long unit)
        {
            int first = (int)(left / unit), last = (int)((right - 1) / unit);
            return new(first, last, Math.Min((long)(first + 1) * unit, right) - left,
                right - (long)last * unit, unit, right - left);
        }
    }

    private static (long Low, long High) Bounds(double start, double end, int length)
    {
        long limit = length * RangeUnit;
        long low = (long)Math.Clamp(Math.Round(start * RangeUnit), 0, limit);
        return (low, (long)Math.Clamp(Math.Round(end * RangeUnit), low, limit));
    }

    internal static Column[] CreateColumns(int width, int outputWidth)
    {
        var columns = new Column[outputWidth];
        for (int x = 0; x < columns.Length; x++)
            columns[x] = Column.Create(x, width, outputWidth);
        return columns;
    }

    internal static uint Sample(byte[] samples, int rowBytes, int width, int height,
        int x, int y, int outputWidth, int outputHeight, uint zero, uint one,
        CancellationToken cancellationToken) => Sample(samples, rowBytes,
            Column.Create(x, width, outputWidth), Row.Create(y, height, outputHeight), zero, one, cancellationToken);

    internal static uint Sample(byte[] samples, int rowBytes,
        in Column column, in Row bounds, uint zero, uint one, CancellationToken cancellationToken)
    {
        // Integer coordinates retain exact fractional coverage on the output grid.
        long selected = 0;
        cancellationToken.ThrowIfCancellationRequested();
        for (int sy = bounds.First; sy <= bounds.Last; sy++)
        {
            int row = sy * rowBytes;
            long horizontal = 0;
            if (Bit(samples, row, column.First) != 0)
                horizontal = column.FirstWeight;
            if (column.Last > column.First)
            {
                if (Bit(samples, row, column.Last) != 0)
                    horizontal += column.LastWeight;
                horizontal += (long)Count(samples, row, column.First + 1, column.Last) * column.Unit;
            }
            long vertical = Math.Min((long)(sy + 1) * bounds.Unit, bounds.Bottom)
                - Math.Max((long)sy * bounds.Unit, bounds.Top);
            selected += horizontal * vertical;
        }
        long area = column.Total * bounds.Total;
        if (selected == 0) return zero;
        if (selected == area) return one;
        uint first = Mix((byte)zero, (byte)one, selected, area);
        uint fourth = Mix((byte)(zero >> 24), (byte)(one >> 24), selected, area);
        if ((zero & 0xFFFFFF) == (uint)(byte)zero * 0x010101
            && (one & 0xFFFFFF) == (uint)(byte)one * 0x010101)
            return first * 0x010101 | fourth << 24;
        return first | Mix((byte)(zero >> 8), (byte)(one >> 8), selected, area) << 8
            | Mix((byte)(zero >> 16), (byte)(one >> 16), selected, area) << 16 | fourth << 24;
    }

    internal static void SampleRow(byte[] samples, int rowBytes,
        in Row bounds, ReadOnlySpan<Column> columns, uint zero, uint one,
        Span<long> coverage, Span<uint> destination, CancellationToken cancellationToken)
    {
        int outputWidth = columns.Length;
        coverage = coverage[..outputWidth];
        destination = destination[..outputWidth];
        cancellationToken.ThrowIfCancellationRequested();
        coverage.Clear();

        // Traverse each source row once while retaining exact coverage for every output column.
        for (int sy = bounds.First; sy <= bounds.Last; sy++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int row = sy * rowBytes;
            long vertical = Math.Min((long)(sy + 1) * bounds.Unit, bounds.Bottom)
                - Math.Max((long)sy * bounds.Unit, bounds.Top);
            for (int x = 0; x < outputWidth; x++)
            {
                if ((x & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                Column column = columns[x];
                long horizontal = 0;
                if (Bit(samples, row, column.First) != 0)
                    horizontal = column.FirstWeight;
                if (column.Last > column.First)
                {
                    if (Bit(samples, row, column.Last) != 0)
                        horizontal += column.LastWeight;
                    horizontal += (long)Count(samples, row, column.First + 1, column.Last) * column.Unit;
                }
                coverage[x] += horizontal * vertical;
            }
        }

        bool gray = (zero & 0xFFFFFF) == (uint)(byte)zero * 0x010101
            && (one & 0xFFFFFF) == (uint)(byte)one * 0x010101;
        for (int x = 0; x < outputWidth; x++)
        {
            if ((x & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            long selected = coverage[x];
            long area = columns[x].Total * bounds.Total;
            if (selected == 0)
            {
                destination[x] = zero;
                continue;
            }
            if (selected == area)
            {
                destination[x] = one;
                continue;
            }
            uint first = Mix((byte)zero, (byte)one, selected, area);
            uint fourth = Mix((byte)(zero >> 24), (byte)(one >> 24), selected, area);
            destination[x] = gray ? first * 0x010101 | fourth << 24
                : first | Mix((byte)(zero >> 8), (byte)(one >> 8), selected, area) << 8
                    | Mix((byte)(zero >> 16), (byte)(one >> 16), selected, area) << 16
                    | fourth << 24;
        }
    }

    private static uint Mix(byte zero, byte one, long selected, long area)
    {
        if (zero == one) return zero;
        long numerator = zero * (area - selected) + one * selected;
        long value = Math.DivRem(numerator, area, out long remainder);
        if (remainder * 2 > area || remainder * 2 == area && (value & 1) != 0) value++;
        return (uint)value;
    }

    private static int Bit(byte[] samples, int row, int x) =>
        (samples[row + (x >> 3)] >> (7 - (x & 7))) & 1;

    private static int Count(byte[] samples, int row, int start, int end)
    {
        int count = 0;
        if (start < end && (start & 7) != 0)
        {
            int length = Math.Min(end - start, 8 - (start & 7));
            uint mask = ((1u << length) - 1) << (8 - (start & 7) - length);
            count += BitOperations.PopCount(samples[row + (start >> 3)] & mask);
            start += length;
        }
        while (end - start >= 64)
        {
            count += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(samples.AsSpan(row + (start >> 3), 8)));
            start += 64;
        }
        while (end - start >= 8)
        {
            count += BitOperations.PopCount((uint)samples[row + (start >> 3)]);
            start += 8;
        }
        if (start < end)
            count += BitOperations.PopCount((uint)samples[row + (start >> 3)] >> (8 - (end - start)));
        return count;
    }
}
