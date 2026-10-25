using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfMalformedFontRecoveryTests
{
    [Fact]
    public void InvalidDifferencesDoNotPreventLaterPageTextFromRenderingInRecovery()
    {
        const int width = 240;
        const int height = 160;
        PdfRenderedPage expected = new PdfPageRenderer(Create(
            "BT /F1 18 Tf 10 20 Td (Later text) Tj ET", false))
            .Render(0, new PdfRenderOptions(width, height));

        PdfRenderedPage actual = new PdfPageRenderer(Create(
            "BT /F0 18 Tf 10 60 Td (Bad font) Tj ET "
            + "BT /F1 18 Tf 10 20 Td (Later text) Tj ET", true))
            .Render(0, new PdfRenderOptions(width, height));

        byte[] actualPixels = actual.Pixels.ToArray();
        byte[] expectedPixels = expected.Pixels.ToArray();
        Assert.Contains(expectedPixels, pixel => pixel != 255);
        for (int index = 0; index < expectedPixels.Length; index++)
            if (expectedPixels[index] != 255)
                Assert.NotEqual(255, actualPixels[index]);
        Assert.Contains("Invalid simple-font differences were ignored.", actual.Diagnostics);
    }

    [Fact]
    public void InvalidDifferencesRemainRejectedInStrictMode()
    {
        var renderer = new PdfPageRenderer(Create(
            "BT /F0 18 Tf 10 60 Td (Bad font) Tj ET", false));

        Assert.Throws<FormatException>(() => renderer.Render(0,
            new PdfRenderOptions(240, 160)));
    }

    [Fact]
    public void InvalidWidthMetricUsesHelveticaAndPreservesLaterTextInRecovery()
    {
        PdfRenderedPage expected = Render(Create(
            "BT /F1 18 Tf 10 20 Td (Later text) Tj ET", false));
        PdfRenderedPage actual = Render(Create(
            "BT /F0 18 Tf 10 60 Td (Bad font) Tj ET "
            + "BT /F1 18 Tf 10 20 Td (Later text) Tj ET", true, "InvalidMetric"));

        AssertLaterTextIsVisible(expected, actual);
        Assert.Contains("An invalid font dictionary was replaced with Helvetica.", actual.Diagnostics);
        Assert.Throws<FormatException>(() => Render(Create(
            "BT /F0 18 Tf 10 60 Td (Bad font) Tj ET", false, "InvalidMetric")));
    }

    [Fact]
    public void NullIndirectWidthUsesTheFontDefaultInRecovery()
    {
        PdfRenderedPage expected = Render(Create(
            "BT /F1 18 Tf 10 20 Td (Later text) Tj ET", false));
        PdfRenderedPage actual = Render(Create(
            "BT /F0 18 Tf 10 60 Td (Bad font) Tj ET "
            + "BT /F1 18 Tf 10 20 Td (Later text) Tj ET", true, "NullMetric"));

        AssertLaterTextIsVisible(expected, actual);
        Assert.Contains("Null font metrics used their default values.", actual.Diagnostics);
        Assert.Throws<FormatException>(() => Render(Create(
            "BT /F0 18 Tf 10 60 Td (Bad font) Tj ET", false, "NullMetric")));
    }

    [Fact]
    public void SimpleWidthsBeyondCode255AreClampedInRecovery()
    {
        PdfRenderedPage expected = Render(Create(
            "BT /F1 18 Tf 10 20 Td (Later text) Tj ET", false));
        PdfRenderedPage actual = Render(Create(
            "BT /F0 18 Tf 10 60 Td (Bad font) Tj ET "
            + "BT /F1 18 Tf 10 20 Td (Later text) Tj ET", true, "WidthRange"));

        AssertLaterTextIsVisible(expected, actual);
        Assert.Contains("Simple-font widths beyond code 255 were ignored.", actual.Diagnostics);
        Assert.Throws<FormatException>(() => Render(Create(
            "BT /F0 18 Tf 10 60 Td (Bad font) Tj ET", false, "WidthRange")));
    }

    [Fact]
    public void RealCidInCompositeWidthsUsesHelveticaInRecovery()
    {
        PdfRenderedPage expected = Render(Create(
            "BT /F1 18 Tf 10 20 Td (Later text) Tj ET", false));
        PdfRenderedPage actual = Render(Create(
            "BT /F0 18 Tf 10 60 Td (\\000A) Tj ET "
            + "BT /F1 18 Tf 10 20 Td (Later text) Tj ET", true, "RealCid"));

        AssertLaterTextIsVisible(expected, actual);
        Assert.Contains("An invalid font dictionary was replaced with Helvetica.", actual.Diagnostics);
        Assert.Throws<FormatException>(() => Render(Create(
            "BT /F0 18 Tf 10 60 Td (\\000A) Tj ET", false, "RealCid")));
    }

    [Fact]
    public void InvalidFontSelectedByGraphicsStateUsesHelveticaInRecovery()
    {
        PdfRenderedPage expected = Render(Create(
            "BT /F1 18 Tf 10 20 Td (Later text) Tj ET", false));
        const string content = "/GS0 gs BT 10 60 Td (Bad font) Tj ET "
            + "BT /F1 18 Tf 10 20 Td (Later text) Tj ET";
        PdfRenderedPage actual = Render(Create(content, true, "GsFont"));

        AssertLaterTextIsVisible(expected, actual);
        Assert.Contains("An invalid font dictionary was replaced with Helvetica.", actual.Diagnostics);
        Assert.Throws<FormatException>(() => Render(Create(content, false, "GsFont")));
    }

    private static PdfRenderedPage Render(PdfDocument document) =>
        new PdfPageRenderer(document).Render(0, new PdfRenderOptions(240, 160));

    private static void AssertLaterTextIsVisible(PdfRenderedPage expected, PdfRenderedPage actual)
    {
        byte[] actualPixels = actual.Pixels.ToArray();
        byte[] expectedPixels = expected.Pixels.ToArray();
        Assert.Contains(expectedPixels, pixel => pixel != 255);
        for (int index = 0; index < expectedPixels.Length; index++)
            if (expectedPixels[index] != 255)
                Assert.NotEqual(255, actualPixels[index]);
    }

    private static PdfDocument Create(string content, bool recovery, string defect = "Differences")
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(120, 80, Encoding.ASCII.GetBytes(content)).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfDictionary badFont = defect switch
        {
            "GsFont" => SimpleFontWithWidths([new PdfBoolean(true)], 65),
            "InvalidMetric" => SimpleFontWithWidths([new PdfBoolean(true)], 65),
            "NullMetric" => SimpleFontWithWidths([update.AddObject(PdfNull.Instance)], 65),
            "WidthRange" => SimpleFontWithWidths(
                Enumerable.Range(0, 10).Select(value => (PdfObject)new PdfInteger(500 + value)), 250),
            "RealCid" => D(("Type", N("Font")), ("Subtype", N("Type0")),
                ("BaseFont", N("Helvetica")), ("Encoding", N("Identity-H")),
                ("DescendantFonts", new PdfArray([D(("Type", N("Font")),
                    ("Subtype", N("CIDFontType2")), ("BaseFont", N("Helvetica")),
                    ("W", new PdfArray([new PdfReal(1.5), new PdfInteger(1), new PdfInteger(500)])))]))),
            _ => D(("Type", N("Font")), ("Subtype", N("Type1")),
                ("BaseFont", N("Helvetica")), ("Encoding", D(("BaseEncoding", N("WinAnsiEncoding")),
                    ("Differences", new PdfArray([new PdfInteger(65), new PdfBoolean(true)])))))
        };
        var validFont = D(("Type", N("Font")), ("Subtype", N("Type1")),
            ("BaseFont", N("Helvetica")), ("Encoding", N("WinAnsiEncoding")));
        PdfDictionary graphicsFontState = D(("Font", new PdfArray([badFont, new PdfInteger(18)])));
        PdfDictionary resources = defect == "GsFont"
            ? D(("Font", D(("F1", validFont))),
                ("ExtGState", D(("GS0", graphicsFontState))))
            : D(("Font", D(("F0", badFont), ("F1", validFont))));
        PdfDictionary changed = new(page.Dictionary.Where(entry => !entry.Key.Equals(N("Resources")))
            .Append(new KeyValuePair<PdfName, PdfObject>(N("Resources"), resources)));
        byte[] bytes = update.ReplaceObject(page.Reference.ObjectNumber, changed).Build();
        return recovery ? PdfDocument.OpenWithCompatibilityRecovery(bytes) : PdfDocument.Open(bytes);
    }

    private static PdfDictionary SimpleFontWithWidths(IEnumerable<PdfObject> widths, int firstChar) =>
        D(("Type", N("Font")), ("Subtype", N("Type1")), ("BaseFont", N("Helvetica")),
            ("FirstChar", new PdfInteger(firstChar)), ("Widths", new PdfArray(widths)),
            ("Encoding", N("WinAnsiEncoding")));

    private static PdfName N(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfDictionary D(params (string Name, PdfObject Value)[] values) =>
        new(values.Select(value => new KeyValuePair<PdfName, PdfObject>(N(value.Name), value.Value)));
}
