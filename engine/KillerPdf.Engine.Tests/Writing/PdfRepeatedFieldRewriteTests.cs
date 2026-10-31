using System.Globalization;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

// A field listed twice in the form is damage, and a viewer shows it once. A repairing rewrite keeps
// the field and what it holds, lists it once, and still refuses a signed file.
public sealed class PdfRepeatedFieldRewriteTests
{
    private static readonly PdfDocumentWriteOptions Repair = new() { ReplaceDanglingReferences = true };

    [Fact]
    public void AFieldListedTwiceInTheFormIsKeptOnce()
    {
        PdfDocument source = PdfDocument.Open(Form("/Fields [5 0 R 5 0 R]",
            "<< /FT /Tx /T (a) /V (kept) /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 3 0 R >>"));
        Assert.Throws<InvalidOperationException>(() => PdfSignatureReader.Read(source));

        PdfDocument output = PdfDocument.Open(PdfDocumentWriter.WriteWithReport(source, Repair).Data);

        Assert.Empty(PdfSignatureReader.Read(output));
        Assert.Equal("kept", PdfFormCalculation.ReadValues(output)["a"]);
        PdfDictionary form = Assert.IsType<PdfDictionary>(Catalog(output)[N("AcroForm")]);
        Assert.Single(Assert.IsType<PdfArray>(form[N("Fields")]));
        Assert.Equal("a", Assert.Single(PdfFormWidgetReader.ReadPage(output, 0)).FieldName);
    }

    [Fact]
    public void AKidListedTwiceUnderAFieldIsKeptOnce()
    {
        PdfDocument source = PdfDocument.Open(Form("/Fields [5 0 R]",
            "<< /FT /Tx /T (parent) /Kids [6 0 R 6 0 R] >>",
            "<< /T (child) /V (kept) /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 3 0 R /Parent 5 0 R >>"));

        PdfDocument output = PdfDocument.Open(PdfDocumentWriter.WriteWithReport(source, Repair).Data);

        Assert.Empty(PdfSignatureReader.Read(output));
        Assert.Equal("kept", PdfFormCalculation.ReadValues(output)["parent.child"]);
        PdfDictionary form = Assert.IsType<PdfDictionary>(Catalog(output)[N("AcroForm")]);
        PdfDictionary parent = Assert.IsType<PdfDictionary>(output.Resolve(
            Assert.IsType<PdfIndirectReference>(Assert.Single(Assert.IsType<PdfArray>(form[N("Fields")])))));
        Assert.Single(Assert.IsType<PdfArray>(parent[N("Kids")]));
    }

    [Fact]
    public void AFormWithNoRepeatedFieldIsRewrittenAsItWas()
    {
        PdfDocument source = PdfDocument.Open(Form("/Fields [5 0 R]",
            "<< /FT /Tx /T (a) /V (kept) /Type /Annot /Subtype /Widget /Rect [0 0 50 20] /P 3 0 R >>"));

        PdfDocument output = PdfDocument.Open(PdfDocumentWriter.WriteWithReport(source, Repair).Data);

        Assert.Equal("kept", PdfFormCalculation.ReadValues(output)["a"]);
    }

    // The tolerance is for finding what is signed, never for rewriting it.
    [Fact]
    public void ASignedFieldListedTwiceStillRefusesTheRewrite()
    {
        PdfDocument source = PdfDocument.Open(Form("/Fields [5 0 R 5 0 R]",
            "<< /FT /Sig /T (s) /V 6 0 R /Type /Annot /Subtype /Widget /Rect [0 0 0 0] /P 3 0 R >>",
            "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 10 20 30] /Contents <0000> >>"));

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfDocumentWriter.WriteWithReport(source, Repair));

        Assert.Contains("signature", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("same field", error.Message, StringComparison.Ordinal);
    }

    private static PdfDictionary Catalog(PdfDocument document) => Assert.IsType<PdfDictionary>(
        document.Resolve(Assert.IsType<PdfIndirectReference>(document.Trailer[N("Root")])));

    private static PdfName N(string value) => new(Encoding.ASCII.GetBytes(value));

    // A one-page file whose catalog holds the given form entries, then the given objects numbered from 5.
    private static byte[] Form(string formEntries, params string[] fieldObjects)
    {
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R /AcroForm << {formEntries} >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Annots [5 0 R] >>",
            "null"
        };
        objects.AddRange(fieldObjects);
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(text.Length);
            text.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xrefOffset = text.Length;
        text.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            text.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        text.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }
}
