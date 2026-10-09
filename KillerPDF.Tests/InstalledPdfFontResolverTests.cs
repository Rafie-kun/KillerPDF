using System.Collections;
using System.IO;
using System.Reflection;
using KillerPDF.Services;
using KillerPdf.Engine.Fonts;
using Xunit;

namespace KillerPDF.Tests;

public class InstalledPdfFontResolverTests
{
    [Theory]
    [InlineData("Helvetica", "ArialMT", "arial.ttf")]
    [InlineData("Helvetica-Bold", "Arial-BoldMT", "arialbd.ttf")]
    [InlineData("Helvetica-Oblique", "Arial-ItalicMT", "ariali.ttf")]
    [InlineData("Helvetica-BoldOblique", "Arial-BoldItalicMT", "arialbi.ttf")]
    [InlineData("Times-Roman", "TimesNewRomanPSMT", "times.ttf")]
    [InlineData("Times-Bold", "TimesNewRomanPS-BoldMT", "timesbd.ttf")]
    [InlineData("Times-Italic", "TimesNewRomanPS-ItalicMT", "timesi.ttf")]
    [InlineData("Times-BoldItalic", "TimesNewRomanPS-BoldItalicMT", "timesbi.ttf")]
    [InlineData("Courier", "CourierNewPSMT", "cour.ttf")]
    [InlineData("Courier-Bold", "CourierNewPS-BoldMT", "courbd.ttf")]
    [InlineData("Courier-Oblique", "CourierNewPS-ItalicMT", "couri.ttf")]
    [InlineData("Courier-BoldOblique", "CourierNewPS-BoldItalicMT", "courbi.ttf")]
    public void StandardAliasesShareExactWindowsFaceBytes(string first, string second, string file)
    {
        var resolver = new InstalledPdfFontResolver();
        byte[] expected = File.ReadAllBytes(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", file));

        byte[]? firstBytes = resolver.Resolve(new PdfFontRequest(first, "Adobe", "Standard", false));
        byte[]? secondBytes = resolver.Resolve(new PdfFontRequest(second, "Adobe", "Standard", false));

        Assert.Equal(expected, firstBytes);
        Assert.Same(firstBytes, secondBytes);
        Assert.Single(StandardCache(resolver));
    }

    [Fact]
    public void StandardCacheCannotGrowWithRequestMetadataOrAliasCount()
    {
        var resolver = new InstalledPdfFontResolver();
        foreach (string family in new[] { "Helvetica", "Times", "Courier" })
        foreach (string suffix in new[] { "", "-Bold", "-Italic", "-BoldItalic" })
        {
            byte[]? first = resolver.Resolve(new PdfFontRequest(family + suffix, "Adobe", "Standard", false));
            Assert.NotNull(first);
            for (int i = 0; i < 8; i++)
                Assert.Same(first, resolver.Resolve(new PdfFontRequest(
                    family + suffix, "Registry" + i, "Ordering" + i, i % 2 == 0)));
        }

        Assert.Equal(12, StandardCache(resolver).Count);
    }

    [Fact]
    public void MissingFacesDoNotEnterSuccessfulStandardCache()
    {
        var resolver = new InstalledPdfFontResolver();
        for (int i = 0; i < 3; i++)
            Assert.Null(resolver.Resolve(new PdfFontRequest(
                "KillerPdfMissingFaceForCacheRegression", "Registry" + i, "Unknown", false)));

        Assert.Empty(StandardCache(resolver));
    }

    [Fact]
    public void ConcurrentAliasRequestsShareOneSuccessfulFace()
    {
        var resolver = new InstalledPdfFontResolver();
        byte[]?[] values = new byte[64][];
        Parallel.For(0, values.Length, i => values[i] = resolver.Resolve(new PdfFontRequest(
            i % 2 == 0 ? "Helvetica" : "ArialMT", "Registry" + i, "Standard", false)));

        Assert.NotNull(values[0]);
        Assert.All(values, value => Assert.Same(values[0], value));
        Assert.Single(StandardCache(resolver));
    }

    private static IDictionary StandardCache(InstalledPdfFontResolver resolver) =>
        (IDictionary)typeof(InstalledPdfFontResolver).GetField("_standardFaceCache",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(resolver)!;
}
