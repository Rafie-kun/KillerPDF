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
