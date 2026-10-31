using System.Globalization;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

// A page copy of a whole document carries its attachments and the catalog /AF index along. The /AF
// index only repeats file specifications other places name, so one entry nothing can read must not
// stop the copy, and a file specification whose /Type is spelled /FileSpec is still an attachment.
public sealed class PdfAssociatedFilesImportTests
{
    [Fact]
    public void AFileSpecificationTypedFileSpecIsCarriedWithItsAttachment()
    {
        byte[] built = Import(Source(type: "/FileSpec", associated: "[6 0 R]"));

        PdfDocument output = PdfDocument.Open(built);
        PdfAttachmentInfo attachment = Assert.Single(PdfAttachmentReader.Read(output));
        Assert.Equal("a.xml", attachment.FileName);
        Assert.Equal("hello", Encoding.ASCII.GetString(attachment.Data.Span));
        Assert.Single(Assert.IsType<PdfArray>(Catalog(output)[Name("AF")]));
    }

    [Theory]
    [InlineData("[6 0 R (not a file)]")]
    [InlineData("[6 0 R 5 0 R]")]
    [InlineData("[6 0 R << /Type /Filespec >>]")]
    public void AnAssociatedFilesEntryNothingCanReadIsLeftOutAndTheRestIsKept(string associated)
    {
        byte[] built = Import(Source(type: "/Filespec", associated: associated));

        PdfDocument output = PdfDocument.Open(built);
        Assert.Equal("a.xml", Assert.Single(PdfAttachmentReader.Read(output)).FileName);
        Assert.Single(Assert.IsType<PdfArray>(Catalog(output)[Name("AF")]));
    }

    [Fact]
    public void AnAssociatedFilesValueThatIsNotAnArrayIsLeftOut()
    {
        byte[] built = Import(Source(type: "/Filespec", associated: "42"));

        PdfDocument output = PdfDocument.Open(built);
        Assert.Equal("a.xml", Assert.Single(PdfAttachmentReader.Read(output)).FileName);
        Assert.False(Catalog(output).ContainsKey(Name("AF")));
    }

    private static byte[] Import(byte[] source) =>
        new PdfIncrementalPageEditor(PdfDocument.Open(new PdfDocumentBuilder().Build()))
            .AddImportedDocument(PdfDocument.Open(source)).Build();

    private static PdfDictionary Catalog(PdfDocument document) => Assert.IsType<PdfDictionary>(
        document.Resolve(Assert.IsType<PdfIndirectReference>(document.Trailer[Name("Root")])));

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static byte[] Source(string type, string associated)
    {
        var source = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[8];
        Add(1, "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(a.xml) 6 0 R] >> >> " +
            $"/AF {associated} >>");
        Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Add(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources <<>> >>");
        Add(4, "null");
        Add(5, "<< /Type /EmbeddedFile /Length 5 >>\nstream\nhello\nendstream");
        Add(6, $"<< /Type {type} /F (a.xml) /UF (a.xml) /EF << /F 5 0 R >> /AFRelationship /Data >>");
        Add(7, "null");
        int xrefOffset = source.Length;
        source.Append("xref\n0 8\n0000000000 65535 f \n");
        for (int index = 1; index <= 7; index++)
            source.Append(offsets[index].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        source.Append("trailer\n<< /Size 8 /Root 1 0 R >>\nstartxref\n")
            .Append(xrefOffset.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(source.ToString());

        void Add(int number, string value)
        {
            offsets[number] = source.Length;
            source.Append(number).Append(" 0 obj\n").Append(value).Append("\nendobj\n");
        }
    }
}
