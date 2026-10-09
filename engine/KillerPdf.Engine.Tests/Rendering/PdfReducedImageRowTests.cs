using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

[Collection(nameof(SoftMaskAllocationCollection))]
public sealed class PdfReducedImageRowTests
{
    [Theory]
    [InlineData(false, false, 89, 67)]
    [InlineData(false, true, 89, 67)]
    [InlineData(true, false, 89, 67)]
    [InlineData(true, true, 89, 67)]
    [InlineData(false, false, 37, 31)]
    [InlineData(false, true, 37, 31)]
    [InlineData(true, false, 37, 31)]
    [InlineData(true, true, 37, 31)]
    public void NarrowRegionsPreserveFractionalSamplingAndReflection(bool flipX,
        bool flipY, int width, int height)
    {
        var renderer = new PdfPageRenderer(CreateWideRgb(129, 99, Samples(129, 99), flipX, flipY));
        var options = new PdfRenderOptions(width, height, transparentBackground: true)
            { CacheResult = false, MaximumParallelism = 4 };
        var full = renderer.Render(0, options);
        Assert.Empty(full.Diagnostics);
        foreach (int left in new[] { 0, width / 2, width - 7 })
        foreach (int top in new[] { 0, height / 2, height - 9 })
        {
            byte[] pixels = new byte[7 * 9 * 4];
            Assert.Empty(renderer.RenderRegionInto(0, options, left, top, 7, 9, pixels));
            for (int row = 0; row < 9; row++)
                Assert.True(full.Pixels.Span.Slice(((top + row) * width + left) * 4, 7 * 4)
                    .SequenceEqual(pixels.AsSpan(row * 7 * 4, 7 * 4)));
        }
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 1)]
    [InlineData(false, false, 4)]
    [InlineData(true, true, 4)]
    public void ReducedRowsPreserveSourceAveragesAndReflection(bool flipX, bool flipY,
        int workers)
    {
        const int width = 128, height = 96;
        byte[] samples = Samples(width, height);
        var document = CreateWideRgb(width, height, samples, flipX, flipY);
        var rendered = new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(width / 2, height / 2, transparentBackground: true)
            { MaximumParallelism = workers });
        Assert.Empty(rendered.Diagnostics);
        for (int y = 0; y < height / 2; y++)
        for (int x = 0; x < width / 2; x++)
        {
            int sx = (flipX ? width / 2 - x - 1 : x) * 2;
            int sy = (flipY ? height / 2 - y - 1 : y) * 2;
            for (int channel = 0; channel < 3; channel++)
            {
                int sum = samples[(sy * width + sx) * 3 + channel]
                    + samples[(sy * width + sx + 1) * 3 + channel]
                    + samples[((sy + 1) * width + sx) * 3 + channel]
                    + samples[((sy + 1) * width + sx + 1) * 3 + channel];
                Assert.Equal((byte)Math.Round(sum / 4d),
                    rendered.Pixels.Span[(y * width / 2 + x) * 4 + 2 - channel]);
            }
            Assert.Equal(255, rendered.Pixels.Span[(y * width / 2 + x) * 4 + 3]);
        }
    }

    [Fact]
    public void ParallelReducedRowsPreserveClippedPixelsAndRegions()
    {
        const int width = 1030, height = 1034;
        var document = CreateWideRgb(width, height, Samples(width, height),
            clip: "0 0 m 515 0 l 257.5 517 l h W n ");
        var renderer = new PdfPageRenderer(document);
        var serial = new PdfRenderOptions(width / 2, height / 2,
            transparentBackground: true) { MaximumParallelism = 1 };
        var parallel = new PdfRenderOptions(width / 2, height / 2,
            transparentBackground: true) { MaximumParallelism = 4 };
        var expected = renderer.Render(0, serial);
        var actual = renderer.Render(0, parallel);
        Assert.Empty(expected.Diagnostics);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        Assert.Contains(actual.Pixels.ToArray().Where((_, i) => i % 4 == 3),
            alpha => alpha > 0 && alpha < 255);
        var region = renderer.RenderRegion(0, parallel, 111, 97, 193, 211);
        Assert.Empty(region.Diagnostics);
        for (int y = 0; y < 211; y++)
            Assert.Equal(expected.Pixels.Slice(((y + 97) * (width / 2) + 111) * 4,
                193 * 4).ToArray(), region.Pixels.Slice(y * 193 * 4, 193 * 4).ToArray());
    }

    [Fact]
    public void ReducedImageDoesNotAllocateAFullConvertedPlane()
    {
        const int sourceSize = 1024, outputSize = 512;
        var document = CreateWideRgb(sourceSize, sourceSize, Samples(sourceSize, sourceSize));
        var renderer = new PdfPageRenderer(document);
        var options = new PdfRenderOptions(outputSize, outputSize);
        byte[] pixels = new byte[outputSize * outputSize * 4];
        Assert.Empty(renderer.RenderInto(0, options, pixels));
        var held = new List<byte[]>();
        try
        {
            for (int index = 0; index < 8; index++)
                held.Add(PdfScratchBuffers.Bytes.Rent(pixels.Length));
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Empty(renderer.RenderInto(0, options, pixels));
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated < 512 * 1024,
                $"Reduced image allocated {allocated} bytes after its decoded samples were cached.");
        }
        finally
        {
            foreach (byte[] buffer in held) PdfScratchBuffers.Bytes.Return(buffer);
        }
    }

    private static PdfDocument CreateWideRgb(int width, int height, byte[] samples,
        bool flipX = false, bool flipY = false, string clip = "")
    {
        PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
        KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
        string matrix = $"{(flipX ? -width / 2 : width / 2)} 0 0 "
            + $"{(flipY ? -height / 2 : height / 2)} {(flipX ? width / 2 : 0)} {(flipY ? height / 2 : 0)}";
        var original = PdfDocument.Open(new PdfDocumentBuilder().AddPage(width / 2,
            height / 2, Encoding.ASCII.GetBytes(clip + matrix + " cm /Image Do")).Build());
        var catalog = (PdfDictionary)original.Resolve((PdfIndirectReference)original.Trailer[Name("Root")]);
        var pages = (PdfDictionary)original.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var pageReference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)original.Resolve(pageReference);
        var update = new PdfIncrementalUpdateBuilder(original);
        // Repeating a byte in the high/low positions gives exactly sample/255 in 16 bits.
        byte[] wideSamples = [.. samples.SelectMany(value => new byte[] { value, value })];
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(width)),
            Entry("Height", new PdfInteger(height)), Entry("BitsPerComponent", new PdfInteger(16)),
            Entry("ColorSpace", Name("DeviceRGB"))]), wideSamples));
        var resources = new PdfDictionary([
            Entry("XObject", new PdfDictionary([Entry("Image", image)]))]);
        update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(page
            .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources))));
        return PdfDocument.Open(update.Build());
    }

    private static byte[] Samples(int width, int height)
    {
        byte[] samples = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 3;
            samples[offset] = (byte)(x * 3 + y * 11);
            samples[offset + 1] = (byte)(x * 7 + y * 5);
            samples[offset + 2] = (byte)(x * 13 + y * 17);
        }
        return samples;
    }
}
