using System.Buffers.Binary;
using KillerPdf.Engine.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Fonts;

public sealed class PdfCffRejectedInputAllocationTests
{
    [Theory]
    [InlineData(false, 0, 4)]
    [InlineData(false, 2, 4)]
    [InlineData(false, 1, 3)]
    [InlineData(true, 0, 4)]
    [InlineData(true, 2, 4)]
    [InlineData(true, 1, 3)]
    public void RejectedHeaderDoesNotCopyLargeFont(bool container, byte version, byte headerSize)
    {
        byte[] source = new byte[2 * 1024 * 1024];
        int offset = container ? 28 : 0;
        if (container)
        {
            "OTTO"u8.CopyTo(source);
            source[5] = 1;
            "CFF "u8.CopyTo(source.AsSpan(12));
            BinaryPrimitives.WriteUInt32BigEndian(source.AsSpan(20), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(source.AsSpan(24), (uint)(source.Length - offset));
        }
        source[offset] = version;
        source[offset + 2] = headerSize;
        Assert.Null(PdfCffGlyphReader.TryRead(source));

        long before = GC.GetAllocatedBytesForCurrentThread();
        PdfCffGlyphReader? result = PdfCffGlyphReader.TryRead(source);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Null(result);
        // A rejected format may allocate bounded reader/error state, but not font-sized storage.
        Assert.InRange(allocated, 0, 64 * 1024);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptedFontOwnsItsBytesAfterHeaderCheck(bool container)
    {
        byte[] cff = PdfCffGlyphReaderTests.Build(
            [.. PdfCffGlyphReaderTests.Numbers(10, 20), 21,
                .. PdfCffGlyphReaderTests.Numbers(100, 0), 5, 14]);
        byte[] source = container ? new byte[28 + cff.Length] : cff;
        if (container)
        {
            "OTTO"u8.CopyTo(source);
            source[5] = 1;
            "CFF "u8.CopyTo(source.AsSpan(12));
            BinaryPrimitives.WriteUInt32BigEndian(source.AsSpan(20), 28);
            BinaryPrimitives.WriteUInt32BigEndian(source.AsSpan(24), (uint)cff.Length);
            cff.CopyTo(source, 28);
        }
        var reader = Assert.IsType<PdfCffGlyphReader>(PdfCffGlyphReader.TryRead(source));
        source.AsSpan().Clear();

        Assert.Equal(new PdfGlyphBounds(10, 20, 110, 20), reader.GetBounds(1));
        Assert.NotEmpty(Assert.IsType<PdfGlyphOutline>(reader.GetOutline(1)).Contours);
    }
}
