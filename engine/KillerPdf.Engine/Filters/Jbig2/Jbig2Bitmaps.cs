// Derived from Apache PDFBox JBIG2 ImageIO Plugin and its C# port.
// Modified for the KillerPDF engine.
#nullable disable

namespace KillerPdf.Engine.Filters.Jbig2
{
    using System;
    using System.Numerics;

    internal static class Jbig2Bitmaps
    {
        /// <summary>
        /// Returns the specified rectangle area of the bitmap.
        /// </summary>
        /// <param name="roi">A <see cref="Jbig2Rectangle"/> that specifies the requested image section.</param>
        /// <param name="src">src the given bitmap</param>
        /// <returns>A <see cref="Jbig2Bitmap"/> that represents the requested image section.</returns>
        public static Jbig2Bitmap Extract(Jbig2Rectangle roi, Jbig2Bitmap src)
        {
            var dst = new Jbig2Bitmap(roi.Width, roi.Height);

            int upShift = roi.X & 0x07;
            int downShift = 8 - upShift;
            int dstLineStartIdx = 0;

            int padding = 8 - dst.Width & 0x07;
            int srcLineStartIdx = src.GetByteIndex(roi.X, roi.Y);
            int srcLineEndIdx = src.GetByteIndex(roi.X + roi.Width - 1, roi.Y);
            bool usePadding = dst.RowStride == srcLineEndIdx + 1 - srcLineStartIdx;

            int maxY = roi.GetMaxY();
            for (int y = roi.Y; y < maxY; y++)
            {
                int srcIdx = srcLineStartIdx;
                int dstIdx = dstLineStartIdx;

                if (srcLineStartIdx == srcLineEndIdx)
                {
                    var pixels = (byte)(src.GetByte(srcIdx) << upShift);
                    dst.SetByte(dstIdx, Unpad(padding, pixels));
                }
                else if (upShift == 0)
                {
                    for (int x = srcLineStartIdx; x <= srcLineEndIdx; x++)
                    {
                        byte value = src.GetByte(srcIdx++);

                        if (x == srcLineEndIdx && usePadding)
                        {
                            value = Unpad(padding, value);
                        }

                        dst.SetByte(dstIdx++, value);
                    }
                }
                else
                {
                    CopyLine(src, dst, upShift, downShift, padding, srcLineStartIdx, srcLineEndIdx,
                            usePadding, srcIdx, dstIdx);
                }

                srcLineStartIdx += src.RowStride;
                srcLineEndIdx += src.RowStride;
                dstLineStartIdx += dst.RowStride;
            }

            return dst;
        }

        /// <summary>
        /// The method combines two given bytes with a logical operator.
        /// The JBIG2 Standard specifies 5 possible combinations of bytes.
        /// Hint: Please take a look at ISO/IEC 14492:2001 (E) for detailed definition
        /// and description of the operators.
        /// </summary>
        /// <param name="value1">The value that should be combined with value2.</param>
        /// <param name="value2">The value that should be combined with value1.</param>
        /// <param name="op">The specified combination operator.</param>
        /// <returns>The combination result.</returns>
        public static byte CombineBytes(byte value1, byte value2, CombinationOperator op)
        {
            return op switch
            {
                CombinationOperator.OR => (byte)(value2 | value1),
                CombinationOperator.AND => (byte)(value2 & value1),
                CombinationOperator.XOR => (byte)(value2 ^ value1),
                CombinationOperator.XNOR => (byte)~(value1 ^ value2),
                _ => value2,// Old value is replaced by new value.
            };
        }

        /// <summary>
        /// This method combines a given bitmap with the current instance.
        /// Parts of the bitmap to blit that are outside of the target bitmap will be ignored.
        /// </summary>
        /// <param name="src">The bitmap that should be combined with the one of the current instance.</param>
        /// <param name="dst">The destination bitmap.</param>
        /// <param name="x">The x coordinate where the upper left corner of the bitmap to blit should be positioned.</param>
        /// <param name="y">The y coordinate where the upper left corner of the bitmap to blit should be positioned.</param>
        /// <param name="combinationOperator">The combination operator for combining two pixels.</param>
        public static void Blit(Jbig2Bitmap src, Jbig2Bitmap dst, int x, int y, CombinationOperator combinationOperator)
        {
            int left = (int)Math.Max(0L, x), top = (int)Math.Max(0L, y);
            int right = (int)Math.Min(dst.Width, (long)x + src.Width);
            int bottom = (int)Math.Min(dst.Height, (long)y + src.Height);
            if (right <= left || bottom <= top) return;

            int firstByte = left >> 3, lastByte = (right - 1) >> 3;
            byte[] source = src.ByteArray, destination = dst.ByteArray;
            int sourceRow = (int)((long)top - y) * src.RowStride;
            int targetRow = top * dst.RowStride;
            if ((x & 7) == 0)
            {
                int sourceStart = (int)(((long)left - x) >> 3);
                int count = (right - left) >> 3;
                int tailMask = 0xff << (8 - (right & 7));
                for (int row = top; row < bottom; row++, sourceRow += src.RowStride,
                    targetRow += dst.RowStride)
                {
                    CombineAligned(source.AsSpan(sourceRow + sourceStart, count),
                        destination.AsSpan(targetRow + firstByte, count), combinationOperator);
                    if ((right & 7) == 0) continue;
                    int offset = targetRow + firstByte + count;
                    byte oldValue = destination[offset];
                    byte combined = CombineBytes(oldValue,
                        source[sourceRow + sourceStart + count], combinationOperator);
                    destination[offset] = (byte)((oldValue & ~tailMask) | (combined & tailMask));
                }
                return;
            }
            int interiorCount = Math.Max(0, lastByte - firstByte - 1);
            int interiorSource = (int)((((long)firstByte + 1) * 8 - x) >> 3);
            int interiorShift = 8 - (x & 7);
            for (int row = top; row < bottom; row++, sourceRow += src.RowStride,
                targetRow += dst.RowStride)
            {
                if (interiorCount > 0)
                    CombineShifted(source.AsSpan(sourceRow + interiorSource, interiorCount + 1),
                        destination.AsSpan(targetRow + firstByte + 1, interiorCount),
                        interiorShift, combinationOperator);
                for (int boundary = 0; boundary < (firstByte == lastByte ? 1 : 2); boundary++)
                {
                    int column = boundary == 0 ? firstByte : lastByte;
                    int start = Math.Max(left, column * 8);
                    int end = (int)Math.Min(right, ((long)column + 1) * 8);
                    int sourceX = (int)((long)start - x);
                    int sourceByte = sourceX >> 3, sourceShift = sourceX & 7;
                    byte value = (byte)(source[sourceRow + sourceByte] << sourceShift);
                    if (sourceShift != 0 && sourceByte + 1 < src.RowStride)
                        value |= (byte)(source[sourceRow + sourceByte + 1] >> (8 - sourceShift));
                    int targetShift = start & 7;
                    int mask = (0xff >> targetShift) & (0xff << (8 - targetShift - (end - start)));
                    int offset = targetRow + column;
                    byte oldValue = destination[offset];
                    byte combined = CombineBytes(oldValue, (byte)(value >> targetShift), combinationOperator);
                    // Only declared source pixels participate. Keep destination pixels and
                    // row padding outside the clipped rectangle for every logical operator.
                    destination[offset] = (byte)((oldValue & ~mask) | (combined & mask));
                }
            }
        }

        private static void CombineAligned(ReadOnlySpan<byte> source, Span<byte> destination,
            CombinationOperator op)
        {
            if (op is not (CombinationOperator.OR or CombinationOperator.AND
                or CombinationOperator.XOR or CombinationOperator.XNOR))
            {
                source.CopyTo(destination);
                return;
            }
            int index = 0;
            if (Vector.IsHardwareAccelerated)
                for (; index <= source.Length - Vector<byte>.Count; index += Vector<byte>.Count)
                {
                    var incoming = new Vector<byte>(source.Slice(index));
                    var previous = new Vector<byte>(destination.Slice(index));
                    var combined = op switch
                    {
                        CombinationOperator.OR => previous | incoming,
                        CombinationOperator.AND => previous & incoming,
                        CombinationOperator.XOR => previous ^ incoming,
                        _ => ~(previous ^ incoming)
                    };
                    combined.CopyTo(destination.Slice(index));
                }
            for (; index < source.Length; index++)
                destination[index] = CombineBytes(destination[index], source[index], op);
        }

        private static void CombineShifted(ReadOnlySpan<byte> source, Span<byte> destination,
            int shift, CombinationOperator op)
        {
            int index = 0, downShift = 8 - shift;
            if (Vector.IsHardwareAccelerated)
                for (; index <= destination.Length - Vector<byte>.Count; index += Vector<byte>.Count)
                {
                    var incoming = (new Vector<byte>(source.Slice(index)) << shift)
                        | (new Vector<byte>(source.Slice(index + 1)) >> downShift);
                    var previous = new Vector<byte>(destination.Slice(index));
                    var combined = op switch
                    {
                        CombinationOperator.OR => previous | incoming,
                        CombinationOperator.AND => previous & incoming,
                        CombinationOperator.XOR => previous ^ incoming,
                        CombinationOperator.XNOR => ~(previous ^ incoming),
                        _ => incoming
                    };
                    combined.CopyTo(destination.Slice(index));
                }
            for (; index < destination.Length; index++)
            {
                byte incoming = (byte)((source[index] << shift) | (source[index + 1] >> downShift));
                destination[index] = CombineBytes(destination[index], incoming, op);
            }
        }

        private static void CopyLine(Jbig2Bitmap src, Jbig2Bitmap dst, int sourceUpShift, int sourceDownShift,
                int padding, int firstSourceByteOfLine, int lastSourceByteOfLine, bool usePadding,
                int sourceOffset, int targetOffset)
        {
            for (int x = firstSourceByteOfLine; x < lastSourceByteOfLine; x++)
            {
                if (sourceOffset + 1 < src.ByteArray.Length)
                {
                    bool isLastByte = x + 1 == lastSourceByteOfLine;
                    var value = (byte)(src.GetByte(sourceOffset++) << sourceUpShift
                            | (int)(uint)(src.GetByte(sourceOffset) & 0xff) >> sourceDownShift);

                    if (isLastByte && !usePadding)
                    {
                        value = Unpad(padding, value);
                    }

                    dst.SetByte(targetOffset++, value);

                    if (isLastByte && usePadding)
                    {
                        value = Unpad(padding, (byte)((src.GetByte(sourceOffset) & 0xff) << sourceUpShift));
                        dst.SetByte(targetOffset, value);
                    }
                }
                else
                {
                    var value = (byte)(src.GetByte(sourceOffset++) << sourceUpShift & 0xff);
                    dst.SetByte(targetOffset++, value);
                }
            }
        }

        /// <summary>
        /// Removes unnecessary bits from a byte.
        /// </summary>
        /// <param name="padding">The amount of unnecessary bits.</param>
        /// <param name="value">The byte that should be cleaned up.</param>
        /// <returns>A cleaned byte.</returns>
        private static byte Unpad(int padding, byte value)
        {
            return (byte)(value >> padding << padding);
        }

    }
}
