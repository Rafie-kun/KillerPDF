using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfFormFlattenerVisibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void FlattenPreservesWidgetLayerMembership(bool visible, bool appearanceLayer = false, bool annotationLayer = true)
    {
        PdfDocument source = PdfDocument.Open(CreatePdf(4, layerVisible: visible, appearanceLayer: appearanceLayer,
            annotationLayer: annotationLayer));
        AssertControls(Render(source, false), visible);

        PdfDocument flattened = PdfDocument.Open(PdfFormFlattener.Flatten(source));

        Assert.Empty(PdfFormWidgetReader.ReadPage(flattened, 0));
        AssertControls(Render(flattened, false), visible);
        int group = Assert.Single(PdfOptionalContentReader.Read(flattened).Groups).ObjectNumber;
        PdfDocument toggled = PdfDocument.Open(PdfOptionalContentEditor.SetInitialVisibility(flattened, group, !visible));
        AssertControls(Render(toggled, false), !visible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlattenKeepsAppearanceAndOrdinaryXObjectLayerVisibilitySeparate(bool groupVisible)
    {
        PdfDocument original = PdfDocument.Open(CreatePdf(4, layerVisible: groupVisible, annotationLayer: false));
        var update = new PdfIncrementalUpdateBuilder(original);
        var appearance = Assert.IsType<PdfStream>(original.Resolve(new PdfIndirectReference(5, 0)));
        update.ReplaceObject(5, new PdfStream(new PdfDictionary(appearance.Dictionary.Append(
            new KeyValuePair<PdfName, PdfObject>(new PdfName("OC"u8), new PdfIndirectReference(8, 0)))), appearance.EncodedData.Span));
        PdfPageTreeEntry page = PdfPageTree.Read(original).Pages[0];
        var resources = Assert.IsType<PdfDictionary>(page.Dictionary[new PdfName("Resources"u8)]).ToDictionary(pair => pair.Key, pair => pair.Value);
        resources[new PdfName("XObject"u8)] = new PdfDictionary([
            new(new PdfName("Shared"u8), new PdfIndirectReference(5, 0))]);
        var pageEntries = page.Dictionary.ToDictionary(pair => pair.Key, pair => pair.Value);
        pageEntries[new PdfName("Resources"u8)] = new PdfDictionary(resources);
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(pageEntries));
        update.ReplaceObject(6, new PdfStream(new PdfDictionary([]),
            "0 1 0 rg 0 0 10 10 re f q .2 0 0 .2 85 5 cm /Shared Do Q"u8));
        PdfDocument source = PdfDocument.Open(update.Build());
        PdfRenderedPage before = Render(source, false);
        AssertControls(before, true);
        Assert.Equal(groupVisible ? new byte[] { 255, 0, 0, 255 } : new byte[] { 255, 255, 255, 255 }, Pixel(before, 90, 90));
        PdfDocument flattened = PdfDocument.Open(PdfFormFlattener.Flatten(source));
        Assert.Empty(PdfFormWidgetReader.ReadPage(flattened, 0));
        PdfRenderedPage after = Render(flattened, false);
        AssertControls(after, true);
        Assert.Equal(before.Pixels.ToArray(), after.Pixels.ToArray());
    }

    [Theory]
    [InlineData("1 0 0 1 100 0 cm")]
    [InlineData("0 0 10 10 re W n")]
    [InlineData("/G gs")]
    [InlineData("", true)]
    [InlineData("1 0 0 1 100 0 cm", true)]
    [InlineData("0 0 10 10 re W n", true)]
    public void FlattenPreservesArtworkAfterTrailingPageGraphicsState(string trailingContent, bool splitStreams = false)
    {
        PdfDocument source = PdfDocument.Open(CreatePdf(4, trailingContent: trailingContent, splitStreams: splitStreams));
        AssertControls(Render(source, false), true);

        PdfDocument flattened = PdfDocument.Open(PdfFormFlattener.Flatten(source));

        Assert.Empty(PdfFormWidgetReader.ReadPage(flattened, 0));
        AssertControls(Render(flattened, false), true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlattenPreservesLayerScreenAndPrintConfigurations(bool screenVisible)
    {
        PdfDocument source = PdfDocument.Open(CreatePdf(4, layerVisible: screenVisible));
        source = PdfDocument.Open(PdfOptionalContentEditor.SetPrintVisibility(source, 8, !screenVisible));
        AssertControls(Render(source, false), screenVisible);
        AssertControls(Render(source, true), !screenVisible);

        PdfDocument flattened = PdfDocument.Open(PdfFormFlattener.Flatten(source));

        AssertControls(Render(flattened, false), screenVisible);
        AssertControls(Render(flattened, true), !screenVisible);
    }

    [Fact]
    public void FlattenPreservesArtworkOnAPageWithoutContents()
    {
        PdfDocument source = PdfDocument.Open(CreatePdf(4));
        var page = (PdfDictionary)source.Resolve(new PdfIndirectReference(3, 0));
        var update = new PdfIncrementalUpdateBuilder(source);
        update.ReplaceObject(3, new PdfDictionary(page.Where(entry => entry.Key.ValueAsLatin1() != "Contents")));
        source = PdfDocument.Open(update.Build());
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(Render(source, false), 50, 50));

        PdfDocument flattened = PdfDocument.Open(PdfFormFlattener.Flatten(source));

        Assert.Empty(PdfFormWidgetReader.ReadPage(flattened, 0));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(Render(flattened, false), 50, 50));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(Render(flattened, false), 5, 95));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlattenPreservesMembershipVisibilityExpressions(bool groupVisible)
    {
        PdfDocument source = PdfDocument.Open(CreatePdf(4, layerVisible: groupVisible));
        var update = new PdfIncrementalUpdateBuilder(source);
        var widget = (PdfDictionary)source.Resolve(new PdfIndirectReference(4, 0));
        var entries = widget.ToDictionary(item => item.Key, item => item.Value);
        entries[new PdfName("OC"u8)] = new PdfDictionary(new Dictionary<PdfName, PdfObject>
        {
            [new PdfName("Type"u8)] = new PdfName("OCMD"u8),
            [new PdfName("OCGs"u8)] = new PdfArray([new PdfIndirectReference(8, 0)]),
            [new PdfName("VE"u8)] = new PdfArray([new PdfName("Not"u8), new PdfIndirectReference(8, 0)])
        });
        update.ReplaceObject(4, new PdfDictionary(entries));
        source = PdfDocument.Open(update.Build());
        AssertControls(Render(source, false), !groupVisible);

        PdfDocument flattened = PdfDocument.Open(PdfFormFlattener.Flatten(source));

        AssertControls(Render(flattened, false), !groupVisible);
        flattened = PdfDocument.Open(PdfOptionalContentEditor.SetInitialVisibility(flattened, 8, !groupVisible));
        AssertControls(Render(flattened, false), groupVisible);
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(0, true, false)]
    [InlineData(2, false, false)]
    [InlineData(2, true, false)]
    [InlineData(6, false, false)]
    [InlineData(6, true, false)]
    [InlineData(38, false, false)]
    [InlineData(38, true, false)]
    [InlineData(4, false, true)]
    [InlineData(4, true, true)]
    [InlineData(32, false, false)]
    [InlineData(32, true, false)]
    [InlineData(36, false, false)]
    [InlineData(36, true, true)]
    public void OriginalWidgetVisibilityHasLiteralAndRemovedTargetControls(
        int flags, bool printing, bool visible)
    {
        PdfDocument source = PdfDocument.Open(CreatePdf(flags));
        Assert.Single(PdfFormWidgetReader.ReadPage(source, 0));
        var editor = new PdfIncrementalPageEditor(source);
        editor.RemoveFormField("target");
        PdfDocument removed = PdfDocument.Open(editor.Build());
        Assert.Empty(PdfFormWidgetReader.ReadPage(removed, 0));
        PdfRenderedPage original = Render(source, printing);
        PdfRenderedPage removedPage = Render(removed, printing);
        PdfRenderedPage absentPage = Render(PdfDocument.Open(CreatePdf(flags, false)), printing);

        AssertControls(original, visible);
        AssertControls(removedPage, false);
        AssertControls(absentPage, false);
        Assert.Equal(absentPage.Pixels.ToArray(), removedPage.Pixels.ToArray());
        if (!visible) Assert.Equal(removedPage.Pixels.ToArray(), original.Pixels.ToArray());
        else Assert.False(removedPage.Pixels.Span.SequenceEqual(original.Pixels.Span));
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(0, true, true)]
    [InlineData(2, false, false)]
    [InlineData(2, true, false)]
    [InlineData(6, false, false)]
    [InlineData(6, true, false)]
    [InlineData(38, false, false)]
    [InlineData(38, true, false)]
    [InlineData(4, false, true)]
    [InlineData(4, true, true)]
    [InlineData(32, false, false)]
    [InlineData(32, true, false)]
    [InlineData(36, false, false)]
    [InlineData(36, true, true)]
    public void FlattenPreservesHiddenAndPrintOnlyVisibility(int flags, bool printing, bool visible)
    {
        byte[] original = CreatePdf(flags);
        byte[] flattened = PdfFormFlattener.Flatten(PdfDocument.Open(original));
        PdfDocument reopened = PdfDocument.Open(flattened);
        Assert.Empty(PdfFormWidgetReader.ReadPage(reopened, 0));
        AssertControls(Render(reopened, printing), visible);
        AssertControls(new PdfPageRenderer(reopened).Render(0,
            new PdfRenderOptions(100, 100, includeFormFields: false)
            { ForPrinting = printing }), visible);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    public void FlattenMustNotExposeAWidgetInvisibleInBothModes(int flags, bool printing)
    {
        PdfDocument source = PdfDocument.Open(CreatePdf(flags));
        AssertControls(Render(source, printing), false);
        PdfDocument reopened = PdfDocument.Open(PdfFormFlattener.Flatten(source));
        Assert.Empty(PdfFormWidgetReader.ReadPage(reopened, 0));

        AssertControls(Render(reopened, printing), false);
    }

    [Fact]
    public void PrintOnlyWidgetHasDifferentScreenAndPrintArtworkBeforeFlattening()
    {
        PdfDocument source = PdfDocument.Open(CreatePdf(36));
        AssertControls(Render(source, false), false);
        AssertControls(Render(source, true), true);
        PdfDocument reopened = PdfDocument.Open(PdfFormFlattener.Flatten(source));
        Assert.Empty(PdfFormWidgetReader.ReadPage(reopened, 0));
        AssertControls(Render(reopened, false), false);
        AssertControls(Render(reopened, true), true);
    }

    [Fact]
    public void PrintOnlyAppearanceIsASeparateAnnotationWithoutFieldDataOrActions()
    {
        PdfDocument source = PdfDocument.Open(CreatePdf(36));
        var update = new PdfIncrementalUpdateBuilder(source);
        var widget = Assert.IsType<PdfDictionary>(source.Resolve(new PdfIndirectReference(4, 0)));
        var entries = widget.ToDictionary(item => item.Key, item => item.Value);
        entries[new PdfName("AA"u8)] = new PdfDictionary(new Dictionary<PdfName, PdfObject>
        {
            [new PdfName("K"u8)] = new PdfDictionary(new Dictionary<PdfName, PdfObject>
            {
                [new PdfName("S"u8)] = new PdfName("JavaScript"u8),
                [new PdfName("JS"u8)] = new PdfString("event.value = 'changed'"u8, PdfStringForm.Literal)
            })
        });
        update.ReplaceObject(4, new PdfDictionary(entries));
        var existing = update.AddObject(new PdfDictionary(new Dictionary<PdfName, PdfObject>
        {
            [new PdfName("Type"u8)] = new PdfName("Annot"u8),
            [new PdfName("Subtype"u8)] = new PdfName("Text"u8),
            [new PdfName("F"u8)] = new PdfInteger(2),
            [new PdfName("Rect"u8)] = new PdfArray([new PdfInteger(0), new PdfInteger(0),
                new PdfInteger(10), new PdfInteger(10)])
        }));
        var page = PdfPageTree.Read(source).Pages[0];
        var pageEntries = page.Dictionary.ToDictionary(item => item.Key, item => item.Value);
        pageEntries[new PdfName("Annots"u8)] = new PdfArray([new PdfIndirectReference(4, 0), existing]);
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(pageEntries));
        byte[] sourceBytes = update.Build();
        byte[] flattened = PdfFormFlattener.Flatten(PdfDocument.Open(sourceBytes));
        PdfDocument reopened = PdfDocument.Open(flattened);

        Assert.True(flattened.AsSpan(0, sourceBytes.Length).SequenceEqual(sourceBytes));
        Assert.Empty(PdfFormWidgetReader.ReadPage(reopened, 0));
        var annotations = Assert.IsType<PdfArray>(PdfPageTree.Read(reopened).Pages[0]
            .Dictionary[new PdfName("Annots"u8)]);
        Assert.Equal(2, annotations.Count);
        Assert.Contains(annotations, value => value is PdfIndirectReference reference
            && reference.ObjectNumber == existing.ObjectNumber);
        var stampReference = Assert.IsType<PdfIndirectReference>(annotations.Single(value =>
            value is PdfIndirectReference reference && reference.ObjectNumber != existing.ObjectNumber));
        Assert.NotEqual(4, stampReference.ObjectNumber);
        var stamp = Assert.IsType<PdfDictionary>(reopened.Resolve(stampReference));
        Assert.Equal("Stamp", Assert.IsType<PdfName>(stamp[new PdfName("Subtype"u8)]).ValueAsLatin1());
        Assert.Equal(740, Assert.IsType<PdfInteger>(stamp[new PdfName("F"u8)]).Value);
        Assert.False(stamp.ContainsKey(new PdfName("FT"u8)));
        Assert.False(stamp.ContainsKey(new PdfName("V"u8)));
        Assert.False(stamp.ContainsKey(new PdfName("AA"u8)));
        Assert.False(stamp.ContainsKey(new PdfName("A"u8)));
        AssertControls(Render(reopened, false), false);
        AssertControls(Render(reopened, true), true);
        AssertControls(new PdfPageRenderer(reopened).Render(0,
            new PdfRenderOptions(100, 100, includeAnnotations: false)
            { ForPrinting = true }), false);
        Assert.Equal(flattened, PdfFormFlattener.Flatten(reopened));
    }

    private static PdfRenderedPage Render(PdfDocument document, bool printing) =>
        new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(100, 100) { ForPrinting = printing });

    private static void AssertControls(PdfRenderedPage page, bool visible)
    {
        Assert.Equal(100, page.Width);
        Assert.Equal(100, page.Height);
        Assert.Empty(page.Diagnostics);
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(page, 5, 95));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(page, 95, 5));
        Assert.Equal(visible ? new byte[] { 255, 0, 0, 255 }
            : new byte[] { 255, 255, 255, 255 }, Pixel(page, 50, 50));
    }

    private static byte[] Pixel(PdfRenderedPage page, int x, int y) =>
        page.Pixels.Slice((y * page.Width + x) * 4, 4).ToArray();

    private static byte[] CreatePdf(int flags, bool includeTarget = true, string trailingContent = "", bool splitStreams = false,
        bool? layerVisible = null, bool appearanceLayer = false, bool annotationLayer = true)
    {
        string appearance = appearanceLayer
            ? "/OC /Layer BDC 0 0 1 rg 0 0 60 60 re f EMC" : "0 0 1 rg 0 0 60 60 re f";
        string content = splitStreams ? "0 1 0 rg" : "0 1 0 rg 0 0 10 10 re f " + trailingContent;
        string secondContent = "0 0 10 10 re f " + trailingContent;
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R "
                + (layerVisible.HasValue ? "/OCProperties << /OCGs [8 0 R] /D << /BaseState /ON /OFF "
                    + (layerVisible.Value ? "[]" : "[8 0 R]") + " >> >> " : "")
                + (includeTarget ? "/AcroForm << /Fields [4 0 R] >> " : "") + ">>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ExtGState << /G << /ca 0 >> >> >> /Contents "
                + (splitStreams ? "[6 0 R 7 0 R] " : "6 0 R ")
                + (includeTarget ? "/Annots [4 0 R] " : "") + ">>",
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T (target) /V () /F {flags} /Rect [20 20 80 80] /P 3 0 R /AP << /N 5 0 R >> "
                + (layerVisible.HasValue && annotationLayer ? "/OC 8 0 R " : "") + ">>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 60 60] /Resources "
                + (appearanceLayer ? "<< /Properties << /Layer 8 0 R >> >> " : "<< >> ")
                + $"/Length {appearance.Length} >>\nstream\n{appearance}\nendstream",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Length {secondContent.Length} >>\nstream\n{secondContent}\nendstream",
            "<< /Type /OCG /Name (Field layer) >>"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
