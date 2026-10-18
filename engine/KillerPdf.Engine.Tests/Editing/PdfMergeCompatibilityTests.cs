using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfMergeCompatibilityTests
{
    [Fact]
    public void ImportPreservesType3DescriptorWithoutOptionalMetrics()
    {
        var descriptor = new PdfDictionary([
            Entry("Type", Name("FontDescriptor")), Entry("FontName", Name("Example")),
            Entry("Flags", new PdfInteger(4)), Entry("ItalicAngle", new PdfInteger(0))
        ]);
        var glyph = new PdfStream(new PdfDictionary([]), "500 0 d0 0 0 500 700 re f"u8.ToArray());
        var font = new PdfDictionary([
            Entry("Type", Name("Font")), Entry("Subtype", Name("Type3")),
            Entry("FontBBox", Numbers(0, 0, 500, 700)),
            Entry("FontMatrix", Numbers(.001, 0, 0, .001, 0, 0)),
            Entry("FirstChar", new PdfInteger(65)), Entry("LastChar", new PdfInteger(65)),
            Entry("Widths", Numbers(500)),
            Entry("Encoding", new PdfDictionary([
                Entry("Differences", new PdfArray([new PdfInteger(65), Name("A")]))
            ])),
            Entry("CharProcs", new PdfDictionary([Entry("A", glyph)])),
            Entry("FontDescriptor", descriptor)
        ]);
        PdfDocument merged = Import(new PdfDictionary([
            Entry("Font", new PdfDictionary([Entry("F", font)]))
        ]), "BT /F 12 Tf 10 10 Td (A) Tj ET");

        PdfDictionary imported = Resource(merged, "Font", "F");
        PdfDictionary importedDescriptor = Resolve(merged, imported[Name("FontDescriptor")]);
        Assert.False(importedDescriptor.ContainsKey(Name("FontBBox")));
        Assert.False(importedDescriptor.ContainsKey(Name("Ascent")));
        Assert.False(importedDescriptor.ContainsKey(Name("Descent")));
        Assert.False(importedDescriptor.ContainsKey(Name("CapHeight")));
        Assert.False(importedDescriptor.ContainsKey(Name("StemV")));
    }

    [Fact]
    public void ImportPreservesCidFontLanguageName()
    {
        var descriptor = new PdfDictionary([
            Entry("Type", Name("FontDescriptor")), Entry("FontName", Name("Example")),
            Entry("Flags", new PdfInteger(4)), Entry("ItalicAngle", new PdfInteger(0)),
            Entry("FontBBox", Numbers(0, -200, 1000, 900)),
            Entry("Ascent", new PdfInteger(900)), Entry("Descent", new PdfInteger(-200)),
            Entry("CapHeight", new PdfInteger(700)), Entry("StemV", new PdfInteger(80)),
            Entry("Lang", Name("ja"))
        ]);
        var descendant = new PdfDictionary([
            Entry("Type", Name("Font")), Entry("Subtype", Name("CIDFontType2")),
            Entry("BaseFont", Name("Example")), Entry("FontDescriptor", descriptor),
            Entry("CIDSystemInfo", new PdfDictionary([
                Entry("Registry", new PdfString("Adobe"u8, PdfStringForm.Literal)),
                Entry("Ordering", new PdfString("Identity"u8, PdfStringForm.Literal)),
                Entry("Supplement", new PdfInteger(0))
            ]))
        ]);
        var font = new PdfDictionary([
            Entry("Type", Name("Font")), Entry("Subtype", Name("Type0")),
            Entry("BaseFont", Name("Example")), Entry("Encoding", Name("Identity-H")),
            Entry("DescendantFonts", new PdfArray([descendant]))
        ]);
        PdfDocument merged = Import(new PdfDictionary([
            Entry("Font", new PdfDictionary([Entry("F", font)]))
        ]), "BT /F 12 Tf 10 10 Td <0001> Tj ET");

        PdfDictionary imported = Resource(merged, "Font", "F");
        PdfDictionary importedDescendant = Resolve(merged,
            Assert.IsType<PdfArray>(imported[Name("DescendantFonts")])[0]);
        PdfDictionary importedDescriptor = Resolve(merged, importedDescendant[Name("FontDescriptor")]);
        Assert.Equal("ja", Assert.IsType<PdfName>(importedDescriptor[Name("Lang")]).ValueAsLatin1());
    }

    [Theory]
    [InlineData(0.4, 0.4)]
    [InlineData(0, 0.4)]
    [InlineData(0.4, 1)]
    public void ImportPreservesStitchingBoundsIncludingEmptyIntervals(double first, double second)
    {
        PdfDocument merged = Import(StitchingPattern(first, second),
            "/Pattern cs /P scn 0 0 100 100 re f");
        PdfDictionary pattern = Resource(merged, "Pattern", "P");
        PdfDictionary shading = Resolve(merged, pattern[Name("Shading")]);
        PdfDictionary function = Resolve(merged, shading[Name("Function")]);
        var bounds = Assert.IsType<PdfArray>(function[Name("Bounds")]);
        Assert.Equal(first, Assert.IsType<PdfReal>(bounds[0]).Value);
        Assert.Equal(second, Assert.IsType<PdfReal>(bounds[1]).Value);
        var rendered = new PdfPageRenderer(merged).Render(1,
            new PdfRenderOptions(100, 100, includeAnnotations: false, includeFormFields: false));
        Assert.Empty(rendered.Diagnostics);
        if (first == second)
        {
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, rendered.Pixels.Slice((50 * 100 + 20) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, rendered.Pixels.Slice((50 * 100 + 80) * 4, 4).ToArray());
        }
    }

    [Theory]
    [InlineData(0.6, 0.4)]
    [InlineData(-0.1, 0.4)]
    [InlineData(0.4, 1.1)]
    public void ImportStillRejectsReversedOrOutOfDomainStitchingBounds(double first, double second)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            Import(StitchingPattern(first, second), "/Pattern cs /P scn 0 0 100 100 re f"));
        Assert.Contains("/Bounds values are not ordered within /Domain", error.Message);
    }

    private static PdfDictionary StitchingPattern(double first, double second)
    {
        PdfDictionary Constant(double r, double g, double b) => new([
            Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
            Entry("C0", Numbers(r, g, b)), Entry("C1", Numbers(r, g, b)),
            Entry("N", new PdfInteger(1))
        ]);
        var function = new PdfDictionary([
            Entry("FunctionType", new PdfInteger(3)), Entry("Domain", Numbers(0, 1)),
            Entry("Bounds", Numbers(first, second)), Entry("Encode", Numbers(0, 1, 0, 1, 0, 1)),
            Entry("Functions", new PdfArray([Constant(1, 0, 0), Constant(0, 1, 0), Constant(0, 0, 1)]))
        ]);
        return new PdfDictionary([Entry("Pattern", new PdfDictionary([
            Entry("P", new PdfDictionary([
                Entry("Type", Name("Pattern")), Entry("PatternType", new PdfInteger(2)),
                Entry("Shading", new PdfDictionary([
                    Entry("ShadingType", new PdfInteger(2)), Entry("ColorSpace", Name("DeviceRGB")),
                    Entry("Coords", Numbers(0, 0, 100, 0)), Entry("Function", function)
                ]))
            ]))
        ]))]);
    }

    private static PdfDocument Import(PdfDictionary resources, string content)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, Encoding.ASCII.GetBytes(content)).Build());
        PdfDictionary catalog = Resolve(source, source.Trailer[Name("Root")]);
        PdfDictionary pages = Resolve(source, catalog[Name("Pages")]);
        var reference = Assert.IsType<PdfIndirectReference>(Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = Resolve(source, reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfObject StoreStreams(PdfObject value) => value switch
        {
            PdfStream stream => update.AddObject(stream),
            PdfDictionary dictionary => new PdfDictionary(dictionary.Select(entry =>
                new KeyValuePair<PdfName, PdfObject>(entry.Key, StoreStreams(entry.Value)))),
            PdfArray array => new PdfArray(array.Select(StoreStreams)),
            _ => value
        };
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page
            .Where(entry => !entry.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", StoreStreams(resources)))));
        source = PdfDocument.Open(update.Build());
        return PdfDocument.Open(new PdfIncrementalPageEditor(PdfDocument.Open(
                new PdfDocumentBuilder().AddBlankPage().Build()))
            .AddImportedPage(source, 0).Build());
    }

    private static PdfDictionary Resource(PdfDocument document, string category, string name)
    {
        var pages = new PdfIncrementalPageEditor(document);
        Assert.Equal(2, pages.PageCount);
        PdfDictionary catalog = Resolve(document, document.Trailer[Name("Root")]);
        PdfDictionary tree = Resolve(document, catalog[Name("Pages")]);
        PdfDictionary page = Resolve(document, Assert.IsType<PdfArray>(tree[Name("Kids")])[1]);
        return Resolve(document, Resolve(document, Resolve(document, page[Name("Resources")])[Name(category)])[Name(name)]);
    }

    private static PdfDictionary Resolve(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(value is PdfIndirectReference reference ? document.Resolve(reference) : value);
    private static PdfArray Numbers(params double[] values) =>
        new(values.Select(value => (PdfObject)new PdfReal(value)));
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
}
