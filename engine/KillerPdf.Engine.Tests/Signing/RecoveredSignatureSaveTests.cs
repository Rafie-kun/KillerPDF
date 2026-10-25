using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Signing;

public sealed class RecoveredSignatureSaveTests
{
    [Fact]
    public void DefaultSigningOfRecoveredCompressedSourceProducesStrictlyReadableOutput()
    {
        byte[] ordinary = new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "customer", 20, 80, 120, 30, "ALPHA").Build();
        byte[] scannedSource = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(ordinary)
            .Replace("\nxref\n", "\nxrif\n", StringComparison.Ordinal));
        PdfDocument initial = PdfDocument.OpenWithCompatibilityRecovery(scannedSource);
        int fieldObject = Assert.Single(PdfFormWidgetReader.ReadPage(initial, 0)).ObjectNumber;
        var seed = new PdfIncrementalUpdateBuilder(initial);
        seed.ReplaceObject(fieldObject, initial.Resolve(fieldObject));
        byte[] packed = seed.Build(new PdfIncrementalUpdateWriteOptions
            {
                CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
                UseObjectStreams = true,
                CompressObjectStreams = true
            });
        byte[] damaged = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(packed)
            .Replace("/Type /XRef", "/Type /Gone", StringComparison.Ordinal));
        Assert.False(packed.AsSpan().SequenceEqual(damaged));
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(damaged);
        Assert.Equal(recovered.Header.Offset, recovered.CrossReferences.StartXref.Offset);
        Assert.Contains(recovered.CrossReferences.Values,
            entry => entry.Type == PdfCrossReferenceEntryType.Compressed);
        Assert.Equal("ALPHA", Assert.Single(PdfFormWidgetReader.ReadPage(recovered, 0)).Value);

        // A deterministic placeholder exercises serialization without a real signing key.
        byte[] signed = PdfDetachedSignatureWriter.Sign(recovered, _ => [1],
            new PdfSignatureOptions { FieldName = "approval", ReservedSignatureSize = 16 });

        PdfDocument reopened = PdfDocument.Open(signed);
        Assert.Equal(damaged, signed[..damaged.Length]);
        Assert.Single(PdfPageInformation.Read(reopened));
        Assert.Equal("ALPHA", PdfFormWidgetReader.ReadPage(reopened, 0)
            .Single(field => field.FieldName == "customer").Value);
        Assert.Equal("approval", Assert.Single(PdfSignatureReader.Read(reopened)).FieldName);
        Assert.Single(reopened.CrossReferences.Sections);
    }
}
