using System.Text;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class PdfAscii85TupleRecoveryTests
{
    [Theory]
    [InlineData("ASCII85Decode", false)]
    [InlineData("ASCII85Decode", true)]
    [InlineData("A85", false)]
    [InlineData("A85", true)]
    public void CompatibilityDropsLoneFinalDigitAndKeepsCompleteTuple(string filter, bool terminated)
    {
        byte[] encoded = Encoding.ASCII.GetBytes("!<N?+!" + (terminated ? "~>" : ""));
        var stream = Stream(encoded, filter);
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream));
        Assert.Equal(encoded, stream.EncodedData.ToArray());
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream, 3));

        var complete = Stream(Encoding.ASCII.GetBytes("!<N?+" + (terminated ? "~>" : "")), filter);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, PdfStreamDecoder.DecodeWithCompatibilityRecovery(complete));
        if (terminated) Assert.Equal(new byte[] { 1, 2, 3, 4 }, PdfStreamDecoder.Decode(complete));
        else Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(complete));
    }

    [Theory]
    [InlineData("!")]
    [InlineData("!~>")]
    public void LoneDigitDoesNotInventDecodedBytes(string text)
    {
        PdfStream stream = Stream(Encoding.ASCII.GetBytes(text), "ASCII85Decode");
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
        Assert.Empty(PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream));
    }

    [Theory]
    [InlineData("~")]
    [InlineData("!<N?+~")]
    [InlineData("v")]
    [InlineData("!<N?+v")]
    public void InvalidFinalByteStillFailsInCompatibilityMode(string text)
    {
        PdfStream stream = Stream(Encoding.ASCII.GetBytes(text), "ASCII85Decode");
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream));
    }

    private static PdfStream Stream(byte[] data, string filter) => new(new PdfDictionary([
        new KeyValuePair<PdfName, PdfObject>(new PdfName("Filter"u8),
            new PdfName(Encoding.ASCII.GetBytes(filter)))]), data);
}
