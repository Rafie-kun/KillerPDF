using System.Buffers.Binary;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Filters.Jbig2;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class Jbig2SegmentHeaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MaximalTruncatedCount_IsRejectedInPageAndGlobals(bool global)
    {
        byte[] header = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5), uint.MaxValue);
        PdfStream? globals = global ? new PdfStream(new PdfDictionary([]), header) : null;
        long before = GC.GetAllocatedBytesForCurrentThread();
        PdfFilterException error = Assert.Throws<PdfFilterException>(() =>
            PdfJbig2Decoder.Decode(header, globals, _ => header, 1, null, null));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Contains("truncated", error.Message, StringComparison.Ordinal);
        Assert.True(allocated < 128 * 1024, $"Maximal truncated header allocated {allocated} bytes.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ManyHeaders_ConsumeBudgetInPageAndGlobals(bool global)
    {
        byte[] headers = new byte[11 * 2000];
        for (int offset = 0; offset < headers.Length; offset += 11)
            BinaryPrimitives.WriteInt32BigEndian(headers.AsSpan(offset), offset / 11);
        PdfStream? globals = global ? new PdfStream(new PdfDictionary([]), headers) : null;
        PdfFilterException error = Assert.Throws<PdfFilterException>(() =>
            PdfJbig2Decoder.Decode(headers, globals, _ => headers, 1, null, null));
        Assert.Contains("safety limit", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void ReservedCountFormat_IsRejected(int format)
    {
        byte[] header = new byte[20];
        header[5] = (byte)(format << 5);
        Assert.Throws<Jbig2Exception>(() => new Jbig2Document(new ImageInputStream(header.AsMemory())));
    }

    [Fact]
    public void CompleteReferenceTable_UsesCumulativeBudget()
    {
        const int count = 100;
        byte[] header = new byte[9 + (count + 8) / 8 + count + 5];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5), 0xe0000000 | count);
        using IDisposable budget = Jbig2Bitmap.BeginAllocationLimit(1, 1024);
        Jbig2Exception error = Assert.Throws<Jbig2Exception>(() =>
            new Jbig2Document(new ImageInputStream(header.AsMemory())));
        Assert.Contains("safety limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteLongReferenceTable_StillParsesWithinBudget()
    {
        const int count = 100;
        byte[] header = new byte[11 + 9 + (count + 8) / 8 + count + 5];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(11), 1);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(16), 0xe0000000 | count);
        using IDisposable budget = Jbig2Bitmap.BeginAllocationLimit(1, 3072);
        using var document = new Jbig2Document(new ImageInputStream(header.AsMemory()));
        Assert.Equal(count, document.GetGlobalSegment(1).RtSegments.Length);
    }

    [Theory]
    [InlineData(256, 1)]
    [InlineData(257, 2)]
    [InlineData(65536, 2)]
    [InlineData(65537, 4)]
    public void ReferenceWidths_PreserveHeaderAlignment(int segmentNumber, int width)
    {
        byte[] header = new byte[22 + width];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(11), segmentNumber);
        header[16] = 0x20;
        using var document = new Jbig2Document(new ImageInputStream(header.AsMemory()));
        Assert.Single(document.GetGlobalSegment(segmentNumber).RtSegments);
    }

    [Fact]
    public void AuxiliaryAllocations_AndBitmapsShareBudgetAcrossNestedScopes()
    {
        using IDisposable outer = Jbig2Bitmap.BeginAllocationLimit(8, 32);
        using (Jbig2Bitmap.BeginAllocationLimit(8, 32))
            Jbig2Bitmap.ReserveAuxiliary(25);
        Assert.Throws<Jbig2Exception>(() => new Jbig2Bitmap(64, 1));
    }

    [Fact]
    public void TruncatedLongCount_DoesNotAllocateFromUntrustedCount()
    {
        byte[] header = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(5), 0xe0100000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Exception? error = Record.Exception(() =>
        {
            using var document = new Jbig2Document(new ImageInputStream(header.AsMemory()));
        });
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotNull(error);
        Assert.True(allocated < 128 * 1024, $"Truncated header allocated {allocated} bytes.");
    }
}
