using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Fonts;

public sealed class PdfStandardControlGlyphTests
{
    [Theory]
    [InlineData("Courier", 0)]
    [InlineData("Courier", 31)]
    [InlineData("Courier-Bold", 0)]
    [InlineData("Courier-Oblique", 0)]
    public void UndefinedCourierControlIsBlankAndKeepsItsAdvance(string name, int code)
    {
        string octal = Convert.ToString(code, 8).PadLeft(3, '0');
        PdfRenderedPage actual = Render($"(\\{octal}A) Tj", name: name);
        PdfRenderedPage expected = Render("7.2 0 Td (A) Tj", name: name);
        Assert.Contains(actual.Pixels.ToArray(), value => value != 255);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        Assert.Empty(actual.Diagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitControlMappingStillPaintsVisibleGlyph(bool embedded)
    {
        byte[]? program = embedded ? TrueTypeFontTests.BuildTestFont(false, includeOutlines: true) : null;
        PdfDictionary encoding = D(("BaseEncoding", N("WinAnsiEncoding")),
            ("Differences", new PdfArray([new PdfInteger(0), N("A")])));
        PdfRenderedPage actual = Render("(\\000) Tj", encoding, embedded: program);
        PdfRenderedPage expected = Render("(A) Tj", embedded: program);
        Assert.Contains(actual.Pixels.ToArray(), value => value != 255);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        Assert.Empty(actual.Diagnostics);
    }

    [Fact]
    public void MissingVisibleMappingStillReportsUnavailableOutline()
    {
        PdfDictionary encoding = D(("BaseEncoding", N("WinAnsiEncoding")),
            ("Differences", new PdfArray([new PdfInteger(0), N("UnregisteredVisibleGlyph")])));
        PdfRenderedPage actual = Render("(\\000) Tj", encoding);
        Assert.Contains("A text glyph outline is not implemented.", actual.Diagnostics);
    }

    [Fact]
    public void UnknownFontControlRemainsUnresolved()
    {
        PdfRenderedPage actual = Render("(\\000) Tj", name: "UnknownFamily");
        Assert.Contains("A text glyph outline is not implemented.", actual.Diagnostics);
    }

    [Fact]
    public void VisibleUnicodeFallbackIsNotReplacedWithBlankControl()
    {
        PdfRenderedPage actual = Render("(\\000) Tj", toUnicode:
            "1 begincodespacerange <00> <FF> endcodespacerange 1 beginbfchar <00> <D83DDE00> endbfchar");
        Assert.Contains("A text glyph outline is not implemented.", actual.Diagnostics);
    }

    private static PdfRenderedPage Render(string text, PdfDictionary? encoding = null,
        string name = "Courier", byte[]? embedded = null, string? toUnicode = null)
    {
        byte[] content = Encoding.ASCII.GetBytes("BT /F1 12 Tf 10 30 Td " + text + " ET");
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 60, content).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfDictionary font = D(("Type", N("Font")), ("Subtype", N(embedded is null ? "Type1" : "TrueType")),
            ("BaseFont", N(name)), ("Encoding", (PdfObject?)encoding ?? N("WinAnsiEncoding")),
            ("FirstChar", new PdfInteger(0)), ("Widths", new PdfArray(
                Enumerable.Repeat<PdfObject>(new PdfInteger(600), 32))));
        if (embedded is not null)
            font = new PdfDictionary(font.Append(new KeyValuePair<PdfName, PdfObject>(
                N("FontDescriptor"), D(("FontFile2", update.AddObject(new PdfStream(D(), embedded)))))));
        if (toUnicode is not null)
            font = new PdfDictionary(font.Append(new KeyValuePair<PdfName, PdfObject>(
                N("ToUnicode"), update.AddObject(new PdfStream(D(), Encoding.ASCII.GetBytes(toUnicode))))));
        PdfDictionary resources = D(("Font", D(("F1", font))));
        var changed = new PdfDictionary(page.Dictionary.Where(pair => !pair.Key.Equals(N("Resources")))
            .Append(new KeyValuePair<PdfName, PdfObject>(N("Resources"), resources)));
        byte[] bytes = update.ReplaceObject(page.Reference.ObjectNumber, changed).Build();
        return new PdfPageRenderer(PdfDocument.Open(bytes)).Render(0, new PdfRenderOptions(200, 120));
    }

    private static PdfName N(string name) => new(Encoding.ASCII.GetBytes(name));
    private static PdfDictionary D(params (string Key, PdfObject Value)[] pairs) =>
        new(pairs.Select(pair => new KeyValuePair<PdfName, PdfObject>(N(pair.Key), pair.Value)));
}
