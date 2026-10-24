using System.IO;
using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
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
    public void Sign_DefaultOutputSurvivesTemporarySourceCleanup(bool imported)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"killerpdf-sign-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string downloads = Path.Combine(directory, "Downloads");
            Directory.CreateDirectory(downloads);
            string tempRoot = Path.Combine(directory, "sessions");
            using var temps = new KillerPDF.Services.ProcessTempFiles(tempRoot);
            string source = imported ? temps.CreatePath("browser") : Path.Combine(directory, "original.pdf");
            File.WriteAllBytes(source, new PdfDocumentBuilder().AddBlankPage().Build());
            string output = SigningOutputPath.Create(source, temps.Owns(source), downloads, "invoice.pdf");
            using RSA key = RSA.Create(2048);
            using X509Certificate2 certificate = new CertificateRequest("CN=KillerPDF Durable Output Test", key,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            new PdfSigner().Sign(source, output, certificate, new PdfSigner.SignInfo("Approved", "", ""));
            byte[] signed = File.ReadAllBytes(output);

            temps.Dispose();
            using var nextLaunch = new KillerPDF.Services.ProcessTempFiles(tempRoot);
            nextLaunch.CleanupStale();

            Assert.True(File.Exists(output), "Session cleanup deleted the default signed output.");
            Assert.Equal(signed, File.ReadAllBytes(output));
            Assert.Equal(imported ? Path.Combine(downloads, "invoice-signed.pdf")
                : Path.Combine(directory, "original-signed.pdf"), output);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    public void Sign_BindsTheSigningCertificateInTheDeclaredCadesProfile(string culture)
    {
        using RSA key = RSA.Create(2048);
        using X509Certificate2 certificate = new CertificateRequest("CN=KillerPDF Signature Binding Test", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        string directory = Path.Combine(Path.GetTempPath(), $"killerpdf-sign-binding-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            string input = Path.Combine(directory, "input.pdf");
            string output = Path.Combine(directory, "signed.pdf");
            File.WriteAllBytes(input, new PdfDocumentBuilder().AddBlankPage().Build());
            DateTimeOffset started = DateTimeOffset.UtcNow;
            new PdfSigner().Sign(input, output, certificate, new PdfSigner.SignInfo("Approved", "", ""));

            byte[] bytes = File.ReadAllBytes(output);
            PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(PdfDocument.Open(bytes)));
            Assert.Equal("ETSI.CAdES.detached", signature.SubFilter);
            Assert.NotNull(signature.SigningTime);
            Assert.InRange(signature.SigningTime.Value, started.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
            byte[] signedBytes = signature.ByteRange!.Chunk(2).SelectMany(range =>
                bytes.AsSpan(checked((int)range[0]), checked((int)range[1])).ToArray()).ToArray();
            var cms = new SignedCms(new ContentInfo(signedBytes), detached: true);
            cms.Decode(signature.Cms.ToArray());
            cms.CheckSignature(verifySignatureOnly: true);
            SignerInfo signer = Assert.Single(cms.SignerInfos.Cast<SignerInfo>());
            var attributes = signer.SignedAttributes.Cast<CryptographicAttributeObject>().ToArray();
            Assert.Contains(attributes, item => item.Oid.Value == "1.2.840.113549.1.9.3");
            Assert.Contains(attributes, item => item.Oid.Value == "1.2.840.113549.1.9.4");
            CryptographicAttributeObject binding = Assert.Single(attributes,
                item => item.Oid.Value == "1.2.840.113549.1.9.16.2.47");
            AsnEncodedData encoded = Assert.Single(binding.Values.Cast<AsnEncodedData>());
            AsnReader certificateId = new AsnReader(encoded.RawData, AsnEncodingRules.DER)
                .ReadSequence().ReadSequence().ReadSequence();
            if (certificateId.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
                Assert.Equal("2.16.840.1.101.3.4.2.1", certificateId.ReadSequence().ReadObjectIdentifier());
            Assert.Equal(SHA256.HashData(certificate.RawData), certificateId.ReadOctetString());
            Assert.DoesNotContain(attributes, item => item.Oid.Value == "1.2.840.113549.1.9.5");
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            Directory.Delete(directory, recursive: true);
        }
    }

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
