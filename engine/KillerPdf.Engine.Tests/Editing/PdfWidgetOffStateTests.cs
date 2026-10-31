using System.Globalization;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

// ISO 32000 makes the Off appearance of a check box or radio button optional, and a viewer draws
// nothing for a state the normal appearance does not list. IRS forms carry /AS /Off beside an
// /AP /N that holds only the on state, so a page copy and an annotation edit have to accept them.
public sealed class PdfWidgetOffStateTests
{
    [Fact]
    public void AnImportedPageKeepsAWidgetWhoseOffStateHasNoAppearance()
    {
        PdfDocument target = PdfDocument.Open(new PdfDocumentBuilder().Build());

        byte[] built = new PdfIncrementalPageEditor(target)
            .AddImportedPage(PdfDocument.Open(CheckBoxWithoutOffAppearance()), 0)
            .Build();

        PdfDocument output = PdfDocument.Open(built);
        PdfDictionary widget = OnlyWidget(output);
        Assert.Equal("Off", Assert.IsType<PdfName>(widget[Name("AS")]).ValueAsLatin1());
        PdfDictionary states = Assert.IsType<PdfDictionary>(
            Assert.IsType<PdfDictionary>(Resolve(output, widget[Name("AP")]))[Name("N")]);
        Assert.Equal(["Yes"], states.Keys.Select(key => key.ValueAsLatin1()));
    }

    [Fact]
    public void ANoteCanBeAddedToAPageWhoseWidgetLacksAnOffAppearance()
    {
        PdfDocument source = PdfDocument.Open(CheckBoxWithoutOffAppearance());

        byte[] built = new PdfIncrementalAnnotationEditor(source).AddTextNote(0, 20, 20, "Note").Build();

        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(built)));
    }

    // Only the Off state is optional. A widget that names an on state it does not draw is still a
    // damaged appearance dictionary.
    [Fact]
    public void AnImportedPageStillRejectsAWidgetNamingAnotherMissingState()
    {
        PdfDocument target = PdfDocument.Open(new PdfDocumentBuilder().Build());
        PdfDocument source = PdfDocument.Open(CheckBoxWithoutOffAppearance(state: "Maybe"));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalPageEditor(target).AddImportedPage(source, 0).Build());

        Assert.Contains("/AS state has no matching normal appearance", error.Message, StringComparison.Ordinal);
    }

    private static PdfDictionary OnlyWidget(PdfDocument document)
    {
        PdfDictionary page = Assert.IsType<PdfDictionary>(Resolve(document,
            Assert.IsType<PdfArray>(Resolve(document,
                Assert.IsType<PdfDictionary>(Resolve(document,
                    Assert.IsType<PdfDictionary>(Resolve(document, document.Trailer[Name("Root")]))[Name("Pages")]))[Name("Kids")]))[0]));
        return Assert.IsType<PdfDictionary>(Resolve(document,
            Assert.IsType<PdfArray>(Resolve(document, page[Name("Annots")]))[0]));
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value) =>
        value is PdfIndirectReference reference ? document.Resolve(reference) : value;

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private static byte[] CheckBoxWithoutOffAppearance(string state = "Off")
    {
        var source = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[8];
        Add(1, "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>");
        Add(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Add(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources <<>> /Annots [5 0 R] >>");
        Add(4, "<< /FT /Btn /T (approved) /V /Off /Kids [5 0 R] >>");
        Add(5, "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /P 3 0 R /Rect [10 10 30 30] " +
            $"/AS /{state} /AP << /N << /Yes 6 0 R >> >> >>");
        Add(6, "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length 0 >>stream\n\nendstream");
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
