using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageGroupBackdropTests
{
    [Theory]
    [InlineData("pattern")]
    [InlineData("pattern-form")]
    [InlineData("type3")]
    [InlineData("type3-gs")]
    public void Render_PageGroupKeepsNestedProgramBlendingOffThePaper(string program)
    {
        PdfDocument document = NestedBlendDocument(program);
        var renderer = new PdfPageRenderer(document);
        var options = new PdfRenderOptions(10, 10);

        var rendered = renderer.Render(0, options);

        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 },
            rendered.Pixels.Slice((5 * 10 + 5) * 4, 4).ToArray());
        var region = renderer.RenderRegion(0, options, 5, 5, 1, 1);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, region.Pixels.ToArray());
        var transparent = renderer.Render(0, new PdfRenderOptions(10, 10, transparentBackground: true));
        Assert.Equal(new byte[] { 0, 0, 255, 255 },
            transparent.Pixels.Slice((5 * 10 + 5) * 4, 4).ToArray());
    }

    private static PdfDocument NestedBlendDocument(string program)
    {
        bool pattern = program.StartsWith("pattern", StringComparison.Ordinal);
        string resources = pattern ? "/Pattern << /P1 5 0 R >>"
            : "/Font << /F1 5 0 R >> /ExtGState << /FontGS << /Font [5 0 R 10] >> >>";
        string content = pattern ? "/Pattern cs /P1 scn 0 0 10 10 re f"
            : "BT " + (program == "type3-gs" ? "/FontGS gs" : "/F1 10 Tf") + " 0 0 Td (A) Tj ET";
        string Stream(string entries, string body) =>
            $"<< {entries} /Length {body.Length} >>\nstream\n{body}\nendstream";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] " +
                "/Group << /S /Transparency /CS /DeviceRGB >> /Resources << " +
                (program == "pattern-form" ? "/XObject << /Fm1 6 0 R >>" : resources) +
                " >> /Contents 4 0 R >>",
            Stream("", program == "pattern-form" ? "/Fm1 Do" : content)
        };
        if (pattern)
        {
            objects.Add(Stream("/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 " +
                "/BBox [0 0 10 10] /XStep 10 /YStep 10 " +
                "/Resources << /ExtGState << /GS1 << /BM /Difference >> >> >>",
                "/GS1 gs 1 0 0 rg 0 0 10 10 re f"));
            if (program == "pattern-form")
                objects.Add(Stream("/Type /XObject /Subtype /Form /BBox [0 0 10 10] " +
                    "/Resources << " + resources + " >>", content));
        }
        else
        {
            objects.Add("<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] " +
                "/FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << /A 6 0 R >> " +
                "/Encoding << /Differences [65 /A] >> /FirstChar 65 /LastChar 65 /Widths [1000] " +
                "/Resources << /ExtGState << /GS1 << /BM /Difference >> >> >> >>");
            objects.Add(Stream("", "1000 0 d0 /GS1 gs 1 0 0 rg 0 0 1000 1000 re f"));
        }
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string body in objects)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.ASCII.GetBytes(pdf.ToString()));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 0.5)]
    [InlineData(true, 0.5)]
    public void Render_PageGroupBlendsBeforeAddingWhitePaper(bool isolated, double opacity)
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10,
            new PdfContentStreamBuilder()
                .SetGraphicsState(new PdfGraphicsState(fillOpacity: opacity, blendMode: PdfBlendMode.Difference))
                .SetFillRgb(1, 0, 0).Rectangle(0, 0, 5, 10).Fill()).Build());
        PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
        var catalog = (PdfDictionary)document.Resolve((PdfIndirectReference)document.Trailer[Name("Root")]);
        var pages = (PdfDictionary)document.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)document.Resolve(reference);
        var group = new PdfDictionary([
            new(Name("S"), Name("Transparency")),
            new(Name("CS"), Name("DeviceRGB")),
            new(Name("I"), new PdfBoolean(isolated))]);
        var update = new PdfIncrementalUpdateBuilder(document);
        document = PdfDocument.Open(update.ReplaceObject(reference.ObjectNumber,
            new PdfDictionary(page.Append(new(Name("Group"), group)))).Build());

        var renderer = new PdfPageRenderer(document);
        var options = new PdfRenderOptions(10, 10);
        var rendered = renderer.Render(0, options);

        Assert.Empty(rendered.Diagnostics);
        byte paper = opacity == 1 ? (byte)0 : (byte)127;
        Assert.Equal(new byte[] { paper, paper, 255, 255 }, rendered.Pixels.Slice((5 * 10 + 2) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, rendered.Pixels.Slice((5 * 10 + 8) * 4, 4).ToArray());
        var transparent = renderer.Render(0, new PdfRenderOptions(10, 10, transparentBackground: true));
        Assert.Equal(new byte[] { 0, 0, 255, opacity == 1 ? (byte)255 : (byte)128 },
            transparent.Pixels.Slice((5 * 10 + 2) * 4, 4).ToArray());
        Assert.Equal(0, transparent.Pixels.Span[(5 * 10 + 8) * 4 + 3]);
        var region = renderer.RenderRegion(0, options, 2, 5, 7, 1);
        Assert.Equal(rendered.Pixels.Slice((5 * 10 + 2) * 4, 7 * 4).ToArray(), region.Pixels.ToArray());
        var plate = renderer.Render(0, options with { SeparationPlate = "Cyan" });
        Assert.Empty(plate.Diagnostics);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, plate.Pixels.Slice((5 * 10 + 8) * 4, 4).ToArray());
    }

    [Fact]
    public void Render_PageGroupBlendsBeforePaperWhenTheBlendIsInsideAForm()
    {
        var form = new PdfFormXObject(10, 10, new PdfContentStreamBuilder()
            .SetGraphicsState(new PdfGraphicsState(blendMode: PdfBlendMode.Difference))
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 5, 10).Fill());
        var document = WithPageGroup(PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10,
            new PdfContentStreamBuilder().DrawForm(form, 0, 0)).Build()));

        var rendered = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(10, 10));

        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rendered.Pixels.Slice((5 * 10 + 2) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, rendered.Pixels.Slice((5 * 10 + 8) * 4, 4).ToArray());
    }

    [Fact]
    public void Render_PageGroupWithNormalBlendingMatchesTheUngroupedPage()
    {
        byte[] Build() => new PdfDocumentBuilder().AddPage(10, 10, new PdfContentStreamBuilder()
            .SetFillRgb(0, 0, 1).Rectangle(0, 0, 10, 10).Fill()
            .SetGraphicsState(new PdfGraphicsState(fillOpacity: 0.5, blendMode: PdfBlendMode.Normal))
            .SetFillRgb(1, 0, 0).Rectangle(0, 0, 5, 10).Fill()).Build();
        var plain = new PdfPageRenderer(PdfDocument.Open(Build())).Render(0, new PdfRenderOptions(10, 10));
        var grouped = new PdfPageRenderer(WithPageGroup(PdfDocument.Open(Build()))).Render(0, new PdfRenderOptions(10, 10));

        Assert.Empty(grouped.Diagnostics);
        Assert.Equal(255, grouped.Pixels.Span[(5 * 10 + 2) * 4 + 3]);
        Assert.NotEqual(255, grouped.Pixels.Span[(5 * 10 + 2) * 4 + 1]);
        Assert.Equal(plain.Pixels.ToArray(), grouped.Pixels.ToArray());
    }

    private static PdfDocument WithPageGroup(PdfDocument document)
    {
        PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
        var catalog = (PdfDictionary)document.Resolve((PdfIndirectReference)document.Trailer[Name("Root")]);
        var pages = (PdfDictionary)document.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)document.Resolve(reference);
        var group = new PdfDictionary([
            new(Name("S"), Name("Transparency")),
            new(Name("CS"), Name("DeviceRGB")),
            new(Name("I"), new PdfBoolean(false))]);
        return PdfDocument.Open(new PdfIncrementalUpdateBuilder(document).ReplaceObject(reference.ObjectNumber,
            new PdfDictionary(page.Append(new(Name("Group"), group)))).Build());
    }
}
