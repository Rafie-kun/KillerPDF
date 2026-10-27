using System.Globalization;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfDuplicatePageRecoveryTests
{
    [Fact]
    public void TaggedFormAndCommentFixtureIsReadableBeforeDuplicatingItsPageReference()
    {
        PdfDocument source = PdfDocument.Open(Fixture());
        Assert.Single(PdfPageInformation.Read(source));
        Assert.Equal("shared", Assert.Single(PdfFormWidgetReader.ReadPage(source, 0)).FieldName);
        Assert.Equal("Ada", Assert.Single(PdfFormWidgetReader.ReadPage(source, 0)).Value);
        Assert.Equal(4, Annotations(source, 0).Count);
        var root = Dictionary(source, PdfPageTree.Read(source).Catalog[Name("StructTreeRoot")]);
        var parentTree = Dictionary(source, root[Name("ParentTree")]);
        Assert.Equal(8, Assert.IsType<PdfArray>(parentTree[Name("Nums")]).Count);
        byte[] pixels = new PdfPageRenderer(source).Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray();
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels.AsSpan((20 * 100 + 15) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, pixels.AsSpan((10 * 100 + 95) * 4, 4).ToArray());
        Assert.Equal(pixels, new PdfPageRenderer(PdfDocument.Open(PdfDocumentWriter.Write(source)))
            .Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray());
    }

    [Fact]
    public void CompatibilityRecoversDuplicatePagesWithoutSharingAnnotationOrTagOwnership()
    {
        byte[] source = DuplicatePageReference();
        Assert.Throws<InvalidOperationException>(() => PdfPageInformation.Read(PdfDocument.Open(source)));
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(source);
        Assert.Equal(2, PdfPageInformation.Read(recovered).Count);
        PdfPageTree tree = PdfPageTree.Read(recovered);
        Assert.NotEqual(tree.Pages[0].Reference.ObjectNumber, tree.Pages[1].Reference.ObjectNumber);
        PdfFormWidgetInfo firstWidget = Assert.Single(PdfFormWidgetReader.ReadPage(recovered, 0));
        PdfFormWidgetInfo secondWidget = Assert.Single(PdfFormWidgetReader.ReadPage(recovered, 1));
        Assert.Equal("shared", firstWidget.FieldName);
        Assert.Equal(firstWidget.FieldName, secondWidget.FieldName);
        Assert.Equal("Ada", secondWidget.Value);
        Assert.NotEqual(firstWidget.ObjectNumber, secondWidget.ObjectNumber);
        Assert.Empty(Annotations(recovered, 0).Select(value => Assert.IsType<PdfIndirectReference>(value).ObjectNumber)
            .Intersect(Annotations(recovered, 1).Select(value => Assert.IsType<PdfIndirectReference>(value).ObjectNumber)));
        Assert.NotEqual(Assert.IsType<PdfInteger>(tree.Pages[0].Dictionary[Name("StructParents")]).Value,
            Assert.IsType<PdfInteger>(tree.Pages[1].Dictionary[Name("StructParents")]).Value);
        Assert.NotEmpty(recovered.RecoveryDiagnostics);

        var renderer = new PdfPageRenderer(recovered);
        Assert.Equal(renderer.Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray(),
            renderer.Render(1, new PdfRenderOptions(100, 100)).Pixels.ToArray());
        var editor = new PdfIncrementalPageEditor(recovered);
        editor.SetTextFieldValue("shared", "Grace");
        PdfDocument updated = PdfDocument.Open(editor.Build());
        Assert.Equal("Grace", Assert.Single(PdfFormWidgetReader.ReadPage(updated, 0)).Value);
        Assert.Equal("Grace", Assert.Single(PdfFormWidgetReader.ReadPage(updated, 1)).Value);

        var moved = new PdfIncrementalPageEditor(updated);
        moved.SetFormWidgetRectangle(secondWidget.ObjectNumber, secondWidget.Generation, 10, 35, 90, 55);
        PdfDocument reopened = PdfDocument.Open(moved.Build());
        Assert.Equal(10, Assert.Single(PdfFormWidgetReader.ReadPage(reopened, 0)).Bottom);
        Assert.Equal(35, Assert.Single(PdfFormWidgetReader.ReadPage(reopened, 1)).Bottom);
    }

    private static byte[] DuplicatePageReference()
    {
        PdfDocument source = PdfDocument.Open(Fixture());
        PdfPageTree tree = PdfPageTree.Read(source);
        var root = Dictionary(source, tree.RootReference).ToDictionary(item => item.Key, item => item.Value);
        root[Name("Kids")] = new PdfArray([tree.Pages[0].Reference, tree.Pages[0].Reference]);
        root[Name("Count")] = new PdfInteger(2);
        return new PdfIncrementalUpdateBuilder(source).ReplaceObject(tree.RootReference.ObjectNumber,
            new PdfDictionary(root)).Build();
    }

    private static PdfArray Annotations(PdfDocument source, int page) =>
        Assert.IsType<PdfArray>(PdfPageTree.Read(source).Pages[page].Dictionary[Name("Annots")]);

    private static PdfDictionary Dictionary(PdfDocument source, PdfObject value) =>
        Assert.IsType<PdfDictionary>(value is PdfIndirectReference reference ? source.Resolve(reference) : value);

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static byte[] Fixture()
    {
        const string content = "/P << /MCID 0 >> BDC 1 0 0 rg 5 65 25 30 re f EMC";
        const string appearance = "BT /F1 12 Tf 0 g 2 6 Td (Ada) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DR << /Font << /F1 4 0 R >> >> /DA (/F1 12 Tf 0 g) >> /MarkInfo << /Marked true >> /StructTreeRoot 10 0 R /Names << /Dests << /Names [(first) [3 0 R /Fit]] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 4 0 R >> >> /Contents 9 0 R /Annots [5 0 R 6 0 R 7 0 R 8 0 R] /StructParents 0 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (shared) /V (Ada) /DV (Original) /F 4 /Rect [10 10 90 30] /DA (/F1 12 Tf 0 g) /AP << /N 15 0 R >> /P 3 0 R /StructParent 1 >>",
            "<< /Type /Annot /Subtype /Text /Contents (note) /NM (note) /F 4 /Rect [60 60 70 70] /P 3 0 R /Popup 8 0 R /StructParent 2 >>",
            "<< /Type /Annot /Subtype /Text /Contents (reply) /NM (reply) /F 4 /Rect [70 60 80 70] /P 3 0 R /IRT 6 0 R /RT /R /StructParent 3 >>",
            "<< /Type /Annot /Subtype /Popup /Parent 6 0 R /P 3 0 R /Rect [60 60 90 90] /Open false >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /StructTreeRoot /K [11 0 R] /ParentTree 16 0 R /ParentTreeNextKey 4 >>",
            "<< /Type /StructElem /S /Document /P 10 0 R /K [12 0 R 13 0 R 14 0 R 17 0 R] >>",
            "<< /Type /StructElem /S /P /P 11 0 R /Pg 3 0 R /K 0 >>",
            "<< /Type /StructElem /S /Form /P 11 0 R /Pg 3 0 R /K << /Type /OBJR /Pg 3 0 R /Obj 5 0 R >> >>",
            "<< /Type /StructElem /S /Note /P 11 0 R /Pg 3 0 R /K << /Type /OBJR /Pg 3 0 R /Obj 6 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 80 20] /Resources << /Font << /F1 4 0 R >> >> /Length {appearance.Length} >>\nstream\n{appearance}\nendstream",
            "<< /Nums [0 [12 0 R] 1 13 0 R 2 14 0 R 3 17 0 R] >>",
            "<< /Type /StructElem /S /Note /P 11 0 R /Pg 3 0 R /K << /Type /OBJR /Pg 3 0 R /Obj 7 0 R >> >>"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
