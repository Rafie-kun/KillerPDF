using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

// Descriptive metadata read from the catalog's XMP packet. PDF 2.0 deprecates the trailer /Info
// dictionary, so its title, author and other fields live here instead.
internal sealed record PdfXmpDocumentMetadata(
    string? Title, string? Author, string? Subject, string? Keywords, string? Creator,
    string? Producer, DateTimeOffset? CreationDate, DateTimeOffset? ModificationDate,
    PdfTrappedStatus? Trapped)
{
    private const int MaximumPacketBytes = 64 * 1024 * 1024;
    private static readonly XNamespace Rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Pdf = "http://ns.adobe.com/pdf/1.3/";
    private static readonly XNamespace Xmp = "http://ns.adobe.com/xap/1.0/";

    /// <summary>Reads the catalog XMP packet, or returns null when the catalog has none.</summary>
    internal static PdfXmpDocumentMetadata? Read(PdfDocument document) =>
        Load(document) is XDocument xmp ? From(xmp) : null;

    /// <summary>
    /// Returns the catalog XMP packet with every given field it lacks added, leaving the fields it
    /// already has and every other property untouched, or null when nothing was missing.
    /// </summary>
    internal static byte[]? AddMissing(PdfDocument document, PdfDocumentInformation fields)
    {
        XDocument xmp = Load(document)
            ?? throw new InvalidOperationException("The catalog has no XMP metadata.");
        PdfXmpDocumentMetadata present = From(xmp);
        XElement rdf = xmp.Descendants(Rdf + "RDF").FirstOrDefault()
            ?? throw new InvalidOperationException("The catalog XMP metadata has no rdf:RDF element.");
        XElement description = rdf.Elements(Rdf + "Description").FirstOrDefault(IsDocumentDescription)
            ?? AddDescription(rdf);
        int before = description.Elements().Count();
        if (present.Title is null) AddAlternative(description, Dc + "title", fields.Title);
        if (present.Author is null) AddSequence(description, Dc + "creator", fields.Author);
        if (present.Subject is null) AddAlternative(description, Dc + "description", fields.Subject);
        if (present.Keywords is null) AddSimple(description, Pdf + "Keywords", fields.Keywords);
        if (present.Creator is null) AddSimple(description, Xmp + "CreatorTool", fields.Creator);
        if (present.Producer is null) AddSimple(description, Pdf + "Producer", fields.Producer);
        if (present.Trapped is null) AddSimple(description, Pdf + "Trapped", fields.Trapped?.ToString());
        if (present.CreationDate is null)
            AddSimple(description, Xmp + "CreateDate", XmpDate(fields.CreationDate));
        if (present.ModificationDate is null)
            AddSimple(description, Xmp + "ModifyDate", XmpDate(fields.ModificationDate));
        if (description.Elements().Count() == before) return null;
        using var output = new MemoryStream();
        using (XmlWriter writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            OmitXmlDeclaration = true,
            Indent = false,
            NewLineHandling = NewLineHandling.None
        }))
            xmp.Save(writer);
        return output.ToArray();
    }

    private static XDocument? Load(PdfDocument document)
    {
        PdfDictionary catalog = PdfPageTree.Read(document).Catalog;
        if (!catalog.TryGetValue(new PdfName("Metadata"u8), out PdfObject? value)) return null;
        PdfStream stream = Resolve(document, value) as PdfStream
            ?? throw new InvalidOperationException("The catalog /Metadata value is not a stream.");
        byte[] packet = PdfStreamDecoder.Decode(stream, document.Resolve, MaximumPacketBytes);
        try
        {
            using var input = new MemoryStream(packet, writable: false);
            using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumPacketBytes
            });
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException error)
        {
            throw new InvalidOperationException("The catalog XMP metadata is not well-formed XML.", error);
        }
    }

    private static PdfXmpDocumentMetadata From(XDocument xmp)
    {
        XElement[] descriptions = [.. xmp.Descendants(Rdf + "RDF")
            .Elements(Rdf + "Description").Where(IsDocumentDescription)];
        return new(
            Container(descriptions, Dc + "title"),
            Container(descriptions, Dc + "creator"),
            Container(descriptions, Dc + "description"),
            Simple(descriptions, Pdf + "Keywords"),
            Simple(descriptions, Xmp + "CreatorTool"),
            Simple(descriptions, Pdf + "Producer"),
            Date(Simple(descriptions, Xmp + "CreateDate")),
            Date(Simple(descriptions, Xmp + "ModifyDate")),
            Simple(descriptions, Pdf + "Trapped") switch
            {
                "True" => PdfTrappedStatus.True,
                "False" => PdfTrappedStatus.False,
                "Unknown" => PdfTrappedStatus.Unknown,
                _ => null
            });
    }

    private static bool IsDocumentDescription(XElement description) =>
        string.IsNullOrEmpty((string?)description.Attribute(Rdf + "about"));

    // A property can be written as an element or, in the abbreviated RDF form, as an attribute
    // of rdf:Description.
    private static string? Simple(IEnumerable<XElement> descriptions, XName name) =>
        descriptions.Elements(name).Select(element => element.Value.Trim())
            .FirstOrDefault(text => text.Length > 0)
        ?? descriptions.Attributes(name)
            .Select(attribute => attribute.Value.Trim())
            .FirstOrDefault(text => text.Length > 0);

    // dc:title and dc:description are language alternatives (x-default first); dc:creator is an
    // ordered list of names, joined the way the Info /Author string carries several authors.
    private static string? Container(IEnumerable<XElement> descriptions, XName name)
    {
        XElement? property = descriptions.Elements(name).FirstOrDefault();
        if (property is null) return Simple(descriptions, name);
        XElement[] items = [.. property.Descendants(Rdf + "li")];
        if (items.Length == 0)
        {
            string direct = property.Value.Trim();
            return direct.Length > 0 ? direct : null;
        }
        XElement? preferred = items.FirstOrDefault(item =>
            (string?)item.Attribute(XNamespace.Xml + "lang") == "x-default");
        if (property.Elements(Rdf + "Alt").Any())
            return NonEmpty((preferred ?? items[0]).Value);
        return NonEmpty(string.Join("; ", items.Select(item => item.Value.Trim())
            .Where(text => text.Length > 0)));
    }

    private static XElement AddDescription(XElement rdf)
    {
        var description = new XElement(Rdf + "Description", new XAttribute(Rdf + "about", string.Empty));
        rdf.Add(description);
        return description;
    }

    private static void AddSimple(XElement description, XName name, string? value)
    {
        value = PdfDocumentBuilder.XmlSafe(value);
        if (!string.IsNullOrEmpty(value)) description.Add(new XElement(name, value));
    }

    private static void AddAlternative(XElement description, XName name, string? value)
    {
        value = PdfDocumentBuilder.XmlSafe(value);
        if (string.IsNullOrEmpty(value)) return;
        description.Add(new XElement(name, new XElement(Rdf + "Alt",
            new XElement(Rdf + "li", new XAttribute(XNamespace.Xml + "lang", "x-default"), value))));
    }

    private static void AddSequence(XElement description, XName name, string? value)
    {
        value = PdfDocumentBuilder.XmlSafe(value);
        if (string.IsNullOrEmpty(value)) return;
        description.Add(new XElement(name, new XElement(Rdf + "Seq", new XElement(Rdf + "li", value))));
    }

    private static string? XmpDate(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private static string? NonEmpty(string value)
    {
        value = value.Trim();
        return value.Length > 0 ? value : null;
    }

    // XMP dates follow ISO 8601 with optional precision: a year, a month, a day, minutes or
    // seconds, with an optional fraction and time zone.
    private static DateTimeOffset? Date(string? value)
    {
        if (value is null) return null;
        string[] formats =
        [
            "yyyy", "yyyy-MM", "yyyy-MM-dd",
            "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
            "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"
        ];
        return DateTimeOffset.TryParseExact(value, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out DateTimeOffset result)
            ? result
            : throw new InvalidOperationException($"The XMP date '{value}' is not a valid date.");
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("The XMP metadata reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }
}
