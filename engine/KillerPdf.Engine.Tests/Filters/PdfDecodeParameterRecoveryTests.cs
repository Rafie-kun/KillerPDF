using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class PdfDecodeParameterRecoveryTests
{
    [Theory]
    [InlineData(false, "FlateDecode")]
    [InlineData(true, "FlateDecode")]
    [InlineData(false, "Fl")]
    [InlineData(true, "Fl")]
    public void CompatibilityBindsSinglePredictorDictionaryToMatchingFilter(bool flateFirst, string filter)
    {
        PdfDictionary parameters = new([
            Pair("Predictor", new PdfInteger(flateFirst ? 2 : 15)),
            Pair("Columns", new PdfInteger(flateFirst ? 7 : 3))]);
        byte[] encoded = Compress(flateFirst ? [52, 253, 3, 254, 2, 255, 11] : [1, 10, 10, 10]);
        PdfObject[] filters = flateFirst
            ? [Name(filter), Name("ASCIIHexDecode")]
            : [Name("ASCIIHexDecode"), Name(filter)];
        byte[] data = flateFirst ? encoded : Encoding.ASCII.GetBytes(Convert.ToHexString(encoded) + ">");
        byte[] expected = flateFirst ? [65, 66, 67] : [10, 20, 30];
        var malformed = new PdfStream(new PdfDictionary([
            Pair("Filter", new PdfArray(filters)), Pair("DecodeParms", parameters)]), data);

        PdfObject[] correctParameters = flateFirst
            ? [parameters, PdfNull.Instance] : [PdfNull.Instance, parameters];
        var control = new PdfStream(new PdfDictionary([
            Pair("Filter", new PdfArray(filters)), Pair("DecodeParms", new PdfArray(correctParameters))]), data);
        Assert.Equal(expected, PdfStreamDecoder.Decode(control));
        Assert.Equal(expected, PdfStreamDecoder.DecodeWithCompatibilityRecovery(control));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(malformed));
        Assert.Equal(expected, PdfStreamDecoder.DecodeWithCompatibilityRecovery(malformed));
        if (flateFirst)
            Assert.Throws<PdfFilterException>(() =>
                PdfStreamDecoder.DecodeWithCompatibilityRecovery(malformed, expected.Length - 1));
        else
            Assert.Empty(PdfStreamDecoder.DecodeWithCompatibilityRecovery(malformed, expected.Length - 1));
        Assert.Equal(data, malformed.EncodedData.ToArray());
    }

    [Theory]
    [InlineData("FlateDecode", "FlateDecode", "Predictor")]
    [InlineData("FlateDecode", "LZWDecode", "Predictor")]
    [InlineData("ASCIIHexDecode", "RunLengthDecode", "Predictor")]
    [InlineData("ASCIIHexDecode", "FlateDecode", "EarlyChange")]
    [InlineData("ASCIIHexDecode", "FlateDecode", "Columns")]
    [InlineData("ASCIIHexDecode", "DCTDecode", "Predictor")]
    public void AmbiguousOrUnrelatedSingleParametersRemainRejected(
        string first, string second, string key)
    {
        var parameters = new PdfDictionary([Pair(key, new PdfInteger(1))]);
        var stream = new PdfStream(new PdfDictionary([
            Pair("Filter", new PdfArray([Name(first), Name(second)])),
            Pair("DecodeParms", parameters)]), new byte[] { 0 });
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream));
        Assert.Equal(new byte[] { 0 }, stream.EncodedData.ToArray());
    }

    [Theory]
    [InlineData("LZWDecode")]
    [InlineData("LZW")]
    public void CompatibilityBindsPredictorDictionaryToLzw(string filter)
    {
        // Nine-bit codes: clear, 1, 10, 10, 10, end. PNG Sub reconstructs 10, 20, 30.
        byte[] data = "80004140A05404>"u8.ToArray();
        var parameters = new PdfDictionary([
            Pair("Predictor", new PdfInteger(15)), Pair("Columns", new PdfInteger(3))]);
        var filters = new PdfArray([Name("ASCIIHexDecode"), Name(filter)]);
        var control = new PdfStream(new PdfDictionary([
            Pair("Filter", filters),
            Pair("DecodeParms", new PdfArray([PdfNull.Instance, parameters]))]), data);
        var malformed = new PdfStream(new PdfDictionary([
            Pair("Filter", filters), Pair("DecodeParms", parameters)]), data);
        Assert.Equal(new byte[] { 10, 20, 30 }, PdfStreamDecoder.Decode(control));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(malformed));
        Assert.Equal(new byte[] { 10, 20, 30 }, PdfStreamDecoder.DecodeWithCompatibilityRecovery(malformed));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.DecodeWithCompatibilityRecovery(malformed, 2));
        Assert.Equal(data, malformed.EncodedData.ToArray());
    }

    private static byte[] Compress(byte[] source)
    {
        using var output = new MemoryStream();
        using (var stream = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            stream.Write(source);
        return output.ToArray();
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Pair(string key, PdfObject value) => new(Name(key), value);
}
