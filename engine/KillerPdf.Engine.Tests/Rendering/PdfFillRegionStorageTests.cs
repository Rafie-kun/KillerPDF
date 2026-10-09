using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

[Collection(nameof(SoftMaskAllocationCollection))]
public sealed class PdfFillRegionStorageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmallRegionDoesNotRentPageSizedFillCoverage(bool evenOdd)
    {
        const int page = 2048, region = 64;
        var renderer = Create(page, "1 0 0 rg 0 0 m 2048 0 l 2048 2048 l h "
            + (evenOdd ? "f*" : "f"));
        var options = new PdfRenderOptions(page, page) { CacheResult = false, MaximumParallelism = 1 };
        byte[] pixels = new byte[region * region * 4];
        Assert.Empty(renderer.RenderRegionInto(0, options, 1600, 1000, region, region, pixels));
        var held = new List<byte[]>();
        try
        {
            for (int index = 0; index < 8; index++)
                held.Add(PdfScratchBuffers.Bytes.Rent(page * page));
            long before = GC.GetAllocatedBytesForCurrentThread();
            var diagnostics = renderer.RenderRegionInto(0, options, 1600, 1000, region, region, pixels);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Empty(diagnostics);
            Assert.True(allocated < 256 * 1024, $"Small fill region allocated {allocated} bytes.");
            for (int offset = 0; offset < pixels.Length; offset += 4)
                Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels.AsSpan(offset, 4).ToArray());
        }
        finally
        {
            foreach (byte[] buffer in held) PdfScratchBuffers.Bytes.Return(buffer);
        }
    }

    public static TheoryData<bool, int, string> Cases()
    {
        var data = new TheoryData<bool, int, string>();
        foreach (bool transparent in new[] { false, true })
        foreach (int parallelism in new[] { 1, 4 })
        foreach (string path in new[]
        {
            "-8.3 -9.7 m 73.2 12.1 l 17.6 78.4 l h f",
            "-8.3 -9.7 m 73.2 12.1 l 17.6 78.4 l h f*",
            "-8 20 m 10 100 90 -40 75 40 c 12 3 l h f",
            "-8 -8 m 72 -8 l 72 72 l -8 72 l h 17.3 19.7 m 47.1 19.7 l 47.1 46.2 l 17.3 46.2 l h f*",
            "-8 -8 m 72 -8 l 72 72 l -8 72 l h 17.3 19.7 m 17.3 46.2 l 47.1 46.2 l 47.1 19.7 l h f",
            "3.2 4.1 m 58.7 17.6 l 18.4 61.3 l h W n -8 20 m 10 100 90 -40 75 40 c 12 3 l h f",
            "3.2 4.1 m 58.7 17.6 l 18.4 61.3 l h W n 3.2 4.1 m 58.7 17.6 l 18.4 61.3 l h f",
            "-8 20 m 10 100 90 -40 75 40 c 12 3 l h W n -8 20 m 10 100 90 -40 75 40 c 12 3 l h f"
        })
            data.Add(transparent, parallelism, path);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RegionFillsPreserveFullPageCoverageAndWinding(bool transparent, int parallelism, string path)
    {
        var renderer = Create(64, "0.2 0.7 0.4 rg " + path);
        var options = new PdfRenderOptions(127, 129, transparentBackground: transparent)
        {
            CacheResult = false, MaximumParallelism = parallelism
        };
        var full = renderer.Render(0, options);
        Assert.Empty(full.Diagnostics);
        foreach (int top in new[] { 0, 19, 57, 96 })
        foreach (int left in new[] { 0, 17, 61, 94 })
        {
            byte[] pixels = new byte[33 * 33 * 4 + 13];
            Array.Fill(pixels, (byte)73);
            Assert.Empty(renderer.RenderRegionInto(0, options, left, top, 33, 33, pixels));
            for (int row = 0; row < 33; row++)
                Assert.Equal(full.Pixels.Slice(((top + row) * 127 + left) * 4, 33 * 4).ToArray(),
                    pixels.AsSpan(row * 33 * 4, 33 * 4).ToArray());
            Assert.All(pixels.Skip(33 * 33 * 4), value => Assert.Equal((byte)73, value));
        }
    }

    private static PdfPageRenderer Create(int size, string content) => new(PdfDocument.Open(
        new PdfDocumentBuilder().AddPage(size, size, Encoding.ASCII.GetBytes(content)).Build()));
}
