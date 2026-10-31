using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfAttachmentNameKeyTests
{
    private const string TreeKey = "tree-key.txt";
    private const string ListedName = "Listed.txt";

    [Fact]
    public void ReaderReportsNameTreeKeyBesideListedFileName()
    {
        PdfAttachmentInfo attachment = Assert.Single(
            PdfAttachmentReader.Read(PdfDocument.Open(DifferentlyNamedSource())));

        Assert.Equal(ListedName, attachment.FileName);
        Assert.Equal(TreeKey, attachment.NameTreeKey);
    }

    [Fact]
    public void RemoveAttachmentAcceptsTheListedFileName()
    {
        byte[] output = new PdfIncrementalPageEditor(PdfDocument.Open(DifferentlyNamedSource()))
            .RemoveAttachment(ListedName)
            .Build();

        Assert.Empty(PdfAttachmentReader.Read(PdfDocument.Open(output)));
    }

    [Fact]
    public void RemoveAttachmentStillAcceptsTheNameTreeKey()
    {
        byte[] output = new PdfIncrementalPageEditor(PdfDocument.Open(DifferentlyNamedSource()))
            .RemoveAttachment(TreeKey)
            .Build();

        Assert.Empty(PdfAttachmentReader.Read(PdfDocument.Open(output)));
    }

    [Fact]
    public void RenameAttachmentAcceptsTheListedFileName()
    {
        byte[] output = new PdfIncrementalPageEditor(PdfDocument.Open(DifferentlyNamedSource()))
            .RenameAttachment(ListedName, "Renamed.txt")
            .Build();

        PdfAttachmentInfo attachment = Assert.Single(
            PdfAttachmentReader.Read(PdfDocument.Open(output)));
        Assert.Equal("Renamed.txt", attachment.FileName);
        Assert.Equal("payload"u8.ToArray(), attachment.Data.ToArray());
    }

    [Fact]
    public void ReplaceAttachmentAcceptsTheListedFileName()
    {
        byte[] output = new PdfIncrementalPageEditor(PdfDocument.Open(DifferentlyNamedSource()))
            .ReplaceAttachment(ListedName, "changed"u8.ToArray())
            .Build();

        PdfAttachmentInfo attachment = Assert.Single(
            PdfAttachmentReader.Read(PdfDocument.Open(output)));
        Assert.Equal(ListedName, attachment.FileName);
        Assert.Equal("changed"u8.ToArray(), attachment.Data.ToArray());
    }

    [Fact]
    public void RemoveAttachmentStillRejectsAnUnknownName()
    {
        var editor = new PdfIncrementalPageEditor(PdfDocument.Open(DifferentlyNamedSource()));

        Assert.Throws<ArgumentException>(() => editor.RemoveAttachment("missing.txt"));
    }

    // A check applies to what an edit changes. A file with a type or a name the checks would refuse can
    // still be described, renamed or retyped one field at a time, in any order.
    [Theory]
    [InlineData("text")]
    [InlineData("text#2Fplain;#20charset=utf-8")]
    public void AnAttachmentWithAnOddMimeTypeCanStillBeRenamedAndDescribed(string subtype)
    {
        byte[] source = Portfolio("", "(a.txt) 10 0 R",
            (10, Spec("(a.txt)", 20)), (20, Embedded(subtype, "abc")));
        string mime = Assert.Single(PdfAttachmentReader.Read(PdfDocument.Open(source))).MimeType;

        byte[] renamed = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .RenameAttachmentByKey("a.txt", "b.txt").Build();
        byte[] described = new PdfIncrementalPageEditor(PdfDocument.Open(renamed))
            .SetAttachmentDescriptionByKey("b.txt", "reviewed").Build();

        PdfAttachmentInfo attachment = Assert.Single(PdfAttachmentReader.Read(PdfDocument.Open(described)));
        Assert.Equal("b.txt", attachment.FileName);
        Assert.Equal("reviewed", attachment.Description);
        Assert.Equal(mime, attachment.MimeType);
        Assert.Equal("abc"u8.ToArray(), attachment.Data.ToArray());

        byte[] retyped = new PdfIncrementalPageEditor(PdfDocument.Open(described))
            .SetAttachmentClassificationByKey("b.txt", "text/plain", PdfAssociatedFileRelationship.Data).Build();
        Assert.Equal("text/plain", Assert.Single(PdfAttachmentReader.Read(PdfDocument.Open(retyped))).MimeType);
    }

    [Fact]
    public void AnAttachmentWithAPathInItsListedNameCanStillBeDescribed()
    {
        byte[] source = Portfolio("", "(a.txt) 10 0 R",
            (10, Spec(@"(C:\\dir\\a.txt)", 20)), (20, Embedded("text#2Fplain", "abc")));

        byte[] output = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetAttachmentDescriptionByKey("a.txt", "reviewed").Build();

        PdfAttachmentInfo attachment = Assert.Single(PdfAttachmentReader.Read(PdfDocument.Open(output)));
        Assert.Equal(@"C:\dir\a.txt", attachment.FileName);
        Assert.Equal("reviewed", attachment.Description);
    }

    // Each rewrite copies the payload into the output again, so a rename, a description and a type go
    // through one rewrite, and a field the call leaves out keeps the file's own value.
    [Fact]
    public void RenameDescriptionAndTypeOfOneAttachmentTakeASingleRewrite()
    {
        byte[] source = Portfolio("", "(a.txt) 10 0 R",
            (10, Spec("(a.txt)", 20)), (20, Embedded("text#2Fplain", "abc")));

        byte[] edited = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .EditAttachmentByKey("a.txt", "b.txt", "application/pdf", PdfAssociatedFileRelationship.Source,
                setDescription: true, description: "reviewed").Build();

        Assert.Equal(PdfDocument.Open(source).CrossReferences.Sections.Count + 1,
            PdfDocument.Open(edited).CrossReferences.Sections.Count);
        PdfAttachmentInfo attachment = Assert.Single(PdfAttachmentReader.Read(PdfDocument.Open(edited)));
        Assert.Equal("b.txt", attachment.NameTreeKey);
        Assert.Equal("b.txt", attachment.FileName);
        Assert.Equal("reviewed", attachment.Description);
        Assert.Equal("application/pdf", attachment.MimeType);
        Assert.Equal(PdfAssociatedFileRelationship.Source, attachment.Relationship);
        Assert.Equal("abc"u8.ToArray(), attachment.Data.ToArray());

        byte[] untouched = new PdfIncrementalPageEditor(PdfDocument.Open(edited))
            .EditAttachmentByKey("b.txt", setDescription: true, description: null).Build();
        PdfAttachmentInfo cleared = Assert.Single(PdfAttachmentReader.Read(PdfDocument.Open(untouched)));
        Assert.Null(cleared.Description);
        Assert.Equal("b.txt", cleared.FileName);
        Assert.Equal("application/pdf", cleared.MimeType);
        Assert.Equal(PdfAssociatedFileRelationship.Source, cleared.Relationship);
    }

    [Fact]
    public void ANewValueThatIsNotValidIsStillRefused()
    {
        byte[] source = Portfolio("", "(a.txt) 10 0 R",
            (10, Spec("(a.txt)", 20)), (20, Embedded("text", "abc")));
        var editor = new PdfIncrementalPageEditor(PdfDocument.Open(source));

        Assert.Throws<ArgumentException>(() => editor.RenameAttachmentByKey("a.txt", "bad:name.txt"));
        Assert.Throws<ArgumentException>(() => editor.SetAttachmentClassificationByKey(
            "a.txt", "text", PdfAssociatedFileRelationship.Data));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.SetAttachmentClassificationByKey(
            "a.txt", "text/plain", (PdfAssociatedFileRelationship)99));
    }

    // pdf.js-12025-1.pdf has 17 keys that all name one file specification.
    [Fact]
    public void EditingOneOfSeveralKeysOnASharedSpecificationChangesThatRowOnly()
    {
        byte[] source = SharedSpecification();

        byte[] described = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetAttachmentDescriptionByKey("k1", "reviewed").Build();
        PdfAttachmentInfo[] afterDescription = [.. PdfAttachmentReader.Read(PdfDocument.Open(described))];
        Assert.Equal(3, afterDescription.Length);
        Assert.Equal("reviewed", Assert.Single(afterDescription, item => item.NameTreeKey == "k1").Description);
        Assert.All(afterDescription.Where(item => item.NameTreeKey != "k1"),
            item => Assert.Equal("shared", item.Description));

        byte[] replaced = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .ReplaceAttachmentByKey("k2", "changed"u8.ToArray()).Build();
        PdfAttachmentInfo[] afterReplace = [.. PdfAttachmentReader.Read(PdfDocument.Open(replaced))];
        Assert.Equal("changed"u8.ToArray(), Assert.Single(afterReplace, item => item.NameTreeKey == "k2").Data.ToArray());
        Assert.All(afterReplace.Where(item => item.NameTreeKey != "k2"),
            item => Assert.Equal("abc"u8.ToArray(), item.Data.ToArray()));
    }

    [Fact]
    public void TheSharedSpecificationStaysAssociatedWhileAnotherKeyUsesIt()
    {
        byte[] source = SharedSpecification();

        byte[] edited = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetAttachmentDescriptionByKey("k1", "reviewed").Build();
        byte[] removed = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .RemoveAttachmentByKey("k3").Build();

        Assert.Contains(10, AssociatedObjectNumbers(edited));
        Assert.Equal(2, AssociatedObjectNumbers(edited).Count);
        Assert.Equal([10], AssociatedObjectNumbers(removed));
        Assert.Equal(["k1", "k2"], PdfAttachmentReader.Read(PdfDocument.Open(removed))
            .Select(item => item.NameTreeKey));
    }

    [Fact]
    public void ARenameOntoAnotherFilesHiddenKeyKeepsTheTypedNameAndTouchesNothingElse()
    {
        byte[] source = Portfolio("", "(b.txt) 10 0 R (z.txt) 11 0 R",
            (10, Spec("(b.txt)", 20)), (11, Spec("(listed.txt)", 21)),
            (20, Embedded("text#2Fplain", "bee")), (21, Embedded("text#2Fplain", "zed")));

        byte[] output = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .RenameAttachmentByKey("b.txt", "z.txt").Build();

        PdfAttachmentInfo[] attachments = [.. PdfAttachmentReader.Read(PdfDocument.Open(output))];
        Assert.Equal(2, attachments.Length);
        PdfAttachmentInfo renamed = Assert.Single(attachments, item => item.FileName == "z.txt");
        Assert.Equal("bee"u8.ToArray(), renamed.Data.ToArray());
        Assert.NotEqual("z.txt", renamed.NameTreeKey);
        PdfAttachmentInfo other = Assert.Single(attachments, item => item.FileName == "listed.txt");
        Assert.Equal("z.txt", other.NameTreeKey);
        Assert.Equal("zed"u8.ToArray(), other.Data.ToArray());

        byte[] removed = new PdfIncrementalPageEditor(PdfDocument.Open(output)).RemoveAttachment("z.txt").Build();
        Assert.Equal("listed.txt", Assert.Single(PdfAttachmentReader.Read(PdfDocument.Open(removed))).FileName);
    }

    [Fact]
    public void AFileUnderABlankKeyCanBeRemovedDescribedAndRenamed()
    {
        byte[] source = Portfolio("", "() 10 0 R ( ) 11 0 R (c.txt) 12 0 R",
            (10, Spec("(first.txt)", 20)), (11, Spec("(second.txt)", 21)), (12, Spec("(c.txt)", 22)),
            (20, Embedded("text#2Fplain", "one")), (21, Embedded("text#2Fplain", "two")),
            (22, Embedded("text#2Fplain", "three")));

        byte[] removed = new PdfIncrementalPageEditor(PdfDocument.Open(source)).RemoveAttachmentByKey("").Build();
        Assert.Equal(["second.txt", "c.txt"], PdfAttachmentReader.Read(PdfDocument.Open(removed))
            .Select(item => item.FileName));

        byte[] described = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetAttachmentDescriptionByKey(" ", "spaced").Build();
        Assert.Equal("spaced", Assert.Single(PdfAttachmentReader.Read(PdfDocument.Open(described)),
            item => item.FileName == "second.txt").Description);

        byte[] renamed = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .RenameAttachmentByKey("", "named.txt").Build();
        Assert.Equal("one"u8.ToArray(), Assert.Single(PdfAttachmentReader.Read(PdfDocument.Open(renamed)),
            item => item.FileName == "named.txt").Data.ToArray());
        Assert.Throws<ArgumentException>(() =>
            new PdfIncrementalPageEditor(PdfDocument.Open(source)).RenameAttachmentByKey("c.txt", " "));
    }

    // Two keys can decode to the same text when one is PDFDocEncoded and the other UTF-16.
    [Fact]
    public void KeysThatDecodeAlikeDoNotBlockEditsToTheDocument()
    {
        byte[] source = Portfolio("", "(a.txt) 10 0 R (c.txt) 12 0 R <FEFF0061002E007400780074> 11 0 R",
            (10, Spec("(a.txt)", 20)), (11, Spec("(a.txt)", 21)), (12, Spec("(c.txt)", 22)),
            (20, Embedded("text#2Fplain", "one")), (21, Embedded("text#2Fplain", "two")),
            (22, Embedded("text#2Fplain", "three")));

        byte[] other = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetAttachmentDescriptionByKey("c.txt", "only").Build();
        Assert.Equal(3, PdfAttachmentReader.Read(PdfDocument.Open(other)).Count);

        byte[] twin = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetAttachmentDescriptionByKey("a.txt", "one of two").Build();
        PdfAttachmentInfo[] afterEdit = [.. PdfAttachmentReader.Read(PdfDocument.Open(twin))];
        Assert.Equal(3, afterEdit.Length);
        Assert.Single(afterEdit, item => item.Description == "one of two");

        byte[] removed = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .RemoveAttachmentByKey("a.txt").Build();
        PdfAttachmentInfo[] afterRemove = [.. PdfAttachmentReader.Read(PdfDocument.Open(removed))];
        Assert.Equal(2, afterRemove.Length);
        Assert.Single(afterRemove, item => item.NameTreeKey == "a.txt");
    }

    // A portfolio's initial document is named by key, so renaming that file renames the key it names.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RenamingTheInitialDocumentOfAPortfolioCarriesTheCollectionAlong(bool indirect)
    {
        string collection = "<< /D (a.txt) /View /D >>";
        byte[] source = Portfolio(indirect ? "/Collection 30 0 R" : "/Collection " + collection,
            "(a.txt) 10 0 R (b.txt) 11 0 R",
            (10, Spec("(a.txt)", 20)), (11, Spec("(b.txt)", 21)),
            (20, Embedded("text#2Fplain", "one")), (21, Embedded("text#2Fplain", "two")),
            (30, collection));

        byte[] output = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .RenameAttachmentByKey("a.txt", "r.txt").Build();
        Assert.Equal("r.txt", PdfCollectionReader.Read(PdfDocument.Open(output))!.InitialDocument);

        byte[] other = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .RenameAttachmentByKey("b.txt", "s.txt").Build();
        Assert.Equal("a.txt", PdfCollectionReader.Read(PdfDocument.Open(other))!.InitialDocument);
    }

    private static byte[] SharedSpecification() => Portfolio("/AF [10 0 R]",
        "(k1) 10 0 R (k2) 10 0 R (k3) 10 0 R",
        (10, Spec("(shared.txt)", 20, "shared")), (20, Embedded("text#2Fplain", "abc")));

    private static List<int> AssociatedObjectNumbers(byte[] file)
    {
        PdfDocument document = PdfDocument.Open(file);
        PdfDictionary catalog = Resolve(document, document.Trailer[Name("Root")]);
        PdfObject value = catalog[Name("AF")];
        PdfArray associated = Assert.IsType<PdfArray>(
            value is PdfIndirectReference reference ? document.Resolve(reference) : value);
        return [.. associated.Select(item => Assert.IsType<PdfIndirectReference>(item).ObjectNumber)];
    }

    private static string Spec(string listedName, int stream, string? description = null) =>
        $"<< /Type /Filespec /F {listedName} /UF {listedName} /EF << /F {stream} 0 R /UF {stream} 0 R >> /AFRelationship /Data"
        + (description is null ? string.Empty : $" /Desc ({description})") + " >>";

    private static string Embedded(string subtype, string payload) =>
        $"<< /Type /EmbeddedFile /Subtype /{subtype} /Params << /Size {payload.Length} >> /Length {payload.Length} >>\nstream\n{payload}\nendstream";

    // A one-page file whose embedded-files name tree holds the given raw key tokens, with the given
    // numbered objects (10 and up) behind it. catalogExtra adds entries to the catalog.
    private static byte[] Portfolio(string catalogExtra, string keyTokens,
        params (int Number, string Body)[] objects)
    {
        var bodies = new Dictionary<int, string>
        {
            [1] = $"<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [{keyTokens}] >> >> {catalogExtra} >>",
            [2] = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>"
        };
        foreach (var (number, body) in objects) bodies[number] = body;
        int size = bodies.Keys.Max() + 1;
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new Dictionary<int, int>();
        foreach (var (number, body) in bodies.OrderBy(entry => entry.Key))
        {
            offsets[number] = text.Length;
            text.Append($"{number} 0 obj\n{body}\nendobj\n");
        }
        int xref = text.Length;
        text.Append($"xref\n0 {size}\n0000000000 65535 f \n");
        for (int number = 1; number < size; number++)
            text.Append(offsets.TryGetValue(number, out int offset)
                ? $"{offset:0000000000} 00000 n \n" : "0000000000 65535 f \n");
        text.Append($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    /// <summary>An attachment whose /UF and /F differ from its name-tree key, as PDFBox and LaTeX write them.</summary>
    private static byte[] DifferentlyNamedSource()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment(TreeKey, "payload"u8.ToArray(), "text/plain")
            .Build();
        PdfDocument document = PdfDocument.Open(source);
        PdfDictionary catalog = Resolve(document, document.Trailer[Name("Root")]);
        PdfDictionary names = Resolve(document, catalog[Name("Names")]);
        PdfDictionary tree = Resolve(document, names[Name("EmbeddedFiles")]);
        PdfArray files = Assert.IsType<PdfArray>(tree[Name("Names")]);
        PdfIndirectReference reference = Assert.IsType<PdfIndirectReference>(files[1]);
        PdfDictionary specification = Resolve(document, reference);
        PdfString listed = new(Encoding.ASCII.GetBytes(ListedName), PdfStringForm.Literal);
        PdfDictionary renamed = new(specification.Select(entry =>
            entry.Key.Equals(Name("UF")) || entry.Key.Equals(Name("F"))
                ? new KeyValuePair<PdfName, PdfObject>(entry.Key, listed) : entry));
        return new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(reference.ObjectNumber, renamed).Build();
    }

    private static PdfDictionary Resolve(PdfDocument document, PdfObject value) =>
        Assert.IsType<PdfDictionary>(value is PdfIndirectReference reference
            ? document.Resolve(reference) : value);

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
