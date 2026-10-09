using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

[Collection(nameof(SoftMaskAllocationCollection))]
public sealed class PdfDirectImageAllocationTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void RepeatedColorKeyImagesKeepTransientStorageBounded(int components, bool reduced)
    {
        const int tile = 16, page = 128;
        int source = reduced ? tile * 2 : tile;
        byte[] samples = new byte[source * source * components];
        for (int index = 0; index < source * source; index++)
        {
            if (components == 1) samples[index] = 91;
            else
            {
                samples[index * 3] = 64;
                samples[index * 3 + 1] = 128;
                samples[index * 3 + 2] = 192;
            }
        }
        PdfImage image = components == 1 ? PdfImage.FromGray(source, source, samples)
            : PdfImage.FromRgb(source, source, samples);
        var content = new PdfContentStreamBuilder();
        for (int y = 0; y < page; y += tile)
        for (int x = 0; x < page; x += tile)
            content.DrawImage(image, x, y, tile, tile);
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(page, page, content).Build());
        static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        var catalog = (PdfDictionary)document.Resolve((PdfIndirectReference)document.Trailer[Name("Root")]);
        var pages = (PdfDictionary)document.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var pageObject = (PdfDictionary)document.Resolve((PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0]);
        var resources = (PdfDictionary)pageObject[Name("Resources")];
        var images = (PdfDictionary)resources[Name("XObject")];
        var reference = (PdfIndirectReference)images[Name("Im1")];
        var stream = (PdfStream)document.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(document);
        var mask = new PdfArray(Enumerable.Range(0, components * 2).Select(_ => (PdfObject)new PdfInteger(0)));
        update.ReplaceObject(reference.ObjectNumber, new PdfStream(new PdfDictionary(stream.Dictionary
            .Append(new KeyValuePair<PdfName, PdfObject>(Name("Mask"), mask))), stream.EncodedData.Span));
        document = PdfDocument.Open(update.Build());
        var renderer = new PdfPageRenderer(document);
        var options = new PdfRenderOptions(page, page) { CacheResult = false, MaximumParallelism = 1 };
        byte[] pixels = new byte[page * page * 4];
        Assert.Empty(renderer.RenderInto(0, options, pixels));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var diagnostics = renderer.RenderInto(0, options, pixels);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Empty(diagnostics);
        Assert.True(allocated < 256 * 1024, $"Repeated color-key images allocated {allocated} bytes.");
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            Assert.Equal(components == 1 ? 91 : 192, pixels[offset]);
            Assert.Equal(components == 1 ? 91 : 128, pixels[offset + 1]);
            Assert.Equal(components == 1 ? 91 : 64, pixels[offset + 2]);
            Assert.Equal(255, pixels[offset + 3]);
        }
    }
}
