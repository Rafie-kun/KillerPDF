using System.Text;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class PdfCcittRecoveryTests
{
    [Fact]
    public void ExplicitZeroRowsUsesTheImageHeightLikeOmittedRows()
    {
        byte[] data = [0x89, 0xC0, 0x89, 0xC0];
        var parameters = new PdfDictionary([
            Pair("Columns", new PdfInteger(8)), Pair("EncodedByteAlign", new PdfBoolean(true))]);
        PdfStream Stream(PdfDictionary value) => new(new PdfDictionary([
            Pair("Filter", Name("CCITTFaxDecode")), Pair("Height", new PdfInteger(2)),
            Pair("DecodeParms", value)]), data);
        var control = Stream(parameters);
        Assert.Equal(new byte[] { 0xE3, 0xE3 }, PdfStreamDecoder.Decode(control));
        Assert.Equal(new byte[] { 0xE3, 0xE3 }, PdfStreamDecoder.DecodeWithCompatibilityRecovery(control));
        var explicitZero = Stream(new PdfDictionary(parameters.Append(Pair("Rows", new PdfInteger(0)))));
        Assert.Equal(new byte[] { 0xE3, 0xE3 }, PdfStreamDecoder.Decode(explicitZero));
        Assert.Equal(new byte[] { 0xE3, 0xE3 }, PdfStreamDecoder.DecodeWithCompatibilityRecovery(explicitZero));
        Assert.Equal(data, explicitZero.EncodedData.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroRowsStillHonorsPolarityAndOutputBounds(bool blackIsOne)
    {
        var stream = new PdfStream(new PdfDictionary([
            Pair("Filter", Name("CCF")), Pair("Height", new PdfInteger(1)),
            Pair("DecodeParms", new PdfDictionary([
                Pair("Columns", new PdfInteger(8)), Pair("Rows", new PdfInteger(0)),
                Pair("BlackIs1", new PdfBoolean(blackIsOne))]))]), new byte[] { 0x89, 0xC0 });
        byte[] expected = blackIsOne ? [0x1C] : [0xE3];
        Assert.Equal(expected, PdfStreamDecoder.Decode(stream, 1));
        Assert.Equal(expected, PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream, 1));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream, 0));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream, 0));
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(0, 0)]
    [InlineData(0, -1)]
    public void InvalidFaxDimensionsRemainRejected(int rows, int height)
    {
        var stream = new PdfStream(new PdfDictionary([
            Pair("Filter", Name("CCF")), Pair("Height", new PdfInteger(height)),
            Pair("DecodeParms", new PdfDictionary([
                Pair("Columns", new PdfInteger(8)), Pair("Rows", new PdfInteger(rows))]))]),
            new byte[] { 0x89, 0xC0 });
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompatibilityRetainsCompleteRowsAndWhitesTheMissingTail(bool blackIsOne)
    {
        byte[] data = [0x89, 0xC0];
        var stream = new PdfStream(new PdfDictionary([
            Pair("Filter", Name("CCF")),
            Pair("DecodeParms", new PdfDictionary([
                Pair("Columns", new PdfInteger(8)), Pair("Rows", new PdfInteger(3)),
                Pair("EncodedByteAlign", new PdfBoolean(true)),
                Pair("BlackIs1", new PdfBoolean(blackIsOne))]))]), data);
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
        byte[] expected = blackIsOne ? [0x1C, 0, 0] : [0xE3, 0xFF, 0xFF];
        Assert.Equal(expected, PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream, 2));
        Assert.Equal(data, stream.EncodedData.ToArray());
    }

    [Fact]
    public void RecoveryDiscardsAnIncompleteRowEvenAfterItPaintsBlackPixels()
    {
        // First row is E3. Next row has white zero, black four, then an unfinished white run.
        byte[] data = [0x89, 0xC0, 0x35, 0x60];
        var stream = new PdfStream(new PdfDictionary([
            Pair("Filter", Name("CCF")),
            Pair("DecodeParms", new PdfDictionary([
                Pair("Columns", new PdfInteger(8)), Pair("Rows", new PdfInteger(3)),
                Pair("EncodedByteAlign", new PdfBoolean(true))]))]), data);
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
        Assert.Equal(new byte[] { 0xE3, 0xFF, 0xFF },
            PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream));
        Assert.Equal(data, stream.EncodedData.ToArray());
    }

    [Theory]
    [InlineData("")]
    [InlineData("35")]
    [InlineData("0000")]
    public void RecoveryRejectsInputWithoutAnyCompleteRow(string hex)
    {
        var stream = new PdfStream(new PdfDictionary([
            Pair("Filter", Name("CCF")),
            Pair("DecodeParms", new PdfDictionary([
                Pair("Columns", new PdfInteger(8)), Pair("Rows", new PdfInteger(3)),
                Pair("EncodedByteAlign", new PdfBoolean(true))]))]), Convert.FromHexString(hex));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream));
    }

    [Theory]
    [InlineData(-1, "0011000101", 2)]
    [InlineData(0, "1000100111", 6)]
    public void CompatibilityHonorsEarlyFaxEndMarkers(int k, string rowBits, int markerCount)
    {
        string bits = rowBits + string.Concat(Enumerable.Repeat("000000000001", markerCount));
        bits = bits.PadRight((bits.Length + 7) / 8 * 8, '0');
        byte[] data = Enumerable.Range(0, bits.Length / 8)
            .Select(index => Convert.ToByte(bits.Substring(index * 8, 8), 2)).ToArray();
        PdfStream Stream(int rows, bool endOfBlock = true) => new(new PdfDictionary([
            Pair("Filter", Name("CCF")),
            Pair("DecodeParms", new PdfDictionary([
                Pair("Columns", new PdfInteger(8)), Pair("Rows", new PdfInteger(rows)),
                Pair("K", new PdfInteger(k)), Pair("EndOfBlock", new PdfBoolean(endOfBlock))]))]), data);
        Assert.Equal(new byte[] { 0xE3 }, PdfStreamDecoder.Decode(Stream(1)));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(Stream(3)));
        Assert.Equal(new byte[] { 0xE3, 0xFF, 0xFF },
            PdfStreamDecoder.DecodeWithCompatibilityRecovery(Stream(3)));
        Assert.Throws<PdfFilterException>(() =>
            PdfStreamDecoder.DecodeWithCompatibilityRecovery(Stream(3, false)));
    }

    [Theory]
    [InlineData(-1, "98A0", false, 2, "FFE3", "FFE3FFFF")]
    [InlineData(1, "001C4E", true, 1, "E3", "E3FFFFFF")]
    public void TruncationRecoveryPreservesTwoDimensionalAndMixedRows(
        int k, string encoded, bool endOfLine, int completeRows, string complete, string recovered)
    {
        byte[] data = Convert.FromHexString(encoded);
        PdfStream Stream(int rows) => new(new PdfDictionary([
            Pair("Filter", Name("CCF")),
            Pair("DecodeParms", new PdfDictionary([
                Pair("Columns", new PdfInteger(8)), Pair("Rows", new PdfInteger(rows)),
                Pair("K", new PdfInteger(k)), Pair("EndOfLine", new PdfBoolean(endOfLine))]))]), data);
        Assert.Equal(Convert.FromHexString(complete), PdfStreamDecoder.Decode(Stream(completeRows)));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(Stream(4)));
        Assert.Equal(Convert.FromHexString(recovered),
            PdfStreamDecoder.DecodeWithCompatibilityRecovery(Stream(4)));
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Pair(string key, PdfObject value) => new(Name(key), value);
}
