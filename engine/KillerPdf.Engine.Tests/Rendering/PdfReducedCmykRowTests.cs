using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

[Collection(nameof(SoftMaskAllocationCollection))]
public sealed class PdfReducedCmykRowTests
{
    [Theory]
    [InlineData(false, false, 8, 1)]
    [InlineData(false, true, 8, 1)]
    [InlineData(true, false, 8, 1)]
    [InlineData(true, true, 8, 1)]
    [InlineData(false, false, 16, 4)]
    [InlineData(false, true, 16, 4)]
    [InlineData(true, false, 16, 4)]
    [InlineData(true, true, 16, 4)]
    public void ReducedInkRowsPreserveSourceAverages(bool flipX, bool flipY,
        int bits, int workers)
    {
        const int width = 128, height = 96;
        byte[] samples = Samples(width, height);
        var renderer = new PdfPageRenderer(Create(width, height, samples, bits, flipX, flipY));
        var rendered = renderer.Render(0, new PdfRenderOptions(width / 2, height / 2,
            transparentBackground: true) { MaximumParallelism = workers });
        Assert.Empty(rendered.Diagnostics);
        for (int y = 0; y < height / 2; y++)
        for (int x = 0; x < width / 2; x++)
        {
            int sx = (flipX ? width / 2 - x - 1 : x) * 2;
            int sy = (flipY ? height / 2 - y - 1 : y) * 2;
            uint ink = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                int sum = samples[(sy * width + sx) * 4 + channel]
                    + samples[(sy * width + sx + 1) * 4 + channel]
                    + samples[((sy + 1) * width + sx) * 4 + channel]
                    + samples[((sy + 1) * width + sx + 1) * 4 + channel];
                ink |= (uint)(byte)Math.Round(sum / 4d) << (channel * 8);
            }
            uint rgb = PdfDeviceCmyk.ToRgb(ink);
            Assert.True(rendered.Pixels.Span.Slice((y * width / 2 + x) * 4, 4)
                .SequenceEqual([(byte)rgb, (byte)(rgb >> 8), (byte)(rgb >> 16), (byte)255]));
        }
    }

    [Theory]
    [InlineData(false, false, 89, 67)]
    [InlineData(false, true, 89, 67)]
    [InlineData(true, false, 89, 67)]
    [InlineData(true, true, 89, 67)]
    [InlineData(false, false, 37, 31)]
    [InlineData(false, true, 37, 31)]
    [InlineData(true, false, 37, 31)]
    [InlineData(true, true, 37, 31)]
    public void NarrowInkRegionsPreserveFractionalSampling(bool flipX, bool flipY,
        int width, int height)
    {
        var renderer = new PdfPageRenderer(Create(129, 99, Samples(129, 99), 16, flipX, flipY));
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

    [Fact]
    public void ParallelInkRowsPreserveAntialiasedClipping()
    {
        const int width = 1030, height = 1034;
        var renderer = new PdfPageRenderer(Create(width, height, Samples(width, height), 16,
            clip: "0 0 m 515 0 l 257.5 517 l h W n "));
        var serial = renderer.Render(0, new PdfRenderOptions(width / 2, height / 2,
            transparentBackground: true) { CacheResult = false, MaximumParallelism = 1 });
        var parallel = renderer.Render(0, new PdfRenderOptions(width / 2, height / 2,
            transparentBackground: true) { CacheResult = false, MaximumParallelism = 4 });
        Assert.Empty(serial.Diagnostics);
        Assert.Empty(parallel.Diagnostics);
        Assert.Equal(serial.Pixels.ToArray(), parallel.Pixels.ToArray());
        Assert.Contains(parallel.Pixels.ToArray().Where((_, i) => i % 4 == 3),
            alpha => alpha > 0 && alpha < 255);
    }

    [Fact]
    public void ReducedInkImageDoesNotAllocateAFullConvertedPlane()
    {
        const int sourceSize = 1024, outputSize = 512;
        var renderer = new PdfPageRenderer(Create(sourceSize, sourceSize,
            Samples(sourceSize, sourceSize), 16));
        var options = new PdfRenderOptions(outputSize, outputSize) { MaximumParallelism = 1 };
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
                $"Reduced CMYK image allocated {allocated} bytes after decoded samples were cached.");
        }
        finally
        {
            foreach (byte[] buffer in held) PdfScratchBuffers.Bytes.Return(buffer);
        }
    }

    private static PdfDocument Create(int width, int height, byte[] samples, int bits,
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
        byte[] encoded = bits == 8 ? samples
            : [.. samples.SelectMany(value => new byte[] { value, value })];
        var image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(width)),
            Entry("Height", new PdfInteger(height)), Entry("BitsPerComponent", new PdfInteger(bits)),
            Entry("ColorSpace", Name("DeviceCMYK"))]), encoded));
        var resources = new PdfDictionary([
            Entry("XObject", new PdfDictionary([Entry("Image", image)]))]);
        var group = new PdfDictionary([
            Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))]);
        update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(page
            .Where(pair => !pair.Key.Equals(Name("Resources")) && !pair.Key.Equals(Name("Group")))
            .Append(Entry("Resources", resources)).Append(Entry("Group", group))));
        return PdfDocument.Open(update.Build());
    }

    private static byte[] Samples(int width, int height)
    {
        byte[] samples = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            samples[offset] = (byte)(x * 3 + y * 11);
            samples[offset + 1] = (byte)(x * 7 + y * 5);
            samples[offset + 2] = (byte)(x * 13 + y * 17);
            samples[offset + 3] = (byte)(x * 19 + y * 23);
        }
        return samples;
    }
}
