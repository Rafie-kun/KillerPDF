using System.Text;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class PdfFilterEndOfDataRecoveryTests
{
    [Theory]
    [InlineData("RunLengthDecode")]
    [InlineData("RL")]
    public void RunLength_CompatibilityRecoversCompleteRunsWithoutEndMarker(string filter)
    {
        PdfStream stream = Stream([2, 65, 66, 67, 254, 90], filter);

        Assert.Equal("ABCZZZ"u8.ToArray(),
            PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream));
    }

    [Theory]
    [InlineData("RunLengthDecode", false)]
    [InlineData("RunLengthDecode", true)]
    [InlineData("RL", false)]
    [InlineData("RL", true)]
    public void RunLength_PreservesStrictMarkersLimitsAndSource(string filter, bool ended)
    {
        byte[] encoded = ended ? [2, 65, 66, 67, 254, 90, 128, 129] : [2, 65, 66, 67, 254, 90];
        byte[] original = (byte[])encoded.Clone();
        PdfStream stream = Stream(encoded, filter);

        Assert.Equal("ABCZZZ"u8.ToArray(), PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream, 6));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream, 5));
        if (ended) Assert.Equal("ABCZZZ"u8.ToArray(), PdfStreamDecoder.Decode(stream, 6));
        else Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream, 6));
        Assert.Equal(original, encoded);
        Assert.Equal(original, stream.EncodedData.ToArray());
    }

    [Theory]
    [InlineData("RunLengthDecode")]
    [InlineData("RL")]
    public void RunLength_EmptyInputIsBoundsSafeAndMalformedRunsStillFail(string filter)
    {
        Assert.Empty(PdfStreamDecoder.DecodeWithCompatibilityRecovery(Stream([], filter), 0));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(Stream([], filter), 0));
        Assert.Empty(PdfStreamDecoder.Decode(Stream([128], filter), 0));
        Assert.Empty(PdfStreamDecoder.DecodeWithCompatibilityRecovery(Stream([128], filter), 0));
        foreach (byte[] malformed in new byte[][] { [0], [1, 65], [129], [0, 65, 255] })
        {
            PdfStream stream = Stream(malformed, filter);
            Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
            Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream));
        }
    }

    [Theory]
    [InlineData("LZWDecode", 0)]
    [InlineData("LZWDecode", 1)]
    [InlineData("LZW", 0)]
    [InlineData("LZW", 1)]
    public void Lzw_CompatibilityRecoversFullCodesWithoutEndMarker(string filter, int earlyChange)
    {
        PdfStream stream = Stream(PackCodes((256, 9), (65, 9), (66, 9), (258, 9), (260, 9)),
            filter, earlyChange);

        Assert.Equal("ABABABA"u8.ToArray(), PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream));
    }

    [Theory]
    [InlineData("LZWDecode", 0, false)]
    [InlineData("LZWDecode", 0, true)]
    [InlineData("LZWDecode", 1, false)]
    [InlineData("LZWDecode", 1, true)]
    [InlineData("LZW", 0, false)]
    [InlineData("LZW", 0, true)]
    [InlineData("LZW", 1, false)]
    [InlineData("LZW", 1, true)]
    public void Lzw_PreservesStrictMarkersLimitsAndSource(string filter, int earlyChange, bool ended)
    {
        (int, int)[] codes = [(256, 9), (65, 9), (66, 9), (258, 9), (260, 9)];
        byte[] encoded = PackCodes(ended ? [.. codes, (257, 9), (511, 9)] : codes);
        byte[] original = (byte[])encoded.Clone();
        PdfStream stream = Stream(encoded, filter, earlyChange);

        Assert.Equal("ABABABA"u8.ToArray(), PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream, 7));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream, 6));
        if (ended) Assert.Equal("ABABABA"u8.ToArray(), PdfStreamDecoder.Decode(stream, 7));
        else Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream, 7));
        Assert.Equal(original, encoded);
        Assert.Equal(original, stream.EncodedData.ToArray());
    }

    [Theory]
    [InlineData("LZWDecode", 0)]
    [InlineData("LZWDecode", 1)]
    [InlineData("LZW", 0)]
    [InlineData("LZW", 1)]
    public void Lzw_RecoveryPreservesClearCodesAndRejectsInvalidCodes(string filter, int earlyChange)
    {
        byte[] reset = PackCodes((256, 9), (65, 9), (66, 9), (256, 9), (67, 9));
        Assert.Equal("ABC"u8.ToArray(),
            PdfStreamDecoder.DecodeWithCompatibilityRecovery(Stream(reset, filter, earlyChange)));
        foreach (byte[] invalid in new byte[][]
        {
            PackCodes((256, 9), (258, 9)),
            PackCodes((256, 9), (65, 9), (511, 9)),
            PackCodes((256, 9), (65, 9), (66, 9), (256, 9), (258, 9))
        })
        {
            PdfStream stream = Stream(invalid, filter, earlyChange);
            Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
            Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream));
        }
        PdfStream badParameter = Stream(PackCodes((256, 9), (257, 9)), filter, 2);
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(badParameter));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.DecodeWithCompatibilityRecovery(badParameter));
    }

    [Theory]
    [InlineData("LZWDecode")]
    [InlineData("LZW")]
    public void Lzw_EmptyAndIncompleteCodesPreserveOnlyDecodedPrefix(string filter)
    {
        Assert.Empty(PdfStreamDecoder.DecodeWithCompatibilityRecovery(Stream([], filter), 0));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(Stream([], filter), 0));
        Assert.Empty(PdfStreamDecoder.DecodeWithCompatibilityRecovery(Stream([0x80], filter), 0));
        PdfStream marked = Stream(PackCodes((256, 9), (257, 9)), filter);
        Assert.Empty(PdfStreamDecoder.Decode(marked, 0));
        Assert.Empty(PdfStreamDecoder.DecodeWithCompatibilityRecovery(marked, 0));
        byte[] partial = PackCodes((256, 9), (65, 9), (66, 9))[..^1];
        Assert.Equal("A"u8.ToArray(), PdfStreamDecoder.DecodeWithCompatibilityRecovery(Stream(partial, filter), 1));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(Stream(partial, filter), 1));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.DecodeWithCompatibilityRecovery(Stream(partial, filter), 0));
    }

    [Theory]
    [InlineData("LZWDecode", 0)]
    [InlineData("LZWDecode", 1)]
    [InlineData("LZW", 0)]
    [InlineData("LZW", 1)]
    public void Lzw_RecoveryPreservesEarlyChangeWidthTransition(string filter, int earlyChange)
    {
        // The first 255 literal codes use nine bits for EarlyChange 0; 254 for EarlyChange 1.
        int narrowCount = earlyChange == 0 ? 255 : 254;
        (int, int)[] codes = [(256, 9), .. Enumerable.Repeat((65, 9), narrowCount),
            .. Enumerable.Repeat((65, 10), 260 - narrowCount)];
        byte[] expected = Encoding.ASCII.GetBytes(
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
            "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        foreach (bool ended in new[] { false, true })
        {
            PdfStream stream = Stream(PackCodes(ended ? [.. codes, (257, 10)] : codes), filter, earlyChange);
            Assert.Equal(expected, PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream, 260));
            Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream, 259));
            if (ended) Assert.Equal(expected, PdfStreamDecoder.Decode(stream, 260));
            else Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream, 260));
        }
    }

    private static byte[] PackCodes(params (int Code, int Width)[] codes)
    {
        var bits = new List<int>();
        foreach ((int code, int width) in codes)
            for (int shift = width - 1; shift >= 0; shift--) bits.Add((code >> shift) & 1);
        byte[] packed = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++) packed[i / 8] |= (byte)(bits[i] << (7 - i % 8));
        return packed;
    }

    private static PdfStream Stream(byte[] encoded, string filter, int? earlyChange = null)
    {
        var entries = new List<KeyValuePair<PdfName, PdfObject>>
        {
            new(new PdfName("Filter"u8), new PdfName(Encoding.ASCII.GetBytes(filter)))
        };
        if (earlyChange.HasValue)
            entries.Add(new(new PdfName("DecodeParms"u8), new PdfDictionary([
                new(new PdfName("EarlyChange"u8), new PdfInteger(earlyChange.Value))])));
        return new PdfStream(new PdfDictionary(entries), encoded);
    }
}
