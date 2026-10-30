using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfImportRobustnessTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ImportingADocumentWithAnIndirectNamesDictionaryKeepsItsAttachments(bool indirect)
    {
        byte[] source = Update(new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("kept.txt", "payload"u8.ToArray(), "text/plain").Build(),
            (document, update, root, catalog) =>
            {
                PdfDictionary names = Resolve(document, catalog[Name("Names")]);
                return Replace(catalog, "Names", indirect ? update.AddObject(names) : names);
            });

        byte[] output = new PdfIncrementalPageEditor(Empty())
            .AddImportedDocument(PdfDocument.Open(source)).Build();

        PdfAttachmentInfo attachment = Assert.Single(
            PdfAttachmentReader.Read(PdfDocument.Open(output)));
        Assert.Equal("kept.txt", attachment.FileName);
    }

    [Fact]
    public void ImportingOptionalContentWithoutAGroupListGivesAReadableRefusal()
    {
        byte[] source = Update(OnePage(), (document, update, root, catalog) => Replace(catalog,
            "OCProperties", Dictionary(("D", Dictionary(("OFF", new PdfArray([])))))));
        var editor = new PdfIncrementalPageEditor(Empty());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            editor.AddImportedDocument(PdfDocument.Open(source)).Build());
        Assert.Contains("/OCGs", error.Message);
    }

    [Fact]
    public void ImportingACompleteDocumentWithWidgetsButNoAcroFormSucceeds()
    {
        byte[] source = Update(OnePage(), (document, update, root, catalog) =>
        {
            ReplacePage(document, update, page => Replace(page, "Annots", new PdfArray([
                Dictionary(("Type", Name("Annot")), ("Subtype", Name("Widget")),
                    ("FT", Name("Tx")), ("T", Text("field")),
                    ("Rect", Numbers(10, 10, 100, 30)), ("F", new PdfInteger(4)))])));
            return catalog;
        });

        byte[] output = new PdfIncrementalPageEditor(Empty())
            .AddImportedDocument(PdfDocument.Open(source)).Build();

        Assert.NotEmpty(PdfDocument.Open(output).Trailer);
    }

    [Theory]
    [InlineData("FreeTextTypewriter")]
    [InlineData("FreeTextTypeWriter")]
    public void ImportingFreeTextAnnotationsToleratesTheTypewriterIntentSpelling(string intent)
    {
        byte[] source = Update(OnePage(), (document, update, root, catalog) =>
        {
            ReplacePage(document, update, page => Replace(page, "Annots", new PdfArray([
                Dictionary(("Type", Name("Annot")), ("Subtype", Name("FreeText")),
                    ("Rect", Numbers(10, 10, 100, 30)), ("Contents", Text("note")),
                    ("DA", Text("/Helv 12 Tf 0 g")), ("IT", Name(intent)))])));
            return catalog;
        });

        byte[] output = new PdfIncrementalPageEditor(Empty())
            .AddImportedDocument(PdfDocument.Open(source)).Build();

        Assert.NotEmpty(PdfDocument.Open(output).Trailer);
    }

    [Fact]
    public void AddingAnAttachmentIgnoresMetadataThatIsNotXml()
    {
        byte[] source = Update(OnePage(), (document, update, root, catalog) => Replace(catalog,
            "Metadata", update.AddObject(new PdfStream(
                Dictionary(("Type", Name("Metadata")), ("Subtype", Name("XML"))),
                "this is not xml <"u8.ToArray()))));

        byte[] output = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .AddAttachment("note.txt", "x"u8.ToArray(), "text/plain").Build();

        Assert.Single(PdfAttachmentReader.Read(PdfDocument.Open(output)));
    }

    [Fact]
    public void AddingAnAttachmentToAnUnauthenticatedEncryptedFileAsksForThePassword()
    {
        byte[] encrypted = new PdfDocumentBuilder().AddBlankPage()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user", OwnerPassword = "owner"
            }).Build();
        byte[] withMetadata = Update(PdfDocument.Open(encrypted, "owner"),
            (document, update, root, catalog) => Replace(catalog,
                "Metadata", update.AddObject(new PdfStream(
                    Dictionary(("Type", Name("Metadata")), ("Subtype", Name("XML"))),
                    "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray()))));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalPageEditor(PdfDocument.Open(withMetadata))
                .AddAttachment("note.txt", "x"u8.ToArray(), "text/plain").Build());

        Assert.DoesNotContain("XMP", error.Message);
        Assert.Contains("password", error.Message);
    }

    // A file with no cross-reference data at all, so every object is found by scanning.
    private const string RecoveredSource =
        "%PDF-1.7\n\ntrailer\n<<\n/Root 1 0 R\n>>\n\n"
        + "1 0 obj\n<<\n/Type /Catalog\n/Pages 2 0 R\n/OpenAction 2 0 R\n>>\nendobj\n\n"
        + "2 0 obj\n<<\n/S /JavaScript\n/JS(func=function(){app.alert(1)};func();)\n>>\nendobj\n\n%%EOF\n";

    [Fact]
    public void EditingARecoveredDocumentThatCannotBeUpdatedInPlaceGivesAReadableRefusal()
    {
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(
            Encoding.ASCII.GetBytes(RecoveredSource));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalPageEditor(recovered).RotateClockwise(0).Build());
        Assert.Contains("repaired", error.Message);
    }

    [Fact]
    public void AddingAFormFieldToARecoveredDocumentGivesAReadableRefusal()
    {
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(
            Encoding.ASCII.GetBytes(RecoveredSource));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalPageEditor(recovered)
                .AddTextField(0, "field", 10, 10, 100, 30).Build());
        Assert.Contains("repaired", error.Message);
    }

    private static PdfDocument Empty() => PdfDocument.Open(new PdfDocumentBuilder().Build());

    private static byte[] OnePage() => new PdfDocumentBuilder().AddBlankPage().Build();

    private static byte[] Update(byte[] source,
        Func<PdfDocument, PdfIncrementalUpdateBuilder, PdfIndirectReference, PdfDictionary, PdfDictionary> edit) =>
        Update(PdfDocument.Open(source), edit);

    private static byte[] Update(PdfDocument document,
        Func<PdfDocument, PdfIncrementalUpdateBuilder, PdfIndirectReference, PdfDictionary, PdfDictionary> edit)
    {
        var root = Assert.IsType<PdfIndirectReference>(document.Trailer[Name("Root")]);
        PdfDictionary catalog = Resolve(document, root);
        var update = new PdfIncrementalUpdateBuilder(document);
        update.ReplaceObject(root.ObjectNumber, edit(document, update, root, catalog));
        return update.Build();
    }

    private static void ReplacePage(PdfDocument document, PdfIncrementalUpdateBuilder update,
        Func<PdfDictionary, PdfDictionary> edit)
    {
        PdfDictionary catalog = Resolve(document, document.Trailer[Name("Root")]);
        PdfDictionary pages = Resolve(document, catalog[Name("Pages")]);
        var page = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        update.ReplaceObject(page.ObjectNumber, edit(Resolve(document, page)));
    }

    private static PdfDictionary Replace(PdfDictionary source, string key, PdfObject value) =>
        new(source.Where(entry => !entry.Key.Equals(Name(key))).Append(
            new KeyValuePair<PdfName, PdfObject>(Name(key), value)));

    private static PdfDictionary Dictionary(params (string Key, PdfObject Value)[] entries) =>
        new(entries.Select(entry => new KeyValuePair<PdfName, PdfObject>(Name(entry.Key), entry.Value)));

    private static PdfDictionary Resolve(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(value is PdfIndirectReference reference
            ? document.Resolve(reference) : value);

    private static PdfArray Numbers(params int[] values) =>
        new(values.Select(value => (PdfObject)new PdfInteger(value)));

    private static PdfString Text(string value) =>
        new(Encoding.ASCII.GetBytes(value), PdfStringForm.Literal);

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
