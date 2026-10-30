using System.Text;
using System.Xml.Linq;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

// Saving descriptive metadata must carry over what the user did not touch and must not stop on text
// that XML cannot hold or on a value kept as it was found.
public sealed class PdfMetadataPreservationTests
{
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Pdf = "http://ns.adobe.com/pdf/1.3/";
    private static readonly XNamespace Foo = "urn:killer:test";

    // ImageMagick ends /Title with U+0000, and some Ghostscript output holds language escapes.
    [Theory]
    [InlineData("aspect\0", "aspect", false)]
    [InlineData("aspect\0", "aspect", true)]
    [InlineData("a\u001Bb\u0002c", "abc", false)]
    [InlineData("a\u001Bb\u0002c", "abc", true)]
    public void ATitleThatXmlCannotCarryStillSavesAndStaysInInfo(string title, string inXmp, bool existingXmp)
    {
        byte[] source = OnePage(existingXmp ? Packet(string.Empty) : null, infoEntries: $"/Title {Utf16(title)}");
        PdfDocumentInformation read = PdfDocumentInformation.Read(PdfDocument.Open(source));
        Assert.Equal(title, read.Title);

        byte[] output = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetMetadata(new PdfDocumentMetadata { Title = read.Title, Author = "Someone" })
            .Build();

        PdfDocument reopened = PdfDocument.Open(output);
        Assert.Equal(title, PdfDocumentInformation.Read(reopened).Title);
        XDocument xmp = ReadXmp(reopened);
        Assert.Equal(inXmp, Assert.Single(xmp.Descendants(Dc + "title").Descendants(Rdf + "li")).Value);
        Assert.Equal("Someone", Assert.Single(xmp.Descendants(Dc + "creator").Descendants(Rdf + "li")).Value);
    }

    [Fact]
    public void AKeywordsOnlySaveKeepsTranslationsLanguageAndEveryCreator()
    {
        byte[] source = OnePage(Packet(
            "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Hello</rdf:li>"
            + "<rdf:li xml:lang=\"de\">Hallo</rdf:li><rdf:li xml:lang=\"fr\">Bonjour</rdf:li></rdf:Alt></dc:title>"
            + "<dc:description><rdf:Alt><rdf:li xml:lang=\"x-default\">About</rdf:li>"
            + "<rdf:li xml:lang=\"de\">Ueber</rdf:li></rdf:Alt></dc:description>"
            + "<dc:creator><rdf:Seq><rdf:li>Alice</rdf:li><rdf:li>Bob</rdf:li></rdf:Seq></dc:creator>"
            + "<dc:language><rdf:Bag><rdf:li>de</rdf:li></rdf:Bag></dc:language>"
            + "<foo:keep>kept</foo:keep>"));
        PdfDocumentInformation read = PdfDocumentInformation.Read(PdfDocument.Open(source));
        Assert.Equal("Alice; Bob", read.Author);

        byte[] output = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = read.Title, Author = read.Author, Subject = read.Subject,
                Keywords = "added", Language = read.Language
            })
            .Build();

        XDocument xmp = ReadXmp(PdfDocument.Open(output));
        Assert.Equal(["Hello", "Hallo", "Bonjour"],
            xmp.Descendants(Dc + "title").Descendants(Rdf + "li").Select(item => item.Value));
        Assert.Equal(["About", "Ueber"],
            xmp.Descendants(Dc + "description").Descendants(Rdf + "li").Select(item => item.Value));
        Assert.Equal(["Alice", "Bob"],
            xmp.Descendants(Dc + "creator").Descendants(Rdf + "li").Select(item => item.Value));
        Assert.Equal("de", Assert.Single(xmp.Descendants(Dc + "language").Descendants(Rdf + "li")).Value);
        Assert.Equal("kept", Assert.Single(xmp.Descendants(Foo + "keep")).Value);
        Assert.Equal("added", Assert.Single(xmp.Descendants(Pdf + "Keywords")).Value);
    }

    [Fact]
    public void ChangingTheTitleReplacesOnlyItsDefaultEntry()
    {
        byte[] source = OnePage(Packet(
            "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Hello</rdf:li>"
            + "<rdf:li xml:lang=\"de\">Hallo</rdf:li></rdf:Alt></dc:title>"));
        PdfDocumentInformation read = PdfDocumentInformation.Read(PdfDocument.Open(source));

        byte[] output = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetMetadata(new PdfDocumentMetadata { Title = "Hi", Author = read.Author })
            .Build();

        PdfDocument reopened = PdfDocument.Open(output);
        Assert.Equal("Hi", PdfDocumentInformation.Read(reopened).Title);
        XElement[] items = [.. ReadXmp(reopened).Descendants(Dc + "title").Descendants(Rdf + "li")];
        Assert.Equal(["Hi", "Hallo"], items.Select(item => item.Value));
        Assert.Equal("x-default", (string?)items[0].Attribute(XNamespace.Xml + "lang"));
    }

    [Fact]
    public void ClearingTheTitleRemovesItAndItsTranslations()
    {
        byte[] source = OnePage(Packet(
            "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">Hello</rdf:li>"
            + "<rdf:li xml:lang=\"de\">Hallo</rdf:li></rdf:Alt></dc:title>"));

        byte[] output = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetMetadata(new PdfDocumentMetadata { Title = null })
            .Build();

        PdfDocument reopened = PdfDocument.Open(output);
        Assert.Null(PdfDocumentInformation.Read(reopened).Title);
        Assert.Empty(ReadXmp(reopened).Descendants(Dc + "title"));
    }

    [Fact]
    public void ADirectInfoKeepsAnUnreadableDateThroughASave()
    {
        byte[] source = OnePage(null, directInfo: "<< /Title (T) /CreationDate (garbage-date) >>");
        PdfDocumentInformation read = PdfDocumentInformation.Read(PdfDocument.Open(source));
        Assert.Equal(["CreationDate"], read.UnreadableKeys);

        byte[] output = new PdfIncrementalPageEditor(PdfDocument.Open(source))
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = read.Title, Author = "New", PreservedInfoKeys = [.. read.UnreadableKeys]
            })
            .Build();

        PdfDocument reopened = PdfDocument.Open(output);
        PdfDocumentInformation after = PdfDocumentInformation.Read(reopened);
        Assert.Equal(["CreationDate"], after.UnreadableKeys);
        Assert.Equal("New", after.Author);
        PdfDictionary info = Assert.IsType<PdfDictionary>(
            reopened.Resolve(Assert.IsType<PdfIndirectReference>(reopened.Trailer[new PdfName("Info"u8)])));
        Assert.Equal("garbage-date", Encoding.ASCII.GetString(
            Assert.IsType<PdfString>(info[new PdfName("CreationDate"u8)]).Bytes.Span));
    }

    [Fact]
    public void ADateThatIsNotKeptAsReadStillMustBeValid()
    {
        byte[] source = OnePage(null);
        PdfDocument document = PdfDocument.Open(source);
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfIndirectReference info = update.AddObject(new PdfDictionary([
            new(new PdfName("CreationDate"u8), new PdfString("garbage"u8.ToArray(), PdfStringForm.Literal))]));

        Assert.Throws<InvalidOperationException>(() => update.SetDocumentInformation(info).Build());
        Assert.Throws<InvalidOperationException>(() => update.SetDocumentInformation(info, ["Title"]).Build());
        Assert.NotEmpty(update.SetDocumentInformation(info, ["CreationDate"]).Build());
    }

    private static string Utf16(string value) =>
        "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(value)) + ">";

    private static XDocument ReadXmp(PdfDocument document)
    {
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(
            document.Resolve(Assert.IsType<PdfIndirectReference>(document.Trailer[new PdfName("Root"u8)])));
        PdfStream stream = Assert.IsType<PdfStream>(
            document.Resolve(Assert.IsType<PdfIndirectReference>(catalog[new PdfName("Metadata"u8)])));
        return XDocument.Parse(Encoding.UTF8.GetString(stream.EncodedData.Span));
    }

    private static string Packet(string properties) =>
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"" + Rdf.NamespaceName + "\">"
        + "<rdf:Description rdf:about=\"\" xmlns:dc=\"" + Dc.NamespaceName + "\" xmlns:pdf=\""
        + Pdf.NamespaceName + "\" xmlns:foo=\"" + Foo.NamespaceName + "\">" + properties
        + "</rdf:Description></rdf:RDF></x:xmpmeta>";

    // A one-page file with an optional XMP packet and an /Info dictionary, either an object or direct.
    private static byte[] OnePage(string? packet, string? infoEntries = null, string? directInfo = null)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R" + (packet is null ? string.Empty : " /Metadata 4 0 R") + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>"
        };
        if (packet is not null)
            objects.Add($"<< /Type /Metadata /Subtype /XML /Length {Encoding.UTF8.GetByteCount(packet)} >>\nstream\n{packet}\nendstream");
        string trailer = directInfo is not null ? $" /Info {directInfo}" : string.Empty;
        if (infoEntries is not null)
        {
            objects.Add($"<< {infoEntries} >>");
            trailer = $" /Info {objects.Count} 0 R";
        }
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(text.Length);
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:0000000000} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R{trailer} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.UTF8.GetBytes(text.ToString());
    }
}
