using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;
using KillerPDF.Services.Signing;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfSignerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sign_AlreadySignedDocument_PreservesBothSignaturesAndOriginalRevision(bool occupiedNextName)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=KillerPDF Co-sign Test", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        string directory = Path.Combine(Path.GetTempPath(), $"killerpdf-cosign-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string input = Path.Combine(directory, "input.pdf");
            string first = Path.Combine(directory, "first.pdf");
            string second = Path.Combine(directory, "second.pdf");
            var builder = new PdfDocumentBuilder().AddBlankPage(200, 300);
            if (occupiedNextName) builder.AddCheckBox(0, "Signature2", 10, 10, 20, 20);
            File.WriteAllBytes(input, builder.Build());
            var signer = new PdfSigner();
            var info = new PdfSigner.SignInfo("Approved", "", "");
            signer.Sign(input, first, certificate, info);
            byte[] originalRevision = File.ReadAllBytes(first);

            signer.Sign(first, second, certificate, info);

            Assert.Equal(originalRevision, File.ReadAllBytes(first));
            byte[] cosigned = File.ReadAllBytes(second);
            Assert.True(cosigned.AsSpan().StartsWith(originalRevision));
            PdfDocument document = PdfDocument.Open(cosigned);
            var signatures = PdfSignatureReader.Read(document);
            Assert.Equal(2, signatures.Count);
            Assert.Equal(2, signatures.Select(signature => signature.FieldName).Distinct().Count());
            Assert.All(signatures, signature =>
                Assert.True(PdfSignatureVerifier.VerifyIntegrity(document, signature).IsCryptographicallyValid));
            if (occupiedNextName)
                Assert.Contains(PdfFormWidgetReader.ReadPage(document, 0),
                    field => field.FieldName == "Signature2" && field.FieldKind == PdfFormFieldKind.Button);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // A visible signature lands where the signer placed it, in a new field, instead of being
    // redirected into a prepared field on another page; an invisible signature fills the prepared
    // field, and a prepared field whose seed values the signature cannot meet falls back to a new one.
    [Theory]
    [InlineData("placed", "Signature1")]
    [InlineData("invisible", "approval")]
    [InlineData("seed", "Signature1")]
    public void Sign_ChoosesThePreparedFieldOnlyWhenItFitsTheSignature(string kind, string expectedField)
    {
        using RSA key = RSA.Create(2048);
        using X509Certificate2 certificate = new CertificateRequest("CN=KillerPDF Field Choice Test", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        string directory = Path.Combine(Path.GetTempPath(), $"killerpdf-sign-field-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string input = Path.Combine(directory, "input.pdf");
        string output = Path.Combine(directory, "signed.pdf");
        try
        {
            File.WriteAllBytes(input, new PdfDocumentBuilder().AddBlankPage(200, 300).AddBlankPage(200, 300)
                .AddSignatureField(1, "approval", 10, 10, 100, 30,
                    seedValue: kind == "seed" ? new PdfSignatureSeedValue { Reasons = ["Reviewed"], RequireReason = true } : null)
                .Build());
            var info = new PdfSigner.SignInfo("Approved", "Seattle", "steve@example.test",
                kind == "placed" ? new PdfSigner.VisibleSignatureInfo(0, 20, 20, 120, 40, 10, "Signed") : null);

            new PdfSigner().Sign(input, output, certificate, info);

            PdfDocument signed = PdfDocument.Open(File.ReadAllBytes(output));
            Assert.Equal(expectedField, Assert.Single(PdfSignatureReader.Read(signed), item => item.IsSigned).FieldName);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    // Falling back to a new field is for a prepared field that refuses this signature before
    // anything is signed. A failure after the signature was made is not retried: the retry signed
    // again (a second token PIN prompt and timestamp request) and replaced the first reason.
    [Fact]
    public void FailureAfterSigningIsNotRetriedInANewField()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(200, 300)
            .AddSignatureField(0, "approval", 10, 10, 100, 30).Build());
        int signatures = 0;

        var error = Assert.Throws<InvalidOperationException>(() => PdfSigner.SignWithFallback(document,
            _ => { signatures++; return new byte[1 << 20]; },
            new PdfSignatureOptions { FieldName = "approval" }, newField: "Signature1"));

        Assert.Equal(1, signatures);
        Assert.Contains("reserved", error.Message);
    }

    // When the new field fails as well, the prepared field's own reason is the one the signer can
    // act on, so it is the error reported.
    [Fact]
    public void FailedFallbackReportsThePreparedFieldsReason()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(200, 300)
            .AddSignatureField(0, "approval", 10, 10, 100, 30,
                seedValue: new PdfSignatureSeedValue { Reasons = ["Reviewed"], RequireReason = true })
            .Build());
        var options = new PdfSignatureOptions { FieldName = "approval", Reason = "Approved" };
        string refusal = Assert.Throws<InvalidOperationException>(() =>
            PdfDetachedSignatureWriter.Sign(document, _ => new byte[16], options)).Message;
        int signatures = 0;

        var error = Assert.Throws<InvalidOperationException>(() => PdfSigner.SignWithFallback(document,
            _ => { signatures++; return new byte[1 << 20]; }, options, newField: "Signature1"));

        Assert.Equal(1, signatures);
        Assert.Equal(refusal, error.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sign_WritesVerifiableEngineSignatureAndMetadata(bool existingUnsignedField)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=KillerPDF Application Signing Test",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        string directory = Path.Combine(Path.GetTempPath(), $"killerpdf-sign-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string input = Path.Combine(directory, "input.pdf");
        string output = Path.Combine(directory, "signed.pdf");

        try
        {
            var builder = new PdfDocumentBuilder().AddBlankPage(200, 300);
            if (existingUnsignedField) builder.AddSignatureField(0, "Signature1", 10, 10, 100, 30);
            File.WriteAllBytes(input, builder.Build());
            new PdfSigner().Sign(
                input,
                output,
                certificate,
                new PdfSigner.SignInfo("Approved", "Seattle", "steve@example.test"));

            PdfDocument signed = PdfDocument.Open(File.ReadAllBytes(output));
            PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(signed));
            Assert.True(signature.IsSigned);
            Assert.Equal("Signature1", signature.FieldName);
            Assert.Equal("ETSI.CAdES.detached", signature.SubFilter);
            Assert.NotEmpty(signature.Cms.ToArray());
            Assert.True(PdfSignatureVerifier.VerifyIntegrity(signed, signature).IsCryptographicallyValid);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
