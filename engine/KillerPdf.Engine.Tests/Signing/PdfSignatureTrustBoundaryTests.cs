using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;
using Xunit;

namespace KillerPdf.Engine.Tests.Signing;

public sealed class PdfSignatureTrustBoundaryTests
{
    [Theory]
    [InlineData(2025, false)]
    [InlineData(2028, true)]
    public void UnknownRevocationCannotHideAnUntrustedIssuer(int expiryYear, bool timeValid)
    {
        using RSA rootKey = RSA.Create(2048);
        var rootRequest = new CertificateRequest("CN=Untrusted Fixture Root", rootKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using X509Certificate2 root = rootRequest.CreateSelfSigned(
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        using RSA signerKey = RSA.Create(2048);
        var signerRequest = new CertificateRequest("CN=Untrusted Fixture Signer", signerKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        signerRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        signerRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using X509Certificate2 issued = signerRequest.Create(root,
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(expiryYear, 1, 1, 0, 0, 0, TimeSpan.Zero), [1, 2, 3, 4]);
        using X509Certificate2 signer = issued.CopyWithPrivateKey(signerKey);
        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()), content =>
            {
                var cms = new SignedCms(new ContentInfo(content.ToArray()), detached: true);
                cms.ComputeSignature(new CmsSigner(signer) { IncludeOption = X509IncludeOption.EndCertOnly });
                return cms.Encode();
            }, new PdfSignatureOptions { ReservedSignatureSize = 4096 });
        PdfDocument document = PdfDocument.Open(signed);

        PdfSignatureVerificationResult result = PdfSignatureVerifier.VerifyTrust(document,
            Assert.Single(PdfSignatureReader.Read(document)), new PdfSignatureTrustOptions
            {
                ExtraCertificates = [root],
                VerificationTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                RevocationMode = X509RevocationMode.Offline,
                DisableCertificateDownloads = true,
                UrlRetrievalTimeout = TimeSpan.FromSeconds(1)
            });

        Assert.True(result.IsCryptographicallyValid);
        Assert.Equal(timeValid, result.IsCertificateTimeValid);
        Assert.Equal(PdfCertificateRevocationStatus.Indeterminate, result.RevocationStatus);
        Assert.Equal(PdfCertificateTrustStatus.Untrusted, result.CertificateTrustStatus);
        Assert.Equal(PdfSignatureValidationStatus.Invalid, result.ValidationStatus);
    }
}
