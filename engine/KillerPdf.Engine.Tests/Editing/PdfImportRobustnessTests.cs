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

    // /Process names the process color space's own components, which a DeviceN space may use only
    // part of, so they answer to that space and not to the colorants it lists.
    [Theory]
    [InlineData(null, "/Cyan /Magenta /Yellow /Black")]
    [InlineData("duplicate", "/Cyan /Magenta /Yellow /Cyan")]
    [InlineData("duplicate", "/Cyan /Magenta /None /Black")]
    public void ADeviceNProcessListsTheComponentsOfItsOwnColorSpace(string? refused, string components)
    {
        byte[] source = Update(OnePage(), (document, update, root, catalog) =>
        {
            PdfIndirectReference tint = update.AddObject(new PdfStream(Dictionary(
                ("FunctionType", new PdfInteger(4)), ("Domain", Numbers(0, 1, 0, 1, 0, 1)),
                ("Range", Numbers(0, 1, 0, 1, 0, 1, 0, 1))), "{ 0 }"u8.ToArray()));
            var space = new PdfArray([Name("DeviceN"), new PdfArray([Name("Cyan"), Name("Magenta"), Name("Yellow")]),
                Name("DeviceCMYK"), tint, Dictionary(("Subtype", Name("NChannel")), ("Process", Dictionary(
                    ("ColorSpace", Name("DeviceCMYK")),
                    ("Components", new PdfArray([.. components.Split(' ').Select(item => (PdfObject)Name(item[1..]))])))))]);
            ReplacePage(document, update, page => Replace(page, "Resources", Dictionary(("ColorSpace", Dictionary(("CS0", space))))));
            return catalog;
        });

        var import = () => new PdfIncrementalPageEditor(Empty()).AddImportedPage(PdfDocument.Open(source), 0).Build();
        if (refused is null) Assert.NotEmpty(import());
        else Assert.Contains(refused, Assert.Throws<InvalidOperationException>(() => import()).Message, StringComparison.Ordinal);
    }

    // The window's Extract and Merge open files strictly, and Pillow's xref stopped them on a file
    // that displayed perfectly (#463).
    [Fact]
    public void APillowPdfCanBeExtractedFromAndMergedInto()
    {
        byte[] pillow = PillowHead(new PdfDocumentBuilder().AddBlankPage().AddBlankPage().AddBlankPage().Build());

        PdfDocument document = PdfDocument.Open(pillow);
        Assert.Equal(3, PdfPageInformation.Read(document).Count);
        byte[] extracted = new PdfIncrementalPageEditor(Empty()).AddImportedPage(document, 1).Build();
        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(extracted)));
        byte[] merged = new PdfIncrementalPageEditor(document).AddImportedDocument(PdfDocument.Open(OnePage())).Build();
        Assert.Equal(4, PdfPageInformation.Read(PdfDocument.Open(merged)).Count);

        static byte[] PillowHead(byte[] source)
        {
            string text = Encoding.Latin1.GetString(source);
            Assert.Contains("0000000000 65535 f", text, StringComparison.Ordinal);
            return Encoding.Latin1.GetBytes(text.Replace("0000000000 65535 f", "0000000000 65536 f", StringComparison.Ordinal));
        }
    }

    // A shared dictionary lists more than this space uses, and hints take a /Default key.
    [Fact]
    public void DeviceNDescriptionsMayNameMoreThanTheColorUses()
    {
        byte[] source = Update(OnePage(), (document, update, root, catalog) =>
        {
            PdfIndirectReference tint = update.AddObject(new PdfStream(Dictionary(
                ("FunctionType", new PdfInteger(4)), ("Domain", Numbers(0, 1)), ("Range", Numbers(0, 1, 0, 1, 0, 1, 0, 1))), "{ 0 0 0 }"u8.ToArray()));
            PdfIndirectReference tintOther = update.AddObject(new PdfStream(Dictionary(
                ("FunctionType", new PdfInteger(4)), ("Domain", Numbers(0, 1)), ("Range", Numbers(0, 1, 0, 1, 0, 1, 0, 1))), "{ 0 0 0 }"u8.ToArray()));
            var other = new PdfArray([Name("Separation"), Name("Other"), Name("DeviceCMYK"), tintOther]);
            var space = new PdfArray([Name("DeviceN"), new PdfArray([Name("Spot")]), Name("DeviceCMYK"), tint, Dictionary(
                ("Colorants", Dictionary(("Other", other))),
                ("MixingHints", Dictionary(("Solidities", Dictionary(("Default", new PdfInteger(0)))),
                    ("PrintingOrder", new PdfArray([Name("Cyan"), Name("Spot")])))))]);
            ReplacePage(document, update, page => Replace(page, "Resources", Dictionary(("ColorSpace", Dictionary(("CS0", space))))));
            return catalog;
        });

        Assert.NotEmpty(new PdfIncrementalPageEditor(Empty()).AddImportedPage(PdfDocument.Open(source), 0).Build());
    }

    // A soft mask may have its own resolution. Only /Matte ties it to the image's pixels.
    [Theory]
    [InlineData(178, 94, false, false)]
    [InlineData(154, 81, true, false)]
    [InlineData(178, 94, true, true)]
    public void ASoftMaskOnlyMatchesItsImageWhenItHasAMatte(int width, int height, bool matte, bool refused)
    {
        byte[] source = Update(OnePage(), (document, update, root, catalog) =>
        {
            var mask = new List<(string, PdfObject)> { ("Type", Name("XObject")), ("Subtype", Name("Image")), ("Width", new PdfInteger(width)),
                ("Height", new PdfInteger(height)), ("ColorSpace", Name("DeviceGray")), ("BitsPerComponent", new PdfInteger(8)) };
            if (matte) mask.Add(("Matte", Numbers(0, 0, 0)));
            PdfIndirectReference softMask = update.AddObject(new PdfStream(Dictionary([.. mask]), new byte[width * height]));
            PdfIndirectReference image = update.AddObject(new PdfStream(Dictionary(("Type", Name("XObject")), ("Subtype", Name("Image")),
                ("Width", new PdfInteger(154)), ("Height", new PdfInteger(81)), ("ColorSpace", Name("DeviceRGB")),
                ("BitsPerComponent", new PdfInteger(8)), ("SMask", softMask)), new byte[154 * 81 * 3]));
            ReplacePage(document, update, page => Replace(page, "Resources", Dictionary(("XObject", Dictionary(("R614", image))))));
            return catalog;
        });

        var import = () => new PdfIncrementalPageEditor(Empty()).AddImportedPage(PdfDocument.Open(source), 0).Build();
        if (!refused) Assert.NotEmpty(import());
        else Assert.Contains("/SMask with /Matte", Assert.Throws<InvalidOperationException>(() => import()).Message, StringComparison.Ordinal);
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

    // A /StructParents key with no /StructTreeRoot is a leftover that points at nothing, as
    // jcpe13896_am.pdf has. It must not count as tagging, or the pages are refused or lost from a combine.
    [Theory]
    [InlineData("document")]
    [InlineData("document-into-pages")]
    [InlineData("page")]
    [InlineData("pages")]
    public void AStructParentsKeyWithNoStructureTreeIsDroppedWhenPagesAreImported(string mode)
    {
        PdfDocument source = PdfDocument.Open(Update(
            new PdfDocumentBuilder().AddBlankPage().AddBlankPage().Build(),
            (document, update, root, catalog) =>
            {
                PdfPageTreeEntry[] pages = [.. PdfPageTree.Read(document).Pages];
                for (int index = 0; index < pages.Length; index++)
                    update.ReplaceObject(pages[index].Reference.ObjectNumber,
                        Replace(pages[index].Dictionary, "StructParents", new PdfInteger(index)));
                return catalog;
            }));
        Assert.All(PdfPageTree.Read(source).Pages, page => Assert.True(page.Dictionary.ContainsKey(Name("StructParents"))));
        PdfDocument destination = mode == "document-into-pages" ? PdfDocument.Open(OnePage()) : Empty();

        var editor = new PdfIncrementalPageEditor(destination);
        _ = mode switch
        {
            "page" => editor.AddImportedPage(source, 1),
            "pages" => editor.AddImportedPages(source, [1, 0]),
            _ => editor.AddImportedDocument(source)
        };
        PdfDocument output = PdfDocument.Open(editor.Build());

        PdfPageTreeEntry[] imported = [.. PdfPageTree.Read(output).Pages];
        Assert.Equal(mode switch { "page" => 1, "pages" => 2, "document" => 2, _ => 3 }, imported.Length);
        Assert.All(imported, page => Assert.False(page.Dictionary.ContainsKey(Name("StructParents"))));
        Assert.False(PdfPageTree.Read(output).Catalog.ContainsKey(Name("StructTreeRoot")));
    }

    [Fact]
    public void ATruePageTaggedByAStructureTreeKeepsItsKeyWhenImportedPageByPage()
    {
        byte[] tagged = new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Tagged", Language = "en-US" })
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginMarkedContent(PdfStructureType.Figure, 0).Rectangle(10, 10, 20, 20).Fill().EndMarkedContent())
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Figure, 0, 0, 1, alternateDescription: "A square")
            .Build();

        PdfDocument output = PdfDocument.Open(new PdfIncrementalPageEditor(Empty())
            .AddImportedPage(PdfDocument.Open(tagged), 0).Build());

        PdfPageTree tree = PdfPageTree.Read(output);
        Assert.True(Assert.Single(tree.Pages).Dictionary.ContainsKey(Name("StructParents")));
        Assert.True(tree.Catalog.ContainsKey(Name("StructTreeRoot")));
    }

    // 0.00-96880406 reads as a number in page content, so a layered source that holds it still imports.
    [Fact]
    public void ImportingAPageWithAStrayMinusInANumberFromALayeredSourceSucceeds()
    {
        string stray = "BT /F1 10 Tf 0.00-96880406 0 0 rg 30 250 Td (Stray) Tj ET";
        string plain = "BT /F1 10 Tf 0 0 0 rg 30 250 Td (Plain) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [6 0 R] /D << /Order [6 0 R] /ON [6 0 R] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /Font << /F1 5 0 R >> >> /Contents 8 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /OCG /Name (Layer) >>",
            $"<< /Length {stray.Length} >>\nstream\n{stray}\nendstream",
            $"<< /Length {plain.Length} >>\nstream\n{plain}\nendstream"
        ];
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(text.Length);
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:0000000000} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        PdfDocument source = PdfDocument.Open(Encoding.ASCII.GetBytes(text.ToString()));
        Assert.Equal("Stray", new PdfPageContentReader(source).Read(0).TextRuns.Single().Text);

        byte[] output = new PdfIncrementalPageEditor(Empty()).AddImportedPage(source, 0).Build();

        Assert.Equal("Stray", new PdfPageContentReader(PdfDocument.Open(output)).Read(0).TextRuns.Single().Text);
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
