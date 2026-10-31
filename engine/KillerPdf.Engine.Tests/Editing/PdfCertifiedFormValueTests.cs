using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Signing;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfCertifiedFormValueTests
{
    [Theory]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void PermittedFieldFillingPreservesTheGenuineCertificationAndSignedRevision(int permission, bool unsignedApproval)
    {
        byte[] signed = Certified(permission, unsignedApproval: unsignedApproval);
        PdfDocument document = PdfDocument.Open(signed);
        Verify(signed);

        byte[] filled = new PdfIncrementalPageEditor(document).SetTextFieldValue("customer.name", "Updated").Build();

        Assert.True(filled.AsSpan().StartsWith(signed));
        PdfDocument reopened = PdfDocument.Open(filled);
        Assert.Equal("Updated", PdfFormWidgetReader.ReadPage(reopened, 0).Single(field => field.FieldName == "customer.name").Value);
        Assert.Equal((PdfSignatureCertificationPermission)permission, PdfSignatureReader.ReadCertificationPermission(reopened));
        Verify(filled);
        string? output = Environment.GetEnvironmentVariable("KILLERPDF_TEST_CERTIFIED_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            string fixture = Path.Combine(output, $"certified-p{permission}-{(unsignedApproval ? "unsigned-" : "")}{Guid.NewGuid():N}");
            File.WriteAllBytes(fixture + ".pdf", signed);
            File.WriteAllBytes(fixture + "-filled.pdf", filled);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CertificationKeepsStructuralAndAppearanceChangesProtected(int permission)
    {
        byte[] signed = Certified(permission);
        PdfDocument document = PdfDocument.Open(signed);
        if (permission == 1)
            Assert.Throws<InvalidOperationException>(() => new PdfIncrementalPageEditor(document)
                .SetTextFieldValue("customer.name", "Changed").Build());
        Assert.Throws<InvalidOperationException>(() => new PdfIncrementalPageEditor(document).RotateClockwise(0).Build());
        Assert.Throws<InvalidOperationException>(() => new PdfIncrementalPageEditor(document)
            .SetTextFieldBackgroundColor("customer.name", "Changed", new PdfRgbColor(1, 0, 0)).Build());
        Verify(signed);
    }

    [Theory]
    [InlineData(PdfSignatureLockAction.Include, "customer.name", "customer.name", false)]
    [InlineData(PdfSignatureLockAction.Include, "customer.name", "customer.other", true)]
    [InlineData(PdfSignatureLockAction.Exclude, "customer.name", "customer.name", true)]
    [InlineData(PdfSignatureLockAction.Exclude, "customer.name", "customer.other", false)]
    [InlineData(PdfSignatureLockAction.All, "customer.name", "customer.other", false)]
    public void GenuineFieldMdpLocksSelectThePermittedLogicalFields(PdfSignatureLockAction action,
        string selected, string field, bool allowed)
    {
        foreach (int permission in new[] { 2, 3 })
        {
            byte[] signed = Certified(permission, new PdfSignatureFieldLock(action,
                action == PdfSignatureLockAction.All ? null : [selected]));
            var editor = new PdfIncrementalPageEditor(PdfDocument.Open(signed)).SetTextFieldValue(field, "Changed");
            if (allowed)
            {
                byte[] filled = editor.Build();
                Assert.True(filled.AsSpan().StartsWith(signed));
                Verify(filled);
            }
            else Assert.Throws<InvalidOperationException>(() => editor.Build());
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void CertifiedReadOnlyFieldsStayProtected(int permission)
    {
        byte[] signed = Certified(permission, readOnly: true);
        Assert.Throws<InvalidOperationException>(() => new PdfIncrementalPageEditor(PdfDocument.Open(signed))
            .SetTextFieldValue("customer.name", "Changed").Build());
        Verify(signed);
    }

    private static byte[] Certified(int permission, PdfSignatureFieldLock? fieldLock = null, bool readOnly = false, bool unsignedApproval = false)
    {
        using RSA key = RSA.Create(2048);
        using X509Certificate2 certificate = new CertificateRequest("CN=Certified Form Fixture", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var builder = new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "customer.name", 20, 100, 150, 24, "Original",
                options: new PdfTextFieldOptions { ReadOnly = readOnly })
            .AddTextField(0, "customer.other", 20, 150, 150, 24, "Other")
            .AddSignatureField(0, "certification", 20, 20, 150, 40, fieldLock: fieldLock);
        if (unsignedApproval) builder.AddSignatureField(0, "approval", 200, 20, 150, 40);
        byte[] source = builder.Build();
        return PdfDetachedSignatureWriter.Sign(PdfDocument.Open(source), content =>
        {
            var cms = new SignedCms(new ContentInfo(content.ToArray()), detached: true);
            cms.ComputeSignature(new CmsSigner(certificate)
            {
                IncludeOption = X509IncludeOption.EndCertOnly,
                DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1")
            });
            return cms.Encode();
        }, new PdfSignatureOptions
        {
            FieldName = "certification", ReservedSignatureSize = 4096,
            CertificationPermission = (PdfSignatureCertificationPermission)permission
        });
    }

    private static void Verify(byte[] source)
    {
        PdfDocument document = PdfDocument.Open(source);
        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(document), signature => signature.IsSigned);
        var result = PdfSignatureVerifier.VerifyIntegrity(document, signature);
        Assert.True(result.IsStructurallyValid);
        Assert.True(result.IsCryptographicallyValid);
        Assert.True(signature.HasValidByteRange);
        Assert.True(signature.HasValidCmsEncoding);
        Assert.NotNull(signature.ByteRange);
        Assert.Equal(4, signature.ByteRange.Count);
        using var signedBytes = new MemoryStream();
        for (int index = 0; index < 4; index += 2)
            signedBytes.Write(source.AsSpan(checked((int)signature.ByteRange[index]), checked((int)signature.ByteRange[index + 1])));
        var independent = new SignedCms(new ContentInfo(signedBytes.ToArray()), detached: true);
        independent.Decode(signature.Cms.ToArray());
        independent.CheckSignature(verifySignatureOnly: true);
    }
}
