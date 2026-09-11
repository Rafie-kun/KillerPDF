using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

public sealed class PdfPageDimensionNormalizerTests
{
    [Fact]
    public void FindPagesOutsideRange_ReturnsOnlyOutOfRangePages()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(100, 200)
            .AddBlankPage(20_000, 100)
            .AddBlankPage(2, 2)
            .Build());

        Assert.Equal([1, 2],
            PdfPageDimensionNormalizer.FindPagesOutsideRange(document, 3, 14_400));
    }

    [Fact]
    public void NormalizePages_ScalesGeometryContentAndAnnotationCoordinates()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage(20_000, 10_000)
            .SetPageBox(0, PdfPageBox.Crop, 100, 200, 19_000, 9_000)
            .AddHighlight(0, 100, 200, 300, 40)
            .Build();
        PdfDocument original = PdfDocument.Open(source);

        byte[] result = PdfPageDimensionNormalizer.NormalizePages(
            original, [0], 3, 14_400);

        Assert.True(result.AsSpan(0, source.Length).SequenceEqual(source));
        PdfDocument reopened = PdfDocument.Open(result);
        PdfPageInformation pageInfo = Assert.Single(PdfPageInformation.Read(reopened));
        Assert.Equal(13_680, pageInfo.Width, 6);
        Assert.Equal(6_480, pageInfo.Height, 6);
        PdfDictionary page = FirstPage(reopened);
        PdfArray contents = Assert.IsType<PdfArray>(page[Name("Contents")]);
        Assert.True(contents.Count >= 2);
        PdfStream prefix = Assert.IsType<PdfStream>(reopened.Resolve(
            Assert.IsType<PdfIndirectReference>(contents[0])));
        Assert.Contains("q 0.72 0 0 0.72 0 0 cm", Encoding.ASCII.GetString(prefix.EncodedData.Span));
        PdfArray annotations = ResolveArray(reopened, page[Name("Annots")]);
        PdfDictionary annotation = ResolveDictionary(reopened, annotations[0]);
        Assert.Equal([72d, 144d, 288d, 172.8d], Numbers(reopened, annotation[Name("Rect")]),
            new DoubleArrayComparer());
        Assert.All(Numbers(reopened, annotation[Name("QuadPoints")]),
            number => Assert.InRange(number, 0, 14_400));
    }

    [Fact]
    public void NormalizePages_PreservesBytesWhenSelectedPagesAreAlreadyValid()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage(100, 200).Build();
        Assert.Equal(source, PdfPageDimensionNormalizer.NormalizePages(
            PdfDocument.Open(source), [0], 3, 14_400));
    }

    private static PdfDictionary FirstPage(PdfDocument document)
    {
        PdfDictionary catalog = ResolveDictionary(document, document.Trailer[Name("Root")]);
        PdfDictionary pages = ResolveDictionary(document, catalog[Name("Pages")]);
        PdfArray kids = ResolveArray(document, pages[Name("Kids")]);
        return ResolveDictionary(document, kids[0]);
    }

    private static double[] Numbers(PdfDocument document, PdfObject value) =>
        [.. ResolveArray(document, value).Select(item => Resolve(document, item) switch
        {
            PdfInteger integer => (double)integer.Value,
            PdfReal real => real.Value,
            _ => throw new Xunit.Sdk.XunitException("Expected a numeric PDF value.")
        })];

    private static PdfDictionary ResolveDictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(Resolve(document, value));
    private static PdfArray ResolveArray(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfArray>(Resolve(document, value));
    private static PdfObject Resolve(PdfDocument document, PdfObject value) =>
        value is PdfIndirectReference reference ? document.Resolve(reference) : value;
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private sealed class DoubleArrayComparer : IEqualityComparer<double[]>
    {
        public bool Equals(double[]? x, double[]? y) => x is not null && y is not null
            && x.Length == y.Length && x.Zip(y).All(pair => Math.Abs(pair.First - pair.Second) < 1e-6);
        public int GetHashCode(double[] obj) => 0;
    }
}
