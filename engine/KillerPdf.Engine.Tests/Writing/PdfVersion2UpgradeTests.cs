using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

public sealed class PdfVersion2UpgradeTests
{
    // ImageMagick ends /Title with U+0000, which a PDF string can hold and an XMP packet cannot.
    [Fact]
    public void Apply_ATitleThatXmlCannotCarryStillUpgrades()
    {
        const string packet =
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\"/></rdf:RDF></x:xmpmeta>";
        byte[] source = RawPdf("1.7", "/Title <FEFF0041000000420000>", packet);

        PdfDocument upgraded = PdfDocument.Open(PdfVersion2Upgrade.Apply(PdfDocument.Open(source)));

        Assert.Equal("AB", PdfDocumentInformation.Read(upgraded).Title?.Replace("\0", string.Empty, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PdfTrappedStatus.True, false)]
    [InlineData(PdfTrappedStatus.False, false)]
    [InlineData(PdfTrappedStatus.Unknown, false)]
    [InlineData(PdfTrappedStatus.True, true)]
    [InlineData(PdfTrappedStatus.False, true)]
    [InlineData(PdfTrappedStatus.Unknown, true)]
    public void Apply_PreservesTrappingInNewAndExistingXmp(PdfTrappedStatus trapped, bool existingXmp)
    {
        const string packet =
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:custom=\"http://example.com/ns/\">" +
            "<custom:Department>Finance</custom:Department></rdf:Description></rdf:RDF></x:xmpmeta>";
        byte[] source = RawPdf("1.7", $"/Title (Report) /Trapped /{trapped}",
            existingXmp ? packet : null);

        PdfDocument upgraded = PdfDocument.Open(PdfVersion2Upgrade.Apply(PdfDocument.Open(source)));

        Assert.Equal(trapped, PdfDocumentInformation.Read(upgraded).Trapped);
        Assert.DoesNotContain("Trapped", InfoKeys(upgraded));
        var reference = (PdfIndirectReference)PdfPageTree.Read(upgraded).Catalog[new PdfName("Metadata"u8)];
        var metadata = (PdfStream)upgraded.Resolve(reference);
        var xml = System.Xml.Linq.XDocument.Parse(
            Encoding.UTF8.GetString(PdfStreamDecoder.Decode(metadata, upgraded.Resolve)));
        Assert.Equal(trapped.ToString(),
            xml.Descendants(System.Xml.Linq.XName.Get("Trapped", "http://ns.adobe.com/pdf/1.3/")).Single().Value);
        if (existingXmp)
            Assert.Equal("Finance",
                xml.Descendants(System.Xml.Linq.XName.Get("Department", "http://example.com/ns/")).Single().Value);
    }

    [Fact]
    public void Apply_MovesDocumentInformationIntoXmpAndKeepsOnlyTheDatesInInfo()
    {
        var created = new DateTimeOffset(2024, 3, 5, 10, 20, 30, TimeSpan.FromHours(-5));
        var modified = new DateTimeOffset(2025, 1, 2, 8, 0, 0, TimeSpan.Zero);
        byte[] source = new PdfDocumentBuilder(PdfVersion.Pdf17)
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = "Quarterly report",
                Author = "Steve",
                Subject = "Sales figures",
                Keywords = "q3, sales",
                Creator = "Word",
                Producer = "Old producer",
                CreationDate = created,
                ModificationDate = modified
            })
            .AddPage(300, 400, Text("upgrade me"))
            .Build();

        PdfDocument upgraded = PdfDocument.Open(
            PdfVersion2Upgrade.Apply(PdfDocument.Open(source)));

        Assert.Equal(PdfVersion.Pdf20, upgraded.Header.Version);
        PdfDocumentInformation information = PdfDocumentInformation.Read(upgraded);
        Assert.Equal("Quarterly report", information.Title);
        Assert.Equal("Steve", information.Author);
        Assert.Equal("Sales figures", information.Subject);
        Assert.Equal("q3, sales", information.Keywords);
        Assert.Equal("Word", information.Creator);
        Assert.Equal("Old producer", information.Producer);
        Assert.Equal(created, information.CreationDate);
        Assert.Equal(modified, information.ModificationDate);
        Assert.Equal(["CreationDate", "ModDate"], InfoKeys(upgraded));
        Assert.Equal("upgrade me", new PdfPageContentReader(upgraded).Read(0).Text);
    }

    [Fact]
    public void Apply_WritesXmpMetadataFromInfoWhenTheFileHasNone()
    {
        byte[] source = RawPdf("1.4",
            "/Title <FEFF004300610066006503010020007200650070006F00720074> /Author (Ann Lee) " +
            "/Subject (Menu) /Keywords (food) /Creator (Writer) /Producer (Old press) " +
            "/CreationDate (D:20190607080910+02'00')");

        PdfDocument upgraded = PdfDocument.Open(
            PdfVersion2Upgrade.Apply(PdfDocument.Open(source)));

        PdfDocumentInformation information = PdfDocumentInformation.Read(upgraded);
        Assert.Equal("Cafe\u0301 report", information.Title);
        Assert.Equal("Ann Lee", information.Author);
        Assert.Equal("Menu", information.Subject);
        Assert.Equal("food", information.Keywords);
        Assert.Equal("Writer", information.Creator);
        Assert.Equal("Old press", information.Producer);
        Assert.Equal(new DateTimeOffset(2019, 6, 7, 8, 9, 10, TimeSpan.FromHours(2)),
            information.CreationDate);
        Assert.Equal(["CreationDate"], InfoKeys(upgraded));
    }

    [Fact]
    public void Apply_AddsInfoFieldsMissingFromAnExistingXmpPacketAndKeepsItsOtherProperties()
    {
        const string packet =
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" " +
            "xmlns:custom=\"http://example.com/ns/\">" +
            "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">XMP title</rdf:li></rdf:Alt></dc:title>" +
            "<custom:Department>Finance</custom:Department>" +
            "</rdf:Description></rdf:RDF></x:xmpmeta>";
        byte[] source = RawPdf("1.6", "/Title (Info title) /Author (Info author) /Producer (Info press)",
            packet);

        byte[] upgraded = PdfVersion2Upgrade.Apply(PdfDocument.Open(source));

        PdfDocumentInformation information = PdfDocumentInformation.Read(PdfDocument.Open(upgraded));
        Assert.Equal("XMP title", information.Title);
        Assert.Equal("Info author", information.Author);
        Assert.Equal("Info press", information.Producer);
        Assert.Contains("<custom:Department>Finance</custom:Department>",
            Encoding.UTF8.GetString(upgraded));
    }

    [Fact]
    public void Read_UsesValidInfoWhenOptionalXmpIsMalformed()
    {
        byte[] source = RawPdf("1.7", "/Title (Valid title)", "<not-closed>");

        PdfDocumentInformation information = PdfDocumentInformation.Read(PdfDocument.Open(source));

        Assert.Equal("Valid title", information.Title);
        Assert.Equal(1, information.PageCount);
        Assert.Throws<InvalidOperationException>(() => PdfVersion2Upgrade.Apply(PdfDocument.Open(source)));
    }

    [Fact]
    public void Apply_AddsMissingFieldsToTheDocumentRdfSubject()
    {
        const string packet =
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description rdf:about=\"other\" xmlns:custom=\"http://example.com/ns/\">" +
            "<custom:Value>keep</custom:Value></rdf:Description>" +
            "<rdf:Description rdf:about=\"\" /></rdf:RDF></x:xmpmeta>";
        byte[] source = RawPdf("1.7", "/Author (Document author)", packet);

        byte[] upgraded = PdfVersion2Upgrade.Apply(PdfDocument.Open(source));
        string text = Encoding.UTF8.GetString(upgraded);

        Assert.Matches("rdf:Description rdf:about=\"\"[^>]*>.*Document author.*</rdf:Description>", text);
        Assert.Equal("Document author", PdfDocumentInformation.Read(PdfDocument.Open(upgraded)).Author);
    }

    [Fact]
    public void Apply_RemovesProcedureSetsImageAlternatesAndOpi()
    {
        byte[] source = RawObjects("1.3",
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /Resources << /ProcSet [/PDF] >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Contents 4 0 R " +
                "/Resources << /ProcSet [/PDF /Text /ImageB] " +
                "/XObject << /Im1 5 0 R /Fm1 6 0 R >> /Font << /F1 7 0 R >> >> >>",
            Stream("", "q 10 0 0 10 0 0 cm /Im1 Do Q /Fm1 Do " +
                "BT /F1 12 Tf 20 30 Td (still here) Tj ET"),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray " +
                "/BitsPerComponent 8 /Alternates [<< /Image 9 0 R >>] " +
                "/OPI << /2.0 << /Type /OPI /Version 2.0 /F (photo.tif) /Inks /full_color >> >>", "A"),
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 10 10] " +
                "/OPI << /1.3 << /Type /OPI /Version 1.3 /F (art.tif) /Size [1 1] " +
                "/CropRect [0 0 1 1] /Position [0 0 0 1 1 1 1 0] >> >>", ""),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray " +
                "/BitsPerComponent 8", "B"));

        byte[] upgraded = PdfVersion2Upgrade.Apply(PdfDocument.Open(source));

        string text = Encoding.Latin1.GetString(upgraded);
        Assert.DoesNotContain("/ProcSet", text);
        Assert.DoesNotContain("/Alternates", text);
        Assert.DoesNotContain("/OPI", text);
        Assert.Equal("still here", new PdfPageContentReader(PdfDocument.Open(upgraded)).Read(0).Text);
    }

    [Fact]
    public void Apply_RejectsPostScriptXObjectsInsteadOfDiscardingThem()
    {
        byte[] source = RawObjects("1.7",
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << /XObject << /Ps1 4 0 R >> >> >>",
            Stream("/Type /XObject /Subtype /PS", "%!PS"));

        Assert.Throws<NotSupportedException>(() => PdfVersion2Upgrade.Apply(PdfDocument.Open(source)));
    }

    private static string[] InfoKeys(PdfDocument document)
    {
        if (!document.Trailer.TryGetValue(new PdfName("Info"u8), out PdfObject? value))
            return [];
        if (value is PdfIndirectReference reference) value = document.Resolve(reference);
        return [.. ((PdfDictionary)value).Keys
            .Select(key => Encoding.ASCII.GetString(key.Bytes.Span))
            .Order(StringComparer.Ordinal)];
    }

    // A minimal one-page file with a trailer /Info dictionary and, optionally, an XMP packet.
    private static byte[] RawPdf(string version, string information, string? xmp = null)
    {
        var source = new StringBuilder($"%PDF-{version}\n");
        var offsets = new List<int>();
        void Add(string body)
        {
            offsets.Add(source.Length);
            source.Append($"{offsets.Count} 0 obj {body} endobj\n");
        }
        Add(xmp is null ? "<< /Type /Catalog /Pages 2 0 R >>"
            : "<< /Type /Catalog /Pages 2 0 R /Metadata 5 0 R >>");
        Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] >>");
        Add($"<< {information} >>");
        if (xmp is not null)
            Add($"<< /Type /Metadata /Subtype /XML /Length {Encoding.UTF8.GetByteCount(xmp)} >>\nstream\n{xmp}\nendstream");
        int xref = source.Length;
        source.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) source.Append($"{offset:0000000000} 00000 n \n");
        source.Append($"trailer << /Size {offsets.Count + 1} /Root 1 0 R /Info 4 0 R >>\n");
        source.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.UTF8.GetBytes(source.ToString());
    }

    // Numbered objects in order, the first being the catalog, with no trailer /Info.
    private static byte[] RawObjects(string version, params string[] bodies)
    {
        var source = new StringBuilder($"%PDF-{version}\n");
        var offsets = new List<int>();
        foreach (string body in bodies)
        {
            offsets.Add(source.Length);
            source.Append($"{offsets.Count} 0 obj {body} endobj\n");
        }
        int xref = source.Length;
        source.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) source.Append($"{offset:0000000000} 00000 n \n");
        source.Append($"trailer << /Size {offsets.Count + 1} /Root 1 0 R >>\n");
        source.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(source.ToString());
    }

    private static string Stream(string entries, string data) =>
        $"<< {entries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static PdfContentStreamBuilder Text(string text) =>
        new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .MoveText(20, 30).ShowLatin1Text(text).EndText();
}
