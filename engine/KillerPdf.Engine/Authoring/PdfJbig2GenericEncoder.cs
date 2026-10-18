namespace KillerPdf.Engine.Authoring;

// Lossless JBIG2 generic-region coding (ITU T.88, template 0 with typical prediction) for one-bit
// images, written as an embedded PDF JBIG2 stream: a page information segment and one immediate
// generic region, without the file header and end-of-page segment PDF leaves out.
internal static class PdfJbig2GenericEncoder
{
    private const int TypicalPredictionContext = 0x9B25;

    internal static byte[] Encode(int width, int height, ReadOnlySpan<byte> pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.Length != checked(width * height))
            throw new ArgumentException($"A {width} by {height} bitonal image requires {width * height} samples.", nameof(pixels));

        // One byte per pixel, 1 for black, with four blank columns on each side and two blank rows
        // above, so every context pixel can be read without bounds checks.
        int stride = width + 8;
        byte[] plane = new byte[checked(stride * (height + 2))];
        for (int y = 0; y < height; y++)
        {
            int row = (y + 2) * stride + 4;
            ReadOnlySpan<byte> source = pixels.Slice(y * width, width);
            for (int x = 0; x < width; x++)
                if (source[x] < 128) plane[row + x] = 1;
        }

        var coder = new MqEncoder();
        int typical = 0;
        for (int y = 0; y < height; y++)
        {
            int row = (y + 2) * stride + 4, above = row - stride, twoAbove = above - stride;
            // A row identical to the one above costs one decision (6.2.5.7, TPGDON).
            int repeats = y > 0 && plane.AsSpan(row, width).SequenceEqual(plane.AsSpan(above, width)) ? 1 : 0;
            coder.Encode(TypicalPredictionContext, repeats ^ typical);
            typical = repeats;
            if (typical == 1) continue;

            // Template 0 with the default adaptive pixels (3,-1), (-3,-1), (2,-2) and (-2,-2),
            // bit for bit the context the engine's decoder forms.
            int context = plane[row - 1] | plane[row - 2] << 1 | plane[row - 3] << 2 | plane[row - 4] << 3
                | plane[above + 3] << 4 | plane[above + 2] << 5 | plane[above + 1] << 6 | plane[above] << 7
                | plane[above - 1] << 8 | plane[above - 2] << 9 | plane[above - 3] << 10
                | plane[twoAbove + 2] << 11 | plane[twoAbove + 1] << 12 | plane[twoAbove] << 13
                | plane[twoAbove - 1] << 14 | plane[twoAbove - 2] << 15;
            for (int x = 0; x < width; x++)
            {
                int bit = plane[row + x];
                coder.Encode(context, bit);
                context = (context & 0x7BF7) << 1 | bit | plane[above + x + 4] << 4 | plane[twoAbove + x + 3] << 11;
            }
        }
        byte[] coded = coder.Finish();

        using var output = new MemoryStream(coded.Length + 64);
        // Page information (7.4.8): size, unknown resolution, eventually lossless, no striping.
        WriteSegmentHeader(output, 0, 48, 19);
        WriteInt32(output, width);
        WriteInt32(output, height);
        WriteInt32(output, 0);
        WriteInt32(output, 0);
        output.WriteByte(0x01);
        output.WriteByte(0);
        output.WriteByte(0);
        // Immediate generic region (7.4.6): the whole page at 0,0, arithmetic coded, template 0,
        // typical prediction on, then the four adaptive pixels.
        WriteSegmentHeader(output, 1, 38, 17 + 1 + 8 + coded.Length);
        WriteInt32(output, width);
        WriteInt32(output, height);
        WriteInt32(output, 0);
        WriteInt32(output, 0);
        output.WriteByte(0);
        output.WriteByte(0x08);
        foreach (sbyte offset in (ReadOnlySpan<sbyte>)[3, -1, -3, -1, 2, -2, -2, -2])
            output.WriteByte(unchecked((byte)offset));
        output.Write(coded);
        return output.ToArray();
    }

    // Segment header (7.2) for a segment of page 1 that refers to no other segment.
    private static void WriteSegmentHeader(Stream output, int number, int type, int dataLength)
    {
        WriteInt32(output, number);
        output.WriteByte((byte)type);
        output.WriteByte(0);
        output.WriteByte(1);
        WriteInt32(output, dataLength);
    }

    private static void WriteInt32(Stream output, int value)
    {
        output.WriteByte((byte)(value >> 24));
        output.WriteByte((byte)(value >> 16));
        output.WriteByte((byte)(value >> 8));
        output.WriteByte((byte)value);
    }

    // The MQ arithmetic encoder of T.88 Annex E.2, with one probability state per context.
    private sealed class MqEncoder
    {
        private static readonly ushort[] Qe =
        [
            0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221, 0x5601, 0x5401,
            0x4801, 0x3801, 0x3001, 0x2401, 0x1C01, 0x1601, 0x5601, 0x5401,
            0x5101, 0x4801, 0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201,
            0x1C01, 0x1801, 0x1601, 0x1401, 0x1201, 0x1101, 0x0AC1, 0x09C1,
            0x08A1, 0x0521, 0x0441, 0x02A1, 0x0221, 0x0141, 0x0111, 0x0085,
            0x0049, 0x0025, 0x0015, 0x0009, 0x0005, 0x0001, 0x5601
        ];
        private static readonly byte[] NextMps =
        [
            1, 2, 3, 4, 5, 38, 7, 8, 9, 10, 11, 12, 13, 29, 15, 16,
            17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
            33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46
        ];
        private static readonly byte[] NextLps =
        [
            1, 6, 9, 12, 29, 33, 6, 14, 14, 14, 17, 18, 20, 21, 14, 14,
            15, 16, 17, 18, 19, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29,
            30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46
        ];

        private readonly byte[] _state = new byte[65536];
        private readonly byte[] _mps = new byte[65536];
        private readonly List<byte> _output = [];
        private int _a = 0x8000;
        private uint _c;
        private int _ct = 12;
        private int _b;
        private bool _started;

        internal void Encode(int context, int bit)
        {
            int index = _state[context];
            int qe = Qe[index];
            _a -= qe;
            if (bit == _mps[context])
            {
                if ((_a & 0x8000) != 0)
                {
                    _c += (uint)qe;
                    return;
                }
                if (_a < qe) _a = qe;
                else _c += (uint)qe;
                _state[context] = NextMps[index];
            }
            else
            {
                if (_a < qe) _c += (uint)qe;
                else _a = qe;
                if (index is 0 or 6 or 14) _mps[context] ^= 1;
                _state[context] = NextLps[index];
            }
            do
            {
                _a <<= 1;
                _c <<= 1;
                if (--_ct == 0) ByteOut();
            } while ((_a & 0x8000) == 0);
        }

        internal byte[] Finish()
        {
            uint top = _c + (uint)_a;
            _c |= 0xFFFF;
            if (_c >= top) _c -= 0x8000;
            _c <<= _ct;
            ByteOut();
            _c <<= _ct;
            ByteOut();
            if (_b != 0xFF) Next(0xFF);
            Next(0xAC);
            _output.Add((byte)_b);
            return [.. _output];
        }

        private void ByteOut()
        {
            if (_b == 0xFF)
            {
                Next((int)(_c >> 20));
                _c &= 0xFFFFF;
                _ct = 7;
            }
            else if (_c < 0x8000000)
            {
                Next((int)(_c >> 19));
                _c &= 0x7FFFF;
                _ct = 8;
            }
            else
            {
                _b++;
                if (_b == 0xFF)
                {
                    _c &= 0x7FFFFFF;
                    Next((int)(_c >> 20));
                    _c &= 0xFFFFF;
                    _ct = 7;
                }
                else
                {
                    Next((int)(_c >> 19));
                    _c &= 0x7FFFF;
                    _ct = 8;
                }
            }
        }

        // Moves to the next output byte; the byte before the first is a placeholder.
        private void Next(int value)
        {
            if (_started) _output.Add((byte)_b);
            _started = true;
            _b = value & 0xFF;
        }
    }
}
