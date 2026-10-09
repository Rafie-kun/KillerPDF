using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Filters.Jbig2;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class Jbig2BlitTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 0)]
    [InlineData(0, 3)]
    [InlineData(10, 3)]
    [InlineData(23, 0)]
    [InlineData(0, 5)]
    public void DecodedGenericRegionClipsToDeclaredPagePixels(int x, int y)
    {
        const int sourceWidth = 17, sourceHeight = 4, width = 23, height = 5;
        byte[] pixels = new byte[sourceWidth * sourceHeight];
        for (int row = 0; row < sourceHeight; row++)
        for (int column = 0; column < sourceWidth; column++)
            pixels[row * sourceWidth + column] = (byte)((column * 3 + row * 5) % 7 < 3 ? 0 : 255);
        var page = new List<byte>();
        UInt(page, width); UInt(page, height); UInt(page, 0); UInt(page, 0);
        page.AddRange([1, 0, 0]);
        var region = new List<byte>();
        UInt(region, sourceWidth); UInt(region, sourceHeight); UInt(region, (uint)x); UInt(region, (uint)y);
        region.AddRange([0, 1]); // OR composition and MMR generic-region coding.
        region.AddRange(PdfCcittGroup4Encoder.Encode(sourceWidth, sourceHeight, pixels));
        var encoded = new List<byte>();
        Segment(encoded, 1, 48, page);
        Segment(encoded, 2, 39, region);
        Segment(encoded, 3, 49, []);
        static PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
        var stream = new PdfStream(new PdfDictionary([
            new(Name("Filter"), Name("JBIG2Decode")),
            new(Name("Width"), new PdfInteger(width)), new(Name("Height"), new PdfInteger(height))]),
            encoded.ToArray());
        byte[] expected = [.. Enumerable.Repeat((byte)255, ((width + 7) / 8) * height)];
        for (int row = 0; row < sourceHeight; row++)
        for (int column = 0; column < sourceWidth; column++)
        {
            int targetX = x + column, targetY = y + row;
            if (targetX >= width || targetY >= height || pixels[row * sourceWidth + column] != 0) continue;
            expected[targetY * ((width + 7) / 8) + (targetX >> 3)] &= (byte)~(1 << (7 - (targetX & 7)));
        }
        Assert.Equal(expected, PdfStreamDecoder.Decode(stream, expected.Length));

        static void UInt(List<byte> output, uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            output.AddRange(bytes.ToArray());
        }
        static void Segment(List<byte> output, uint number, byte type, List<byte> data)
        {
            UInt(output, number);
            output.AddRange([type, 0, 1]);
            UInt(output, (uint)data.Count);
            output.AddRange(data);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void BlitMatchesPixelOperationsAtEveryBoundary(int operatorCode)
    {
        var random = new Random(17391);
        foreach (int targetWidth in new[] { 13, 521 })
        foreach (int sourceWidth in new[] { 1, 7, 8, 9, 17, 31, 256, 519, 1025 })
        foreach (int x in new[] { -32, -17, -9, -8, -7, -6, -5, -4, -3, -2, -1,
            0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 13, int.MinValue, int.MaxValue })
        foreach (int y in new[] { -5, -2, -1, 0, 1, 3, 4, 5, int.MinValue, int.MaxValue })
        {
            var source = new Jbig2Bitmap(sourceWidth, 4);
            var actual = new Jbig2Bitmap(targetWidth, 5);
            random.NextBytes(source.ByteArray);
            random.NextBytes(actual.ByteArray);
            byte[] expected = (byte[])actual.ByteArray.Clone();
            for (int sourceY = 0; sourceY < source.Height; sourceY++)
            for (int sourceX = 0; sourceX < source.Width; sourceX++)
            {
                long targetX = (long)x + sourceX, targetY = (long)y + sourceY;
                if (targetX < 0 || targetX >= actual.Width || targetY < 0 || targetY >= actual.Height) continue;
                int offset = (int)targetY * actual.RowStride + ((int)targetX >> 3);
                int mask = 1 << (7 - ((int)targetX & 7));
                bool oldBit = (expected[offset] & mask) != 0;
                bool newBit = (source.ByteArray[sourceY * source.RowStride + (sourceX >> 3)]
                    & (1 << (7 - (sourceX & 7)))) != 0;
                bool result = operatorCode switch
                {
                    0 => oldBit || newBit,
                    1 => oldBit && newBit,
                    2 => oldBit != newBit,
                    3 => oldBit == newBit,
                    _ => newBit
                };
                expected[offset] = (byte)(result ? expected[offset] | mask : expected[offset] & ~mask);
            }
            Exception? error = Record.Exception(() => Jbig2Bitmaps.Blit(source, actual, x, y,
                (CombinationOperator)operatorCode));
            string location = $"Operator {operatorCode}, source width {sourceWidth}, target width {targetWidth}, placement {x},{y}.";
            Assert.True(error is null, location + " " + error);
            Assert.True(expected.AsSpan().SequenceEqual(actual.ByteArray), location);
        }
    }
}
