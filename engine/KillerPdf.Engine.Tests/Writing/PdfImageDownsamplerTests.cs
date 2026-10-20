using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

// #433: Reduce File Size resamples images drawn at more resolution than the page shows.
public sealed class PdfImageDownsamplerTests
{
    // Photo-like noise, so the stored image does not compress away and resampling pays off.
    private static PdfImage Photo(int size)
    {
        var rgb = new byte[size * size * 3];
        new Random(433).NextBytes(rgb);
        return PdfImage.FromRgb(size, size, rgb);
    }

    // One 1200 pixel image drawn 100 points wide: 864 dpi.
    private static PdfDocument Sample() => PdfDocument.Open(new PdfDocumentBuilder()
        .AddPage(200, 200, new PdfContentStreamBuilder().DrawImage(Photo(1200), 50, 50, 100, 100))
        .Build());

    private static PdfDictionary FirstImage(ReadOnlyMemory<byte> data)
    {
        PdfDocument document = PdfDocument.Open(data.ToArray());
        PdfPageTreeEntry page = PdfPageTree.Read(document).Pages[0];
        var resources = (PdfDictionary)Resolve(document, page.InheritedValues[new PdfName("Resources"u8)]);
        var xObjects = (PdfDictionary)Resolve(document, resources[new PdfName("XObject"u8)]);
        return ((PdfStream)Resolve(document, xObjects.Values.Single())).Dictionary;
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value) =>
        value is PdfIndirectReference reference ? document.Resolve(reference) : value;

    private static long Integer(PdfDictionary dictionary, string key) =>
        ((PdfInteger)dictionary[new PdfName(System.Text.Encoding.ASCII.GetBytes(key))]).Value;

    [Fact]
    public void Downsample_ReducesAnOversizedImageToTheTargetResolution()
    {
        PdfImageDownsampleResult result = PdfImageDownsampler.Downsample(Sample(),
            new PdfImageDownsampleOptions { TargetDpi = 150, ThresholdDpi = 225 });

        Assert.Equal(1, result.ImagesResampled);
        Assert.True(result.ImageBytesAfter < result.ImageBytesBefore);
        PdfDictionary image = FirstImage(result.Data);
        Assert.Equal(208, Integer(image, "Width"));
        Assert.Equal(208, Integer(image, "Height"));
        Assert.Single(PdfPageTree.Read(PdfDocument.Open(result.Data.ToArray())).Pages);
    }

    [Fact]
    public void Downsample_LeavesImagesUnderTheThreshold()
    {
        PdfDocument document = Sample();
        PdfImageDownsampleResult result = PdfImageDownsampler.Downsample(document,
            new PdfImageDownsampleOptions { TargetDpi = 600, ThresholdDpi = 900 });

        Assert.Equal(0, result.ImagesResampled);
        Assert.Equal(1200, Integer(FirstImage(result.Data), "Width"));
    }

    [Fact]
    public void Downsample_HandsTheSmallerImageToTheJpegEncoder()
    {
        (int Width, int Height, int Components, int Quality)? call = null;
        PdfImageDownsampleResult result = PdfImageDownsampler.Downsample(Sample(),
            new PdfImageDownsampleOptions
            {
                TargetDpi = 150,
                JpegQuality = 70,
                JpegEncoder = (samples, width, height, components, quality) =>
                {
                    Assert.Equal(width * height * components, samples.Length);
                    call = (width, height, components, quality);
                    return [0xFF, 0xD8, 0xFF, 0xD9];
                }
            });

        Assert.NotNull(call);
        Assert.Equal((208, 208, 3, 70), call.Value);
        PdfDictionary image = FirstImage(result.Data);
        Assert.Equal(new PdfName("DCTDecode"u8), image[new PdfName("Filter"u8)]);
    }

    [Fact]
    public void AreaAverage_AveragesTheCoveredSamples()
    {
        byte[] source =
        [
            0, 100, 200, 200,
            100, 0, 200, 200,
            10, 10, 50, 50,
            10, 10, 50, 50
        ];

        Assert.Equal(new byte[] { 50, 200, 10, 50 }, PdfImageDownsampler.AreaAverage(source, 4, 4, 1, 2, 2));
    }

    [Fact]
    public void Downsample_SkipsAnImageWhoseDeclaredSizeOverflows()
    {
        // A malformed image that declares more samples than an int can count must be left
        // alone, not abort the whole operation.
        PdfDocument document = Sample();
        PdfPageTreeEntry page = PdfPageTree.Read(document).Pages[0];
        var resources = (PdfDictionary)Resolve(document, page.InheritedValues[new PdfName("Resources"u8)]);
        var xObjects = (PdfDictionary)Resolve(document, resources[new PdfName("XObject"u8)]);
        var reference = (PdfIndirectReference)xObjects.Values.Single();
        var image = (PdfStream)document.Resolve(reference);
        PdfName Name(string key) => new(System.Text.Encoding.ASCII.GetBytes(key));
        var huge = new PdfDictionary(image.Dictionary
            .Where(entry => !entry.Key.Equals(Name("Width")) && !entry.Key.Equals(Name("Height")))
            .Append(new(Name("Width"), new PdfInteger(100_000)))
            .Append(new(Name("Height"), new PdfInteger(100_000))));
        document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(reference.ObjectNumber, new PdfStream(huge, image.EncodedData.ToArray())).Build());

        PdfImageDownsampleResult result = PdfImageDownsampler.Downsample(document,
            new PdfImageDownsampleOptions { TargetDpi = 150, ThresholdDpi = 225 });

        Assert.Equal(0, result.ImagesResampled);
    }

    [Fact]
    public void Options_RejectAThresholdBelowTheTarget()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfImageDownsampler.Downsample(Sample(),
            new PdfImageDownsampleOptions { TargetDpi = 300, ThresholdDpi = 200 }));
    }
}
