using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfCalculatorColorTests
{
    // Adobe PostScript Language Reference, third edition, bitshift (p. 539).
    [Theory]
    [InlineData("-8 -2 bitshift 1073741822 eq")]
    [InlineData("-1 -31 bitshift 1 eq")]
    [InlineData("-1 -32 bitshift 0 eq")]
    [InlineData("-1 -2147483648 bitshift 0 eq")]
    [InlineData("-1 32 bitshift 0 eq")]
    [InlineData("1 2147483647 bitshift 0 eq")]
    [InlineData("7 3 bitshift 56 eq")]
    [InlineData("142 -3 bitshift 17 eq")]
    [InlineData("-8 0 bitshift -8 eq")]
    public void BitshiftProducesTheSpecifiedTint(string expression) => AssertWhite(expression);

    // Adobe PostScript Language Reference, third edition, round (p. 652).
    [Theory]
    [InlineData("-2.5 round -2 eq")]
    [InlineData("2.5 round 3 eq")]
    [InlineData("-6.5 round -6 eq")]
    [InlineData("-0.5 round 0 eq")]
    [InlineData("-2.5000000000000004 round -3 eq")]
    [InlineData("-2.4999999999999996 round -2 eq")]
    [InlineData("0.49999999999999994 round 0 eq")]
    [InlineData("0.5000000000000001 round 1 eq")]
    [InlineData("2 round 2 eq")]
    public void RoundProducesTheSpecifiedTint(string expression) => AssertWhite(expression);

    private static void AssertWhite(string expression)
    {
        static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) => new(Name(key), value);
        static PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10,
            Encoding.ASCII.GetBytes("/Spot cs 1 scn 0 0 10 10 re f")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var function = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("FunctionType", new PdfInteger(4)), Entry("Domain", Numbers(0, 1)),
            Entry("Range", Numbers(0, 1))]), Encoding.ASCII.GetBytes("{ pop " + expression + " { 1 } { 0 } ifelse }")));
        var resources = new PdfDictionary([Entry("ColorSpace", new PdfDictionary([
            Entry("Spot", new PdfArray([Name("Separation"), Name("Probe"), Name("DeviceGray"), function]))]))]);
        update.ReplaceObject(reference.ObjectNumber, new PdfDictionary(page
            .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources))));
        var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(10, 10));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, rendered.Pixels.Slice((5 * 10 + 5) * 4, 4).ToArray());
    }
}
