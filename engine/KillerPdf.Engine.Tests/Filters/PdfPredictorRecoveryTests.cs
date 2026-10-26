using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class PdfPredictorRecoveryTests
{
    [Fact]
    public void CompatibilityPngRetainsCompleteRowsBeforePartialRow()
    {
        PdfStream stream = Flate([1, 10, 10, 10, 2, 1, 2, 3, 4, 99], 15, 3);

        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
        Assert.Equal(new byte[] { 10, 20, 30, 11, 22, 33 },
            PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream));
    }

    [Fact]
    public void CompatibilityTiffRetainsCompleteRowsBeforePartialRow()
    {
        PdfStream stream = Flate([10, 20, 30, 1, 2, 3, 40, 50, 60, 4, 5, 6, 99], 2, 2, 3);

        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
        Assert.Equal(new byte[] { 10, 20, 30, 11, 22, 33, 40, 50, 60, 44, 55, 66 },
            PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream));
    }

    public static TheoryData<int, int, int, int, byte[], byte[]> SampleRows => new()
    {
        { 2, 9, 1, 1, [0x80, 0], [0xFF, 0x80] },
        { 2, 5, 1, 2, [0x40, 0], [0x55, 0x40] },
        { 2, 3, 1, 4, [0x10, 0], [0x11, 0x10] },
        { 2, 2, 3, 8, [10, 20, 30, 1, 2, 3], [10, 20, 30, 11, 22, 33] },
        { 2, 2, 2, 16, [0, 10, 0, 20, 0, 1, 0, 2], [0, 10, 0, 20, 0, 11, 0, 22] },
        { 15, 9, 1, 1, [1, 0xAA, 0x0F], [0xAA, 0xB9] },
        { 15, 5, 1, 2, [1, 0xAA, 0x0F], [0xAA, 0xB9] },
        { 15, 3, 1, 4, [1, 0xAA, 0x0F], [0xAA, 0xB9] },
        { 15, 2, 3, 8, [1, 10, 20, 30, 1, 2, 3], [10, 20, 30, 11, 22, 33] },
        { 15, 2, 2, 16, [1, 0, 10, 0, 20, 0, 1, 0, 2], [0, 10, 0, 20, 0, 11, 0, 22] }
    };

    [Theory]
    [MemberData(nameof(SampleRows))]
    public void RecoveryHonorsSamplePackingAndRetainsOnlyWholeRows(
        int predictor, int columns, int colors, int bits, byte[] row, byte[] expected)
    {
        PdfStream whole = Flate(row, predictor, columns, colors, bits);
        PdfStream partial = Flate([.. row, 99], predictor, columns, colors, bits);
        PdfStream noWholeRow = Flate([99], predictor, columns, colors, bits);
        byte[] original = partial.EncodedData.ToArray();

        Assert.Equal(expected, PdfStreamDecoder.Decode(whole));
        Assert.Equal(expected, PdfStreamDecoder.DecodeWithCompatibilityRecovery(whole));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(partial));
        Assert.Equal(expected, PdfStreamDecoder.DecodeWithCompatibilityRecovery(partial));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(noWholeRow));
        Assert.Empty(PdfStreamDecoder.DecodeWithCompatibilityRecovery(noWholeRow));
        Assert.Equal(original, partial.EncodedData.ToArray());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(15)]
    public void RecoveryOutputLimitNeverReturnsAnIncompleteRow(int predictor)
    {
        byte[] raw = predictor == 2 ? [10, 10, 10, 20, 20, 20] : [1, 10, 10, 10, 1, 20, 20, 20];
        PdfStream stream = Flate(raw, predictor, 3);

        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream, 4));
        Assert.Equal(new byte[] { 10, 20, 30 },
            PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream, 4));
        Assert.Empty(PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream, 2));
        Assert.Empty(PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream, 0));
    }

    [Fact]
    public void CorruptFlateChecksumStillRetainsCompletePredictorRows()
    {
        byte[] row = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20];
        PdfStream valid = Flate([.. row, .. row, .. row, .. row, 4, 99], 15, 20);
        byte[] corrupt = valid.EncodedData.ToArray();
        corrupt[^1] ^= 0xFF;
        var stream = new PdfStream(valid.Dictionary, corrupt);

        PdfFilterException strict = Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
        Assert.Contains("invalid zlib data", strict.Message, StringComparison.Ordinal);
        Assert.Equal(new byte[] {
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20,
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20,
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20,
            1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20 },
            PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream));
    }

    [Fact]
    public void RecoveryStillRejectsUnknownFilterInACompletePngRow()
    {
        PdfStream stream = Flate([5, 10, 20, 30, 0, 99], 15, 3);
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream));
    }

    private static PdfStream Flate(byte[] data, int predictor, int columns,
        int colors = 1, int bits = 8)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(data);
        return new PdfStream(new PdfDictionary([
            Pair("Filter", new PdfName("FlateDecode"u8)),
            Pair("DecodeParms", new PdfDictionary([
                Pair("Predictor", new PdfInteger(predictor)),
                Pair("Columns", new PdfInteger(columns)),
                Pair("Colors", new PdfInteger(colors)),
                Pair("BitsPerComponent", new PdfInteger(bits))]))]), output.ToArray());
    }

    private static KeyValuePair<PdfName, PdfObject> Pair(string key, PdfObject value)
        => new(new PdfName(Encoding.ASCII.GetBytes(key)), value);
}
