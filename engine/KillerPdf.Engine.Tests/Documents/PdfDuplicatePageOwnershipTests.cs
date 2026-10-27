using System.Globalization;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfDuplicatePageOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WritersRecoverBeforeEnumerationAndKeepIndependentComments(bool incremental)
    {
        byte[] damaged = Fixture();
        PdfDocument source = PdfDocument.OpenWithCompatibilityRecovery(damaged);
        byte[] saved;
        if (incremental)
        {
            var update = new PdfIncrementalUpdateBuilder(source);
            PdfIndirectReference information = update.AddObject(new PdfDictionary([
                new(N("Title"), Text("saved before enumeration"))]));
            saved = update.SetDocumentInformation(information).Build();
        }
        else saved = PdfDocumentWriter.Write(source);
        PdfDocument reopened = PdfDocument.Open(saved);
        Assert.Equal(2, PdfPageInformation.Read(reopened).Count);
        Assert.Empty(reopened.RecoveryDiagnostics);
        PdfPageTree tree = PdfPageTree.Read(reopened);
        Assert.Equal(new PdfPageRenderer(reopened).Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray(),
            new PdfPageRenderer(reopened).Render(1, new PdfRenderOptions(100, 100)).Pixels.ToArray());
        PdfDictionary first = Annotation(reopened, 0, 0), second = Annotation(reopened, 1, 0);
        string secondName = Encoding.ASCII.GetString(Assert.IsType<PdfString>(second[N("NM")]).Bytes.Span);
        Assert.NotEqual("note", secondName);
        var edited = new PdfIncrementalAnnotationEditor(reopened).SetAnnotationContents(1, secondName, "second only");
        PdfDocument changed = PdfDocument.Open(edited.Build());
        Assert.Equal("original", String(Annotation(changed, 0, 0)[N("Contents")]));
        Assert.Equal("second only", String(Annotation(changed, 1, 0)[N("Contents")]));
        for (int index = 0; index < 2; index++)
        {
            PdfArray annotations = Assert.IsType<PdfArray>(tree.Pages[index].Dictionary[N("Annots")]);
            PdfDictionary note = Annotation(reopened, index, 0), reply = Annotation(reopened, index, 1), popup = Annotation(reopened, index, 2);
            Assert.Equal(ObjectNumber(annotations[2]), ObjectNumber(note[N("Popup")]));
            Assert.Equal(ObjectNumber(annotations[0]), ObjectNumber(reply[N("IRT")]));
            Assert.Equal(ObjectNumber(annotations[0]), ObjectNumber(popup[N("Parent")]));
            Assert.All(annotations, value => Assert.Equal(tree.Pages[index].Reference.ObjectNumber,
                ObjectNumber(Dictionary(reopened, value)[N("P")])));
        }
        if (incremental) Assert.Equal(damaged, saved.AsSpan(0, damaged.Length).ToArray());
    }

    [Fact]
    public void CopiedExplicitSelfLinksFollowTheirOccurrenceAndNamedIncomingLinksStayCanonical()
    {
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(Fixture());
        PdfPageTree tree = PdfPageTree.Read(recovered);
        Assert.Equal(2, tree.Pages.Count);
        for (int page = 0; page < 2; page++)
        {
            PdfDictionary link = Annotation(recovered, page, 3);
            Assert.Equal(tree.Pages[page].Reference.ObjectNumber, ObjectNumber(Assert.IsType<PdfArray>(link[N("Dest")])[0]));
            PdfDictionary action = Dictionary(recovered, Annotation(recovered, page, 4)[N("A")]);
            Assert.Equal(tree.Pages[page].Reference.ObjectNumber, ObjectNumber(Assert.IsType<PdfArray>(action[N("D")])[0]));
        }
        PdfDictionary names = Dictionary(recovered, tree.Catalog[N("Names")]);
        PdfDictionary destinations = Dictionary(recovered, names[N("Dests")]);
        Assert.Equal(tree.Pages[0].Reference.ObjectNumber,
            ObjectNumber(Assert.IsType<PdfArray>(Assert.IsType<PdfArray>(destinations[N("Names")])[1])[0]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TaggedWidgetAndCommentOwnershipSurvivesSavingAndSharedFieldEdits(bool incremental)
    {
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(Fixture(tagged: true));
        byte[] saved;
        if (incremental)
        {
            var update = new PdfIncrementalUpdateBuilder(recovered);
            saved = update.SetDocumentInformation(update.AddObject(new PdfDictionary([
                new(N("Title"), Text("tagged save"))]))).Build();
        }
        else saved = PdfDocumentWriter.Write(recovered);
        PdfDocument document = PdfDocument.Open(saved);
        PdfPageTree tree = PdfPageTree.Read(document);
        Assert.Equal(2, tree.Pages.Count);
        PdfDictionary structure = Dictionary(document, tree.Catalog[N("StructTreeRoot")]);
        PdfArray nums = Assert.IsType<PdfArray>(Dictionary(document, structure[N("ParentTree")])[N("Nums")]);
        Assert.Equal(12, nums.Count);
        var parents = Enumerable.Range(0, nums.Count / 2).ToDictionary(index =>
            Assert.IsType<PdfInteger>(nums[index * 2]).Value, index => nums[index * 2 + 1]);
        PdfDictionary sharedAncestor = Dictionary(document, Assert.Single(Assert.IsType<PdfArray>(structure[N("K")])));
        PdfArray children = Assert.IsType<PdfArray>(sharedAncestor[N("K")]);
        Assert.Equal(6, children.Count);
        var ownerKeys = new HashSet<long>();
        for (int page = 0; page < 2; page++)
        {
            int pageNumber = tree.Pages[page].Reference.ObjectNumber;
            long pageKey = Assert.IsType<PdfInteger>(tree.Pages[page].Dictionary[N("StructParents")]).Value;
            Assert.True(ownerKeys.Add(pageKey));
            PdfObject paragraph = Assert.Single(Assert.IsType<PdfArray>(parents[pageKey]));
            Assert.Equal(pageNumber, ObjectNumber(Dictionary(document, paragraph)[N("Pg")]));
            Assert.Equal(0, Assert.IsType<PdfInteger>(Dictionary(document, paragraph)[N("K")]).Value);
            foreach (int index in new[] { 0, 5 })
            {
                PdfArray annotations = Assert.IsType<PdfArray>(tree.Pages[page].Dictionary[N("Annots")]);
                PdfDictionary annotation = Annotation(document, page, index);
                long key = Assert.IsType<PdfInteger>(annotation[N("StructParent")]).Value;
                Assert.True(ownerKeys.Add(key));
                PdfDictionary element = Dictionary(document, parents[key]);
                Assert.Equal(pageNumber, ObjectNumber(element[N("Pg")]));
                Assert.Equal(ObjectNumber(annotations[index]), ObjectNumber(Dictionary(document, element[N("K")])[N("Obj")]));
                Assert.Contains(children, child => ObjectNumber(child) == ObjectNumber(parents[key]));
            }
        }
        PdfFormWidgetInfo first = Assert.Single(PdfFormWidgetReader.ReadPage(document, 0));
        PdfFormWidgetInfo second = Assert.Single(PdfFormWidgetReader.ReadPage(document, 1));
        Assert.NotEqual(first.ObjectNumber, second.ObjectNumber);
        PdfDictionary form = Dictionary(document, tree.Catalog[N("AcroForm")]);
        PdfObject logicalField = Assert.Single(Assert.IsType<PdfArray>(form[N("Fields")]));
        Assert.Equal("Original", String(Dictionary(document, logicalField)[N("DV")]));
        Assert.Equal(ObjectNumber(logicalField), ObjectNumber(Assert.Single(Assert.IsType<PdfArray>(form[N("CO")]))));
        var editor = new PdfIncrementalPageEditor(document).SetTextFieldValue("shared", "Grace");
        PdfDocument changed = PdfDocument.Open(editor.Build());
        Assert.Equal("Grace", Assert.Single(PdfFormWidgetReader.ReadPage(changed, 0)).Value);
        Assert.Equal("Grace", Assert.Single(PdfFormWidgetReader.ReadPage(changed, 1)).Value);
        PdfDocument reset = PdfDocument.Open(new PdfIncrementalPageEditor(changed).ResetFormField("shared").Build());
        Assert.Equal("Original", Assert.Single(PdfFormWidgetReader.ReadPage(reset, 0)).Value);
        Assert.Equal("Original", Assert.Single(PdfFormWidgetReader.ReadPage(reset, 1)).Value);
        PdfDocument moved = PdfDocument.Open(new PdfIncrementalPageEditor(reset)
            .SetFormWidgetRectangle(second.ObjectNumber, second.Generation, 10, 35, 90, 55).Build());
        Assert.Equal(10, Assert.Single(PdfFormWidgetReader.ReadPage(moved, 0)).Bottom);
        Assert.Equal(35, Assert.Single(PdfFormWidgetReader.ReadPage(moved, 1)).Bottom);
    }

    [Fact]
    public void IndirectWidgetSubtypeKeepsOneLogicalFieldAfterRecovery()
    {
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(Fixture(tagged: true, damage: "indirect-widget"));
        Assert.Equal("Ada", Assert.Single(PdfFormWidgetReader.ReadPage(recovered, 0)).Value);
        Assert.Equal("Ada", Assert.Single(PdfFormWidgetReader.ReadPage(recovered, 1)).Value);
        PdfDocument changed = PdfDocument.Open(new PdfIncrementalPageEditor(recovered).SetTextFieldValue("shared", "Grace").Build());
        Assert.Equal("Grace", Assert.Single(PdfFormWidgetReader.ReadPage(changed, 0)).Value);
        Assert.Equal("Grace", Assert.Single(PdfFormWidgetReader.ReadPage(changed, 1)).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveredCalculationFieldsShareOneLogicalValueAndOrder(bool incremental)
    {
        PdfDocument healthy = PdfDocument.Open(Fixture(tagged: true, damage: "calculations", repeated: false));
        Assert.Single(PdfPageInformation.Read(healthy));
        Assert.Equal("6", PdfFormCalculation.Evaluate(healthy).Fields.Single(field => field.FieldName == "total").Value);

        PdfDocument source = PdfDocument.OpenWithCompatibilityRecovery(Fixture(tagged: true, damage: "calculations"));
        byte[] saved;
        if (incremental)
        {
            var update = new PdfIncrementalUpdateBuilder(source);
            saved = update.SetDocumentInformation(update.AddObject(new PdfDictionary([
                new(N("Title"), Text("calculation recovery"))]))).Build();
        }
        else saved = PdfDocumentWriter.Write(source);
        PdfDocument document = PdfDocument.Open(saved);
        Assert.Equal(2, PdfPageInformation.Read(document).Count);
        PdfDictionary form = Dictionary(document, PdfPageTree.Read(document).Catalog[N("AcroForm")]);
        PdfArray fields = Assert.IsType<PdfArray>(form[N("Fields")]);
        Assert.Equal(2, fields.Count);
        PdfObject total = fields.Single(field => String(Dictionary(document, field)[N("T")]) == "total");
        Assert.Equal(ObjectNumber(total), ObjectNumber(Assert.Single(Assert.IsType<PdfArray>(form[N("CO")]))));
        Assert.Equal("total", Assert.Single(PdfFormCalculation.ReadScripts(document)).FieldName);
        Assert.Equal(4, Enumerable.Range(0, 2).SelectMany(page => PdfFormWidgetReader.ReadPage(document, page))
            .Select(widget => widget.ObjectNumber).Distinct().Count());

        PdfDocument changed = PdfDocument.Open(new PdfIncrementalPageEditor(document).SetTextFieldValue("quantity", "4").Build());
        PdfFormCalculationReport report = PdfFormCalculation.Evaluate(changed);
        Assert.Equal(2, report.Fields.Count);
        Assert.Equal("8", report.Fields.Single(field => field.FieldName == "total").Value);
        Assert.Equal(0, report.FailedCount);
        Assert.Equal(0, report.UnsupportedCount);
        changed = PdfDocument.Open(new PdfIncrementalPageEditor(changed).SetTextFieldValue("total", "8").Build());
        for (int page = 0; page < 2; page++)
        {
            IReadOnlyList<PdfFormWidgetInfo> widgets = PdfFormWidgetReader.ReadPage(changed, page);
            Assert.Equal("4", widgets.Single(widget => widget.FieldName == "quantity").Value);
            Assert.Equal("8", widgets.Single(widget => widget.FieldName == "total").Value);
        }
        PdfDocument reset = PdfDocument.Open(new PdfIncrementalPageEditor(changed).ResetFormField("quantity").Build());
        Assert.Equal("4", PdfFormCalculation.Evaluate(reset).Fields.Single(field => field.FieldName == "total").Value);
        Assert.All(Enumerable.Range(0, 2), page => Assert.Equal("2",
            PdfFormWidgetReader.ReadPage(reset, page).Single(widget => widget.FieldName == "quantity").Value));
    }

    [Fact]
    public void PopupsOutsideTheAnnotationArrayKeepTheirOwnPageAndNote()
    {
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(Fixture(damage: "external-popup"));
        Assert.Equal(2, PdfPageInformation.Read(recovered).Count);
        PdfDocument saved = PdfDocument.Open(PdfDocumentWriter.Write(recovered));
        PdfPageTree tree = PdfPageTree.Read(saved);
        var popups = new HashSet<int>();
        for (int page = 0; page < 2; page++)
        {
            PdfArray annotations = Assert.IsType<PdfArray>(tree.Pages[page].Dictionary[N("Annots")]);
            Assert.Equal(4, annotations.Count);
            PdfDictionary note = Annotation(saved, page, 0);
            PdfIndirectReference popupReference = Assert.IsType<PdfIndirectReference>(note[N("Popup")]);
            Assert.True(popups.Add(popupReference.ObjectNumber));
            Assert.DoesNotContain(annotations, value => ObjectNumber(value) == popupReference.ObjectNumber);
            PdfDictionary popup = Dictionary(saved, popupReference);
            Assert.Equal(tree.Pages[page].Reference.ObjectNumber, ObjectNumber(popup[N("P")]));
            Assert.Equal(ObjectNumber(annotations[0]), ObjectNumber(popup[N("Parent")]));
        }
    }

    [Fact]
    public void RecoveredTaggedPagesImportWithSharedFieldsAndSeparateOwners()
    {
        PdfDocument source = PdfDocument.OpenWithCompatibilityRecovery(Fixture(tagged: true));
        PdfDocument destination = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100, System.Array.Empty<byte>()).Build());
        PdfDocument imported = PdfDocument.Open(new PdfIncrementalPageEditor(destination).InsertImportedDocument(1, source).Build());
        Assert.Equal(3, PdfPageInformation.Read(imported).Count);
        PdfFormWidgetInfo first = Assert.Single(PdfFormWidgetReader.ReadPage(imported, 1));
        PdfFormWidgetInfo second = Assert.Single(PdfFormWidgetReader.ReadPage(imported, 2));
        Assert.Equal(first.FieldName, second.FieldName);
        Assert.NotEqual(first.ObjectNumber, second.ObjectNumber);
        PdfDocument updated = PdfDocument.Open(new PdfIncrementalPageEditor(imported).SetTextFieldValue(first.FieldName, "Grace").Build());
        Assert.Equal("Grace", Assert.Single(PdfFormWidgetReader.ReadPage(updated, 1)).Value);
        Assert.Equal("Grace", Assert.Single(PdfFormWidgetReader.ReadPage(updated, 2)).Value);
        PdfPageTree tree = PdfPageTree.Read(imported);
        long firstKey = Assert.IsType<PdfInteger>(tree.Pages[1].Dictionary[N("StructParents")]).Value;
        long secondKey = Assert.IsType<PdfInteger>(tree.Pages[2].Dictionary[N("StructParents")]).Value;
        Assert.NotEqual(firstKey, secondKey);
        PdfDictionary structure = Dictionary(imported, tree.Catalog[N("StructTreeRoot")]);
        IReadOnlyList<PdfNumberTreeEntry> parents = PdfNumberTree.Read(imported, structure[N("ParentTree")]);
        foreach (int page in new[] { 1, 2 })
        {
            long key = Assert.IsType<PdfInteger>(tree.Pages[page].Dictionary[N("StructParents")]).Value;
            PdfArray slots = Assert.IsType<PdfArray>(Assert.Single(parents, entry => entry.Key == key).Value);
            Assert.Equal(tree.Pages[page].Reference.ObjectNumber, ObjectNumber(Dictionary(imported, Assert.Single(slots))[N("Pg")]));
            PdfDictionary note = Annotation(imported, page, 0);
            Assert.Equal(tree.Pages[page].Reference.ObjectNumber, ObjectNumber(Dictionary(imported, note[N("Popup")])[N("P")]));
        }
    }

    [Fact]
    public void RadioWidgetOrderAndExportOptionsSurviveDuplication()
    {
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(Fixture(damage: "radio-order"));
        PdfPageTree tree = PdfPageTree.Read(recovered);
        Assert.Equal(3, tree.Pages.Count);
        PdfDictionary form = Dictionary(recovered, tree.Catalog[N("AcroForm")]);
        PdfDictionary field = Dictionary(recovered, Assert.Single(Assert.IsType<PdfArray>(form[N("Fields")])));
        PdfArray widgets = Assert.IsType<PdfArray>(field[N("Kids")]);
        Assert.Equal(3, widgets.Count);
        for (int page = 0; page < 3; page++)
            Assert.Equal(ObjectNumber(Assert.Single(Assert.IsType<PdfArray>(tree.Pages[page].Dictionary[N("Annots")]))), ObjectNumber(widgets[page]));
        Assert.Equal(new[] { "first", "first", "second" }, Assert.IsType<PdfArray>(field[N("Opt")]).Select(String));
        Assert.Equal("On1", Assert.IsType<PdfName>(field[N("V")]).ValueAsLatin1());
        PdfDocument changed = PdfDocument.Open(new PdfIncrementalPageEditor(recovered).SetRadioButtonValue("choice", "On2").Build());
        Assert.All(Enumerable.Range(0, 3), page => Assert.Equal("/On2", Assert.Single(PdfFormWidgetReader.ReadPage(changed, page)).Value));
    }

    [Theory]
    [InlineData("page-aa")]
    [InlineData("widget-aa")]
    [InlineData("action-next")]
    public void SecondaryLocalActionsFollowTheirCopiedPage(string location)
    {
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(Fixture(tagged: true, damage: location));
        PdfPageTree tree = PdfPageTree.Read(recovered);
        for (int page = 0; page < 2; page++)
        {
            PdfDictionary action = location switch
            {
                "page-aa" => Dictionary(recovered, Dictionary(recovered, tree.Pages[page].Dictionary[N("AA")])[N("O")]),
                "widget-aa" => Dictionary(recovered, Dictionary(recovered, Annotation(recovered, page, 5)[N("AA")])[N("U")]),
                _ => Dictionary(recovered, Dictionary(recovered, Annotation(recovered, page, 4)[N("A")])[N("Next")])
            };
            Assert.Equal(tree.Pages[page].Reference.ObjectNumber, ObjectNumber(Assert.IsType<PdfArray>(action[N("D")])[0]));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClonedCommentNamesAvoidUntouchedPageNamesInEitherEncoding(bool unicode)
    {
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(Fixture(damage: unicode ? "collision-utf16" : "collision-latin"));
        Assert.Equal(3, PdfPageInformation.Read(recovered).Count);
        string cloneName = String(Annotation(recovered, 1, 0)[N("NM")]);
        Assert.NotEqual("recovered-annotation-14", cloneName);
        Assert.Equal("recovered-annotation-14", String(Annotation(recovered, 2, 0)[N("NM")]));
        PdfDocument changed = PdfDocument.Open(new PdfIncrementalAnnotationEditor(recovered)
            .SetAnnotationContents(1, cloneName, "edited clone").Build());
        Assert.Equal("edited clone", String(Annotation(changed, 1, 0)[N("Contents")]));
        Assert.Equal("untouched", String(Annotation(changed, 2, 0)[N("Contents")]));
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("expansion")]
    [InlineData("foreign-owner")]
    [InlineData("tag-id")]
    [InlineData("tag-id-count-one")]
    [InlineData("tag-parent-swap")]
    [InlineData("tag-parent-cycle")]
    [InlineData("tag-merged-extension")]
    [InlineData("radio-direct-kid")]
    public void AmbiguousOrExcessiveRecoveryLeavesTheOriginalObjectModelIntact(string damage)
    {
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(Fixture(tagged: damage.StartsWith("tag-", StringComparison.Ordinal), damage: damage));
        int objectCount = document.CrossReferences.Count;
        PdfDictionary note = Dictionary(document, new PdfIndirectReference(5, 0));
        Assert.Throws<InvalidOperationException>(() => PdfPageInformation.Read(document));
        Assert.Same(note, Dictionary(document, new PdfIndirectReference(5, 0)));
        Assert.Equal(objectCount, document.CrossReferences.Count);
        Assert.Empty(document.RecoveryDiagnostics);
        if (damage.StartsWith("tag-id", StringComparison.Ordinal))
        {
            PdfDictionary widget = Dictionary(document, new PdfIndirectReference(11, 0));
            Assert.Equal("shared", String(widget[N("T")]));
            Assert.Equal("Widget", Assert.IsType<PdfName>(widget[N("Subtype")]).ValueAsLatin1());
        }
    }

    private static PdfDictionary Annotation(PdfDocument document, int page, int index) =>
        Dictionary(document, Assert.IsType<PdfArray>(PdfPageTree.Read(document).Pages[page].Dictionary[N("Annots")])[index]);
    private static PdfDictionary Dictionary(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(value is PdfIndirectReference reference ? document.Resolve(reference) : value);
    private static int ObjectNumber(PdfObject value) => Assert.IsType<PdfIndirectReference>(value).ObjectNumber;
    private static string String(PdfObject value)
    {
        ReadOnlySpan<byte> bytes = Assert.IsType<PdfString>(value).Bytes.Span;
        return bytes.StartsWith(new byte[] { 254, 255 })
            ? Encoding.BigEndianUnicode.GetString(bytes[2..]) : Encoding.ASCII.GetString(bytes);
    }
    private static PdfName N(string value) => new(Encoding.ASCII.GetBytes(value));
    private static PdfString Text(string value) => new(Encoding.ASCII.GetBytes(value), PdfStringForm.Literal);

    private static byte[] Fixture(bool tagged = false, string? damage = null, bool repeated = true)
    {
        string content = tagged ? "/P << /MCID 0 >> BDC 1 0 0 rg 5 5 25 30 re f EMC" : "1 0 0 rg 5 5 25 30 re f";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /Names << /Dests << /Names [(first) [3 0 R /Fit]] >> >> >>",
            "<< /Type /Pages /Kids [10 0 R 10 0 R] /Count 2 /MediaBox [0 0 100 100] /Resources << >> >>",
            "<< /Type /Page /Parent 10 0 R /Contents 4 0 R /Annots [5 0 R 6 0 R 7 0 R 8 0 R 9 0 R] >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Annot /Subtype /Text /Rect [50 50 60 60] /P 3 0 R /NM (note) /Contents (original) /Popup 7 0 R >>",
            "<< /Type /Annot /Subtype /Text /Rect [60 50 70 60] /P 3 0 R /NM (reply) /Contents (reply) /IRT 5 0 R /RT /R >>",
            "<< /Type /Annot /Subtype /Popup /Rect [50 50 90 90] /P 3 0 R /Parent 5 0 R >>",
            "<< /Type /Annot /Subtype /Link /Rect [5 70 20 80] /P 3 0 R /Dest [3 0 R /Fit] >>",
            "<< /Type /Annot /Subtype /Link /Rect [25 70 40 80] /P 3 0 R /A << /S /GoTo /D [3 0 R /XYZ 0 100 null] >> >>",
            "<< /Type /Pages /Parent 2 0 R /Kids [3 0 R] /Count 1 >>"
        ];
        if (tagged)
        {
            objects[0] = "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 12 0 R /MarkInfo << /Marked true >> /AcroForm << /Fields [11 0 R] /CO [11 0 R] /DR << /Font << /F1 18 0 R >> >> /DA (/F1 12 Tf 0 g) >> >>";
            objects[2] = "<< /Type /Page /Parent 10 0 R /Contents 4 0 R /StructParents 0 /Annots [5 0 R 6 0 R 7 0 R 8 0 R 9 0 R 11 0 R] >>";
            objects[4] = "<< /Type /Annot /Subtype /Text /Rect [50 50 60 60] /P 3 0 R /NM (note) /Contents (original) /Popup 7 0 R /StructParent 2 >>";
            const string appearance = "BT /F1 12 Tf 0 g 2 6 Td (Ada) Tj ET";
            objects = [.. objects,
                "<< /Type /Annot /Subtype /Widget /FT /Tx /T (shared) /V (Ada) /DV (Original) /Rect [10 10 90 30] /DA (/F1 12 Tf 0 g) /AP << /N 19 0 R >> /P 3 0 R /StructParent 1 >>",
                "<< /Type /StructTreeRoot /K [13 0 R] /ParentTree 17 0 R /ParentTreeNextKey 3 >>",
                "<< /Type /StructElem /S /Document /P 12 0 R /K [14 0 R 15 0 R 16 0 R] >>",
                "<< /Type /StructElem /S /P /P 13 0 R /Pg 3 0 R /K 0 >>",
                "<< /Type /StructElem /S /Note /P 13 0 R /Pg 3 0 R /K << /Type /OBJR /Pg 3 0 R /Obj 5 0 R >> >>",
                "<< /Type /StructElem /S /Form /P 13 0 R /Pg 3 0 R /K << /Type /OBJR /Pg 3 0 R /Obj 11 0 R >> >>",
                "<< /Nums [0 [14 0 R] 1 16 0 R 2 15 0 R] >>",
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
                $"<< /Length {appearance.Length} /Type /XObject /Subtype /Form /BBox [0 0 80 20] /Resources << /Font << /F1 18 0 R >> >> >>\nstream\n{appearance}\nendstream"];
        }
        if (damage == "external-popup") objects[2] = "<< /Type /Page /Parent 10 0 R /Contents 4 0 R /Annots [5 0 R 6 0 R 8 0 R 9 0 R] >>";
        if (damage == "page-aa") objects[2] = objects[2][..^2] + " /AA << /O << /S /GoTo /D [3 0 R /Fit] >> >> >>";
        if (damage == "widget-aa") objects[10] = objects[10][..^2] + " /AA << /U << /S /GoTo /D [3 0 R /Fit] >> >> >>";
        if (damage == "action-next") objects[8] = "<< /Type /Annot /Subtype /Link /Rect [25 70 40 80] /P 3 0 R /A << /S /GoTo /D [3 0 R /Fit] /Next << /S /GoTo /D [3 0 R /Fit] >> >> >>";
        if (damage == "indirect-widget")
        {
            objects[10] = objects[10].Replace("/Subtype /Widget", "/Subtype 20 0 R", StringComparison.Ordinal);
            objects = [.. objects, "/Widget"];
        }
        if (damage == "tag-parent-swap") objects[16] = "<< /Nums [0 [14 0 R] 1 15 0 R 2 16 0 R] >>";
        if (damage == "tag-parent-cycle")
        {
            objects[16] = "<< /Nums [0 20 0 R 1 16 0 R 2 15 0 R] >>";
            objects = [.. objects, "[20 0 R]"];
        }
        if (damage == "tag-merged-extension") objects[10] = objects[10][..^2] + " /ApplicationPrivateFieldState (logical data) >>";
        if (damage == "foreign-owner")
            objects[4] = "<< /Type /Annot /Subtype /Text /Rect [50 50 60 60] /P 10 0 R /NM (note) /Contents (foreign owner) /Popup 7 0 R >>";
        if (damage == "cycle") objects[9] = "<< /Type /Pages /Parent 2 0 R /Kids [10 0 R] /Count 1 >>";
        if (damage?.StartsWith("tag-id", StringComparison.Ordinal) == true)
            objects[13] = "<< /Type /StructElem /S /P /P 13 0 R /Pg 3 0 R /ID (identity) /K 0 >>";
        if (damage == "tag-id-count-one")
            objects[1] = "<< /Type /Pages /Kids [10 0 R 10 0 R] /Count 1 /MediaBox [0 0 100 100] /Resources << >> >>";
        if (damage is "collision-utf16" or "collision-latin")
        {
            objects[1] = "<< /Type /Pages /Kids [10 0 R 10 0 R 11 0 R] /Count 3 /MediaBox [0 0 100 100] /Resources << >> >>";
            string name = damage == "collision-latin" ? "(recovered-annotation-14)"
                : "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes("recovered-annotation-14")) + ">";
            objects = [.. objects,
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Contents 4 0 R /Annots [12 0 R] >>",
                $"<< /Type /Annot /Subtype /Text /Rect [50 50 60 60] /P 11 0 R /NM {name} /Contents (untouched) >>"];
        }
        if (damage == "expansion")
        {
            objects[1] = "<< /Type /Pages /Kids [11 0 R 11 0 R] /Count 16384 /MediaBox [0 0 100 100] /Resources << >> >>";
            for (int number = 11; number <= 23; number++)
            {
                int child = number == 23 ? 3 : number + 1;
                int parent = number == 11 ? 2 : number - 1;
                objects = [.. objects, $"<< /Type /Pages /Parent {parent} 0 R /Kids [{child} 0 R {child} 0 R] /Count 2 >>"];
            }
        }
        if (damage is "radio-order" or "radio-direct-kid")
            objects = [
                "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] >> >>",
                "<< /Type /Pages /Kids [3 0 R 3 0 R 4 0 R] /Count 3 /MediaBox [0 0 100 100] /Resources << >> >>",
                "<< /Type /Page /Parent 2 0 R /Annots [6 0 R] >>",
                "<< /Type /Page /Parent 2 0 R /Annots [7 0 R] >>",
                "<< /FT /Btn /Ff 32768 /T (choice) /V /On1 /Kids [6 0 R 7 0 R] /Opt [(first) (second)] >>",
                "<< /Type /Annot /Subtype /Widget /Parent 5 0 R /P 3 0 R /Rect [10 10 30 30] /AP << /N << /Off 8 0 R /On1 8 0 R >> >> /AS /On1 >>",
                "<< /Type /Annot /Subtype /Widget /Parent 5 0 R /P 4 0 R /Rect [10 10 30 30] /AP << /N << /Off 8 0 R /On2 8 0 R >> >> /AS /Off >>",
                "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Resources << >> /Length 0 >>\nstream\n\nendstream"];
        if (damage == "radio-direct-kid") objects[4] = "<< /FT /Btn /Ff 32768 /T (choice) /V /On1 /Kids [6 0 R << /Subtype /Widget /Parent 5 0 R >>] /Opt [(first) (second)] >>";
        if (damage == "calculations")
        {
            objects[0] = "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 12 0 R /MarkInfo << /Marked true >> /AcroForm << /Fields [11 0 R 20 0 R] /CO [20 0 R] /DR << /Font << /F1 18 0 R >> >> /DA (/F1 12 Tf 0 g) >> >>";
            objects[2] = "<< /Type /Page /Parent 10 0 R /Contents 4 0 R /StructParents 0 /Annots [5 0 R 6 0 R 7 0 R 8 0 R 9 0 R 11 0 R 20 0 R] >>";
            objects[10] = "<< /Type /Annot /Subtype /Widget /FT /Tx /T (quantity) /V (3) /DV (2) /Rect [10 10 90 30] /DA (/F1 12 Tf 0 g) /AP << /N 19 0 R >> /P 3 0 R /StructParent 1 >>";
            objects[11] = "<< /Type /StructTreeRoot /K [13 0 R] /ParentTree 17 0 R /ParentTreeNextKey 4 >>";
            objects[12] = "<< /Type /StructElem /S /Document /P 12 0 R /K [14 0 R 15 0 R 16 0 R 21 0 R] >>";
            objects[16] = "<< /Nums [0 [14 0 R] 1 16 0 R 2 15 0 R 3 21 0 R] >>";
            const string inputAppearance = "BT /F1 12 Tf 0 g 2 6 Td (3) Tj ET";
            const string totalAppearance = "BT /F1 12 Tf 0 g 2 6 Td (6) Tj ET";
            string Appearance(string text) => $"<< /Length {text.Length} /Type /XObject /Subtype /Form /BBox [0 0 80 20] /Resources << /Font << /F1 18 0 R >> >> >>\nstream\n{text}\nendstream";
            objects[18] = Appearance(inputAppearance);
            objects = [.. objects,
                "<< /Type /Annot /Subtype /Widget /FT /Tx /T (total) /V (6) /DV (4) /Rect [10 35 90 55] /DA (/F1 12 Tf 0 g) /AP << /N 22 0 R >> /P 3 0 R /StructParent 3 /AA << /C << /S /JavaScript /JS (event.value = this.getField\\(\"quantity\"\\).value * 2;) >> >> >>",
                "<< /Type /StructElem /S /Form /P 13 0 R /Pg 3 0 R /K << /Type /OBJR /Pg 3 0 R /Obj 20 0 R >> >>",
                Appearance(totalAppearance)];
        }
        if (!repeated) objects[1] = "<< /Type /Pages /Kids [10 0 R] /Count 1 /MediaBox [0 0 100 100] /Resources << >> >>";
        var result = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(result.ToString()));
            result.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(result.ToString());
        result.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) result.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        result.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(result.ToString());
    }
}
