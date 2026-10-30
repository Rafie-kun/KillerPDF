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

    // An /OCProperties with no /OCGs declares no layers, as PDFBOX-4615-7.pdf does, so it has nothing
    // to carry over or merge with and must not stop the pages being copied.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void MergingOptionalContentWithoutAGroupListKeepsThePages(bool sourceLacksGroups, bool targetLacksGroups)
    {
        byte[] Part(bool lacksGroups) => lacksGroups
            ? Update(OnePage(), (document, update, root, catalog) => Replace(catalog,
                "OCProperties", Dictionary(("D", Dictionary(("OFF", new PdfArray([])))))))
            : OnePage();

        byte[] output = new PdfIncrementalPageEditor(PdfDocument.Open(Part(targetLacksGroups)))
            .AddImportedDocument(PdfDocument.Open(Part(sourceLacksGroups))).Build();

        Assert.Equal(2, PdfPageInformation.Read(PdfDocument.Open(output)).Count);
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

    // A long /Next chain is one deep path through the object graph, not deep nesting, so importing it
    // must stay iterative: a 1 MB stack cannot hold a frame per link.
    [Fact]
    public void ImportingAVeryLongLinkChainOnASmallStackKeepsEveryLink()
    {
        const int links = 100_000;
        byte[] source = LinkChainDocument(links, closeTheLoop: false);

        byte[] output = OnSmallStack(() => new PdfIncrementalPageEditor(Empty())
            .AddImportedDocument(PdfDocument.Open(source)).Build());

        PdfDocument imported = PdfDocument.Open(output);
        PdfDictionary page = Resolve(imported, Assert.IsType<PdfArray>(Resolve(imported,
            Resolve(imported, imported.Trailer[Name("Root")])[Name("Pages")])[Name("Kids")])[0]);
        int count = 0;
        PdfObject? next = page[Name("Link")];
        while (next is PdfIndirectReference reference)
        {
            count++;
            PdfDictionary link = Resolve(imported, reference);
            next = link.TryGetValue(Name("Next"), out PdfObject? value) ? value : null;
        }
        Assert.Equal(links, count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(50)]
    public void ImportingACyclicLinkChainEndsSafelyAndKeepsTheLoop(int length)
    {
        byte[] source = LinkChainDocument(length, closeTheLoop: true);

        byte[] output = OnSmallStack(() => new PdfIncrementalPageEditor(Empty())
            .AddImportedDocument(PdfDocument.Open(source)).Build());

        PdfDocument imported = PdfDocument.Open(output);
        PdfDictionary page = Resolve(imported, Assert.IsType<PdfArray>(Resolve(imported,
            Resolve(imported, imported.Trailer[Name("Root")])[Name("Pages")])[Name("Kids")])[0]);
        var seen = new HashSet<int>();
        PdfObject? next = page[Name("Link")];
        while (next is PdfIndirectReference reference && seen.Add(reference.ObjectNumber))
            next = Resolve(imported, reference)[Name("Next")];
        Assert.Equal(length, seen.Count);
        Assert.True(next is PdfIndirectReference);
    }

    [Fact]
    public void MergingAVeryLongFlatBookmarkListOnASmallStackKeepsEveryBookmark()
    {
        const int bookmarks = 3000;
        PdfDocumentBuilder builder = new PdfDocumentBuilder().AddBlankPage();
        for (int index = 0; index < bookmarks; index++)
            builder.AddBookmark("Item " + index, 0);
        byte[] source = builder.Build();

        byte[] output = OnSmallStack(() => new PdfIncrementalPageEditor(Empty())
            .AddImportedDocument(PdfDocument.Open(source)).Build());

        Assert.Equal(bookmarks, PdfBookmarkReader.Read(PdfDocument.Open(output)).Count);
    }

    // The catalog's first page carries /Link, the head of a chain of dictionaries joined by /Next.
    private static byte[] LinkChainDocument(int links, bool closeTheLoop)
    {
        var source = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Add(string body)
        {
            offsets.Add(source.Length);
            source.Append(offsets.Count).Append(" 0 obj ").Append(body).Append(" endobj\n");
        }
        Add("<< /Type /Catalog /Pages 2 0 R >>");
        Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Link 4 0 R >>");
        for (int index = 0; index < links; index++)
            Add(index < links - 1 ? $"<< /Next {5 + index} 0 R >>"
                : closeTheLoop ? "<< /Next 4 0 R >>" : "<< /Last true >>");
        int xref = source.Length;
        source.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) source.Append(offset.ToString("D10")).Append(" 00000 n \n");
        source.Append("trailer << /Size ").Append(offsets.Count + 1)
            .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(source.ToString());
    }

    private static T OnSmallStack<T>(Func<T> work)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception error) { failure = error; }
        }, 1024 * 1024);
        thread.Start();
        thread.Join();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
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
