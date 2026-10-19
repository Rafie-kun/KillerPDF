using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;
using Xunit;

namespace KillerPdf.Engine.Tests.Signing;

public sealed class PdfEmbeddedCertificateTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void EmbeddedIntermediateCompletesOnlyAnExplicitlyTrustedChain(
        bool includeIntermediate, bool trustRoot, bool expectedTrusted)
    {
        string id = Guid.NewGuid().ToString("N");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using RSA rootKey = RSA.Create(2048);
        using RSA intermediateKey = RSA.Create(2048);
        using RSA leafKey = RSA.Create(2048);
        using X509Certificate2 root = Request($"CN=Root-{id}", rootKey, true)
            .CreateSelfSigned(now.AddDays(-2), now.AddDays(2));
        using X509Certificate2 publicIntermediate = Request($"CN=Intermediate-{id}", intermediateKey, true)
            .Create(root, now.AddDays(-1), now.AddDays(1), RandomNumberGenerator.GetBytes(16));
        using X509Certificate2 intermediate = publicIntermediate.CopyWithPrivateKey(intermediateKey);
        using X509Certificate2 publicLeaf = Request($"CN=Signer-{id}", leafKey, false)
            .Create(intermediate, now.AddHours(-1), now.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using X509Certificate2 leaf = publicLeaf.CopyWithPrivateKey(leafKey);

        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()), content =>
            {
                var cms = new SignedCms(new ContentInfo(content.ToArray()), detached: true);
                var signer = new CmsSigner(leaf) { IncludeOption = X509IncludeOption.EndCertOnly };
                signer.Certificates.Add(root);
                if (includeIntermediate) signer.Certificates.Add(intermediate);
                cms.ComputeSignature(signer);
                return cms.Encode();
            }, new PdfSignatureOptions { ReservedSignatureSize = 8192 });
        PdfDocument document = PdfDocument.Open(signed);
        PdfSignatureVerificationResult result = PdfSignatureVerifier.VerifyTrust(document,
            Assert.Single(PdfSignatureReader.Read(document)), new PdfSignatureTrustOptions
            {
                CustomTrustRoots = trustRoot ? [root] : [],
                DisableCertificateDownloads = true,
                RevocationMode = X509RevocationMode.NoCheck,
                VerificationTime = now.UtcDateTime
            });

        Assert.True(result.IsCryptographicallyValid);
        Assert.Equal(expectedTrusted, result.IsCertificateTrusted);
        if (expectedTrusted)
        {
            Assert.Equal(3, result.CertificateChain.Count);
            Assert.Equal(intermediate.Subject, result.CertificateChain[1].Subject);
        }
    }

    private static CertificateRequest Request(string subject, RSA key, bool authority)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(authority
            ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request;
    }
}
