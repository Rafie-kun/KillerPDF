using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class PdfJpegRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompatibilityRetainsCompleteBaselineRowsBeforeEntropyTruncation(bool marker)
    {
        byte[] source = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/wAALCAAQABABAREA/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/9oACAEBAAA/AP4f6/YCv9ACv2Ar/9k=");
        PdfStream Stream(byte[] bytes) => new(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(new PdfName("Filter"u8), new PdfName("DCTDecode"u8))]), bytes);
        byte[] complete = PdfStreamDecoder.Decode(Stream(source), 256);
        Assert.Equal(256, complete.Length);
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
            Assert.Equal(y < 8 ? (byte)(x < 8 ? 16 : 64) : (byte)(x < 8 ? 192 : 240), complete[y * 16 + x]);

        byte[] truncated = marker ? [.. source.AsSpan(0, 334), 0xFF, 0xD9] : source[..334];
        byte[] original = truncated.ToArray();
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(Stream(truncated), 256));
        byte[] recovered = PdfStreamDecoder.DecodeWithCompatibilityRecovery(Stream(truncated), 256);
        Assert.Equal(256, recovered.Length);
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
            Assert.Equal(y < 8 ? (byte)(x < 8 ? 16 : 64) : (byte)128, recovered[y * 16 + x]);
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.DecodeWithCompatibilityRecovery(Stream(truncated), 255));
        Assert.Equal(original, truncated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterleavedRecoveryKeepsCompleteColorRowsAndNeutralTail(bool progressive)
    {
        byte[] complete = ColorFixture(false, progressive);
        byte[] truncated = ColorFixture(true, progressive);
        PdfStream Stream(byte[] bytes) => new(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(new PdfName("Filter"u8), new PdfName("DCTDecode"u8))]), bytes);
        byte[] control = PdfStreamDecoder.Decode(Stream(complete), 384);
        Assert.Equal(384, control.Length);
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 8; x++)
            Assert.Equal(y < 8 ? new byte[] { 64, 80, 96 } : new byte[] { 192, 208, 224 },
                control.AsSpan((y * 8 + x) * 3, 3).ToArray());
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(Stream(truncated), 384));
        byte[] recovered = PdfStreamDecoder.DecodeWithCompatibilityRecovery(Stream(truncated), 384);
        Assert.Equal(384, recovered.Length);
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 8; x++)
            Assert.Equal(y < 8 ? new byte[] { 64, 80, 96 } : new byte[] { 128, 128, 128 },
                recovered.AsSpan((y * 8 + x) * 3, 3).ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(139)]
    public void CompatibilityPreservesDecodedProgressiveContent(int cutoff)
    {
        byte[] source = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/wgALCAAQABABAREA/8QAFgABAQEAAAAAAAAAAAAAAAAACAkK/9oACAEBAAAAAYfsDQAwP//EABQQAQAAAAAAAAAAAAAAAAAAACD/2gAIAQEAAQUCH//EABQQAQAAAAAAAAAAAAAAAAAAACD/2gAIAQEABj8CH//EABQQAQAAAAAAAAAAAAAAAAAAACD/2gAIAQEAAT8hH//aAAgBAQAAABAP/8QAFBABAAAAAAAAAAAAAAAAAAAAIP/aAAgBAQABPxAf/9k=");
        PdfStream Stream(byte[] bytes) => new(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(new PdfName("Filter"u8), new PdfName("DCTDecode"u8))]), bytes);
        byte[] complete = PdfStreamDecoder.Decode(Stream(source), 256);
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
            Assert.Equal(y < 8 ? (byte)(x < 8 ? 16 : 64) : (byte)(x < 8 ? 192 : 240), complete[y * 16 + x]);
        byte[] truncated = cutoff == 0 ? source[..^2] : source[..cutoff];
        byte[] original = truncated.ToArray();
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(Stream(truncated), 256));
        byte[] recovered = PdfStreamDecoder.DecodeWithCompatibilityRecovery(Stream(truncated), 256);
        Assert.Equal(256, recovered.Length);
        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
            Assert.Equal(y < 8 ? (byte)(x < 8 ? 16 : 64)
                : cutoff == 0 ? (byte)(x < 8 ? 192 : 240) : (byte)128, recovered[y * 16 + x]);
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.DecodeWithCompatibilityRecovery(Stream(truncated), 255));
        Assert.Equal(original, truncated);
    }

    private static byte[] ColorFixture(bool truncated, bool progressive)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        void Segment(byte marker, byte[] payload)
        {
            bytes.AddRange([0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2)]);
            bytes.AddRange(payload);
        }
        Segment(0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]);
        Segment(progressive ? (byte)0xC2 : (byte)0xC0,
            [8, 0, 16, 0, 8, 3, (byte)'R', 0x11, 0, (byte)'G', 0x11, 0, (byte)'B', 0x11, 0]);
        byte[] dcLengths = new byte[16];
        dcLengths[2] = 4;
        byte[] acLengths = new byte[16];
        acLengths[0] = 1;
        Segment(0xC4, [0, .. dcLengths, 0, 9, 10, 11, 0x10, .. acLengths, 0]);
        Segment(0xDA, [3, (byte)'R', 0, (byte)'G', 0, (byte)'B', 0, 0, progressive ? (byte)0 : (byte)63, 0]);
        // DC-only RGB rows: 64/80/96 then 192/208/224. Byte five completes the first row.
        string entropy = progressive ? "4FF93F97FB800E003801" : "4FF89FC5FE7000E001C007";
        bytes.AddRange(Convert.FromHexString(truncated ? entropy[..10] : entropy));
        if (!truncated) bytes.AddRange([0xFF, 0xD9]);
        return bytes.ToArray();
    }
}
