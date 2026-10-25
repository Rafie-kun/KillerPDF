using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;
using Xunit;

namespace KillerPdf.Engine.Tests.Signing;

public sealed class PdfSignatureHistoricalTrustTests : IDisposable
{
    private static readonly DateTimeOffset Timestamp = new(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTime VerificationTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly RSA _rootKey = RSA.Create(2048);
    private readonly RSA _signerKey = RSA.Create(2048);
    private readonly RSA _authorityKey = RSA.Create(2048);
    private readonly X509Certificate2 _root;
    private readonly X509Certificate2 _signer;
    private readonly X509Certificate2 _authority;

    public PdfSignatureHistoricalTrustTests()
    {
        var rootRequest = new CertificateRequest("CN=Historical Trust Fixture Root", _rootKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        _root = rootRequest.CreateSelfSigned(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero));
        _signer = Issue(_signerKey, "CN=Expired Historical Fixture Signer", 2024, 2025, timestampAuthority: false);
        _authority = Issue(_authorityKey, "CN=Historical Fixture Authority", 2023, 2030, timestampAuthority: true);
    }

    [Fact]
    public void TrustedSignatureTimestampCanEstablishHistoricalCertificateTimeWithoutClaimingRevocation()
    {
        byte[] signed = PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()), content =>
            {
                var cms = new SignedCms(new ContentInfo(content.ToArray()), detached: true);
                cms.ComputeSignature(new CmsSigner(_signer)
                {
                    IncludeOption = X509IncludeOption.EndCertOnly,
                    DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1")
                });
                return PdfTimestampAuthority.AddTimestamp(cms.Encode(), Authority);
            }, new PdfSignatureOptions { ReservedSignatureSize = 16_384 });
        PdfDocument document = PdfDocument.Open(signed);
        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(document));
        Assert.True(PdfSignatureVerifier.VerifyIntegrity(document, signature).IsCryptographicallyValid);

        PdfSignatureVerificationResult result = PdfSignatureVerifier.VerifyTrust(document, signature,
            new PdfSignatureTrustOptions
            {
                CustomTrustRoots = [_root],
                VerificationTime = VerificationTime,
                UseTrustedSignatureTimestamp = true,
                RevocationMode = X509RevocationMode.NoCheck,
                DisableCertificateDownloads = true
            });

        Assert.True(result.IsCryptographicallyValid);
        Assert.True(result.IsCertificateTrusted);
        Assert.True(result.IsCertificateTimeValid);
        Assert.Equal(PdfCertificateTimeStatus.Valid, result.CertificateTimeStatus);
        Assert.Equal(VerificationTime, result.RequestedVerificationTime);
        Assert.Equal(PdfCertificateRevocationStatus.NotChecked, result.RevocationStatus);
        Assert.Equal(PdfSignatureValidationStatus.Incomplete, result.ValidationStatus);
        Assert.Equal(Timestamp.UtcDateTime, result.EvaluatedVerificationTime);
        Assert.Equal(Timestamp, result.TrustedTimestampTime);
        Assert.Equal(PdfSignatureTimestampStatus.Trusted, result.SignatureTimestampStatus);
        Assert.Equal(PdfCertificateRevocationStatus.NotChecked, result.TimestampRevocationStatus);
    }

    [Theory]
    [InlineData("wrong-imprint")]
    [InlineData("malformed")]
    [InlineData("multiple-values")]
    [InlineData("future")]
    [InlineData("before-signer-validity")]
    [InlineData("after-signer-expiry")]
    [InlineData("accuracy-before-validity")]
    [InlineData("accuracy-after-validity")]
    public void RejectedTimestampCannotRescueExpiryOrInvalidateCmsIntegrity(string kind)
    {
        byte[] signed = SignedPdfWithToken(primary => kind switch
        {
            "malformed" => [0x30, 0x00],
            "multiple-values" => Token(primary, Timestamp),
            "wrong-imprint" => Token(primary, Timestamp, wrongImprint: true),
            "future" => Token(primary, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero)),
            "before-signer-validity" => Token(primary, new DateTimeOffset(2023, 6, 1, 0, 0, 0, TimeSpan.Zero)),
            "after-signer-expiry" => Token(primary, new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero)),
            "accuracy-before-validity" => Token(primary, new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), accuracy: 1),
            "accuracy-after-validity" => Token(primary, new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), accuracy: 1),
            _ => throw new ArgumentException("Unknown timestamp fixture.")
        }, additionalValue: kind == "multiple-values");
        PdfDocument document = PdfDocument.Open(signed);
        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(document));

        PdfSignatureVerificationResult result = PdfSignatureVerifier.VerifyTrust(document, signature, Policy());

        Assert.True(PdfSignatureVerifier.VerifyIntegrity(document, signature).IsCryptographicallyValid);
        Assert.True(result.IsCryptographicallyValid);
        Assert.True(result.IsCertificateTrusted);
        Assert.False(result.IsCertificateTimeValid);
        Assert.Null(result.TrustedTimestampTime);
        Assert.Equal(PdfSignatureTimestampStatus.Rejected, result.SignatureTimestampStatus);
        Assert.NotNull(result.TimestampError);
        Assert.Equal(VerificationTime, result.EvaluatedVerificationTime);
        Assert.Equal(PdfSignatureValidationStatus.Incomplete, result.ValidationStatus);
    }

    [Fact]
    public void TimestampRecoveryDoesNotOverrideAnExplicitlyDisabledPolicy()
    {
        PdfDocument document = PdfDocument.Open(SignedPdfWithToken(primary => Token(primary, Timestamp)));
        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(document));

        PdfSignatureVerificationResult result = PdfSignatureVerifier.VerifyTrust(document, signature,
            Policy() with { UseTrustedSignatureTimestamp = false });

        Assert.True(result.IsCryptographicallyValid);
        Assert.False(result.IsCertificateTimeValid);
        Assert.Null(result.TrustedTimestampTime);
        Assert.Equal(PdfSignatureTimestampStatus.NotChecked, result.SignatureTimestampStatus);
        Assert.Equal(VerificationTime, result.RequestedVerificationTime);
        Assert.Equal(VerificationTime, result.EvaluatedVerificationTime);
        Assert.Equal(PdfSignatureValidationStatus.Incomplete, result.ValidationStatus);
    }

    [Fact]
    public void HistoricalEvaluationIsDisclosedInTextAndJson()
    {
        PdfDocument document = PdfDocument.Open(SignedPdfWithToken(primary => Token(primary, Timestamp)));

        PdfSignatureInspectionReport report = PdfSignatureInspection.Inspect(document, Policy());

        string text = report.ToText();
        Assert.Contains("Requested verification time: " + VerificationTime.ToString("O"), text);
        Assert.Contains("Evaluated verification time: " + Timestamp.UtcDateTime.ToString("O"), text);
        Assert.Contains("Trusted signature timestamp: " + Timestamp.ToString("O"), text);
        Assert.Contains("Timestamp authority revocation: NotChecked", text);
        string authorityFingerprint = _authority.GetCertHashString(HashAlgorithmName.SHA256);
        Assert.Contains("Timestamp authority subject: " + _authority.Subject, text);
        Assert.Contains("Timestamp authority certificate SHA-256: " + authorityFingerprint, text);
        using System.Text.Json.JsonDocument json = System.Text.Json.JsonDocument.Parse(report.ToJson());
        System.Text.Json.JsonElement verification = json.RootElement.GetProperty("signatures")[0].GetProperty("verification");
        Assert.Equal(VerificationTime, verification.GetProperty("requestedVerificationTime").GetDateTime());
        Assert.Equal(Timestamp.UtcDateTime, verification.GetProperty("evaluatedVerificationTime").GetDateTime());
        Assert.Equal(Timestamp, verification.GetProperty("trustedTimestampTime").GetDateTimeOffset());
        Assert.Equal(_authority.Subject, verification.GetProperty("timestampAuthoritySubject").GetString());
        Assert.Equal(authorityFingerprint, verification.GetProperty("timestampAuthorityCertificateSha256").GetString());
    }

    [Theory]
    [InlineData("missing-usage")]
    [InlineData("noncritical-usage")]
    [InlineData("multiple-usages")]
    [InlineData("not-valid-at-time")]
    public void TimestampAuthorityMustHaveValidExclusiveCriticalUsageAtItsTimestamp(string kind)
    {
        using X509Certificate2 authority = Issue(_authorityKey, "CN=Restricted Fixture Authority",
            kind == "not-valid-at-time" ? 2025 : 2023, 2030,
            timestampAuthority: kind != "missing-usage", criticalUsage: kind != "noncritical-usage",
            multipleUsages: kind == "multiple-usages");
        PdfDocument document = PdfDocument.Open(SignedPdfWithToken(primary => Token(primary, Timestamp, authority: authority)));
        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(document));

        PdfSignatureVerificationResult result = PdfSignatureVerifier.VerifyTrust(document, signature, Policy());

        Assert.True(result.IsCryptographicallyValid);
        Assert.False(result.IsCertificateTimeValid);
        Assert.Null(result.TrustedTimestampTime);
        Assert.Equal(PdfSignatureTimestampStatus.Rejected, result.SignatureTimestampStatus);
        Assert.Equal(PdfSignatureValidationStatus.Incomplete, result.ValidationStatus);
    }

    [Fact]
    public void EmbeddedTimestampAuthorityIsNotAnImplicitTrustAnchor()
    {
        var request = new CertificateRequest("CN=Untrusted Timestamp Fixture", _authorityKey,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.8")], true));
        using X509Certificate2 foreign = request.CreateSelfSigned(
            new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        PdfDocument document = PdfDocument.Open(SignedPdfWithToken(primary => Token(primary, Timestamp, authority: foreign)));

        PdfSignatureVerificationResult result = PdfSignatureVerifier.VerifyTrust(document,
            Assert.Single(PdfSignatureReader.Read(document)), Policy());

        Assert.True(result.IsCryptographicallyValid);
        Assert.True(result.IsCertificateTrusted);
        Assert.False(result.IsCertificateTimeValid);
        Assert.Null(result.TrustedTimestampTime);
        Assert.Equal(PdfSignatureTimestampStatus.Rejected, result.SignatureTimestampStatus);
        Assert.Equal(PdfSignatureValidationStatus.Incomplete, result.ValidationStatus);
    }

    [Fact]
    public void OfflinePolicyDoesNotInventTimestampRevocationEvidence()
    {
        PdfDocument document = PdfDocument.Open(SignedPdfWithToken(primary => Token(primary, Timestamp)));

        PdfSignatureVerificationResult result = PdfSignatureVerifier.VerifyTrust(document,
            Assert.Single(PdfSignatureReader.Read(document)), Policy() with { RevocationMode = X509RevocationMode.Offline });

        Assert.True(result.IsCryptographicallyValid);
        Assert.True(result.IsCertificateTrusted);
        Assert.False(result.IsCertificateTimeValid);
        Assert.Equal(PdfCertificateRevocationStatus.Indeterminate, result.RevocationStatus);
        Assert.Equal(PdfCertificateRevocationStatus.Indeterminate, result.TimestampRevocationStatus);
        Assert.Equal(PdfSignatureTimestampStatus.Indeterminate, result.SignatureTimestampStatus);
        Assert.Null(result.TrustedTimestampTime);
        Assert.Equal(PdfSignatureValidationStatus.Incomplete, result.ValidationStatus);
    }

    [Fact]
    public void IntegrityOnlyInspectionDoesNotEvaluateTimestampTrust()
    {
        PdfDocument document = PdfDocument.Open(SignedPdfWithToken(primary => Token(primary, Timestamp)));

        PdfSignatureVerificationResult result = PdfSignatureVerifier.VerifyIntegrity(document,
            Assert.Single(PdfSignatureReader.Read(document)));

        Assert.True(result.IsCryptographicallyValid);
        Assert.False(result.CertificateTrustWasChecked);
        Assert.Equal(PdfSignatureTimestampStatus.NotChecked, result.SignatureTimestampStatus);
        Assert.Null(result.EvaluatedVerificationTime);
        Assert.Null(result.TrustedTimestampTime);
    }

    [Fact]
    public void MissingTimestampDoesNotGuessAHistoricalDate()
    {
        PdfDocument document = PdfDocument.Open(SignedPdfWithToken(null));

        PdfSignatureVerificationResult result = PdfSignatureVerifier.VerifyTrust(document,
            Assert.Single(PdfSignatureReader.Read(document)), Policy());

        Assert.True(result.IsCryptographicallyValid);
        Assert.False(result.IsCertificateTimeValid);
        Assert.Equal(PdfSignatureTimestampStatus.Missing, result.SignatureTimestampStatus);
        Assert.Null(result.TrustedTimestampTime);
        Assert.Equal(VerificationTime, result.EvaluatedVerificationTime);
        Assert.Equal(PdfSignatureValidationStatus.Incomplete, result.ValidationStatus);
    }

    [Fact]
    public void TimestampDoesNotRepairChangedSignedDocumentBytes()
    {
        byte[] signed = SignedPdfWithToken(primary => Token(primary, Timestamp));
        int bounds = signed.AsSpan().IndexOf("/MediaBox [0 0 612 792]"u8);
        Assert.True(bounds >= 0);
        signed[bounds + "/MediaBox [0 0 ".Length] = (byte)'5';
        PdfDocument document = PdfDocument.Open(signed);

        PdfSignatureVerificationResult result = PdfSignatureVerifier.VerifyTrust(document,
            Assert.Single(PdfSignatureReader.Read(document)), Policy());

        Assert.False(result.IsCryptographicallyValid);
        Assert.Null(result.TrustedTimestampTime);
        Assert.Equal(PdfSignatureValidationStatus.Invalid, result.ValidationStatus);
    }

    [Fact]
    public void AdditionalAnchorsCanEstablishBothSignerAndTimestampAuthorityTrust()
    {
        PdfDocument document = PdfDocument.Open(SignedPdfWithToken(primary => Token(primary, Timestamp)));

        PdfSignatureVerificationResult result = PdfSignatureVerifier.VerifyTrust(document,
            Assert.Single(PdfSignatureReader.Read(document)), Policy() with
            {
                CustomTrustRoots = [],
                AdditionalTrustRoots = [_root]
            });

        Assert.True(result.IsCryptographicallyValid);
        Assert.True(result.IsCertificateTrusted);
        Assert.True(result.IsCertificateTimeValid);
        Assert.Equal(Timestamp, result.TrustedTimestampTime);
        Assert.Equal(PdfSignatureValidationStatus.Incomplete, result.ValidationStatus);
    }

    [Fact]
    public void ExclusiveCustomAnchorsDoNotFallBackToAdditionalAnchors()
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Exclusive Unrelated Fixture Root", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using X509Certificate2 unrelated = request.CreateSelfSigned(
            new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2035, 1, 1, 0, 0, 0, TimeSpan.Zero));
        PdfDocument document = PdfDocument.Open(SignedPdfWithToken(primary => Token(primary, Timestamp)));

        PdfSignatureVerificationResult result = PdfSignatureVerifier.VerifyTrust(document,
            Assert.Single(PdfSignatureReader.Read(document)), Policy() with
            {
                CustomTrustRoots = [unrelated],
                AdditionalTrustRoots = [_root]
            });

        Assert.True(result.IsCryptographicallyValid);
        Assert.False(result.IsCertificateTrusted);
        Assert.Null(result.TrustedTimestampTime);
        Assert.Equal(PdfSignatureValidationStatus.Invalid, result.ValidationStatus);
    }

    private PdfSignatureTrustOptions Policy() => new()
    {
        CustomTrustRoots = [_root],
        VerificationTime = VerificationTime,
        UseTrustedSignatureTimestamp = true,
        RevocationMode = X509RevocationMode.NoCheck,
        DisableCertificateDownloads = true
    };

    private byte[] SignedPdfWithToken(Func<SignerInfo, byte[]>? timestamp, bool additionalValue = false)
    {
        return PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()), content =>
            {
                var cms = new SignedCms(new ContentInfo(content.ToArray()), detached: true);
                cms.ComputeSignature(new CmsSigner(_signer)
                {
                    IncludeOption = X509IncludeOption.EndCertOnly,
                    DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1")
                });
                SignerInfo primary = cms.SignerInfos[0];
                if (timestamp is not null)
                    primary.AddUnsignedAttribute(new AsnEncodedData(PdfTimestampAuthority.SignatureTimestampTokenOid,
                        timestamp(primary)));
                if (additionalValue)
                    primary.AddUnsignedAttribute(new AsnEncodedData(PdfTimestampAuthority.SignatureTimestampTokenOid,
                        [0x05, 0x00]));
                return cms.Encode();
            }, new PdfSignatureOptions { ReservedSignatureSize = 16_384 });
    }

    private byte[] Token(SignerInfo primary, DateTimeOffset time, bool wrongImprint = false, long? accuracy = null,
        X509Certificate2? authority = null)
    {
        authority ??= _authority;
        Rfc3161TimestampRequest request = Rfc3161TimestampRequest.CreateFromSignerInfo(primary,
            HashAlgorithmName.SHA256, requestSignerCertificates: true);
        byte[] imprint = request.GetMessageHash().ToArray();
        if (wrongImprint) imprint[0] ^= 1;
        var info = new Rfc3161TimestampTokenInfo(new Oid("1.3.6.1.4.1.32473.1"),
            request.HashAlgorithmId, imprint, "*"u8.ToArray(), time, accuracyInMicroseconds: accuracy);
        var token = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, authority)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
            IncludeOption = X509IncludeOption.EndCertOnly
        };
        var certificateId = new AsnWriter(AsnEncodingRules.DER);
        certificateId.PushSequence();
        certificateId.PushSequence();
        certificateId.PushSequence();
        certificateId.WriteOctetString(SHA256.HashData(authority.RawData));
        certificateId.PopSequence();
        certificateId.PopSequence();
        certificateId.PopSequence();
        signer.SignedAttributes.Add(new AsnEncodedData("1.2.840.113549.1.9.16.2.47", certificateId.Encode()));
        token.ComputeSignature(signer, silent: true);
        return token.Encode();
    }

    private X509Certificate2 Issue(RSA key, string subject, int firstYear, int lastYear, bool timestampAuthority,
        bool criticalUsage = true, bool multipleUsages = false)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        if (timestampAuthority)
        {
            var usages = new OidCollection { new Oid("1.3.6.1.5.5.7.3.8") };
            if (multipleUsages) usages.Add(new Oid("1.3.6.1.5.5.7.3.3"));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                usages, criticalUsage));
        }
        using X509Certificate2 issued = request.Create(_root,
            new DateTimeOffset(firstYear, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(lastYear, 1, 1, 0, 0, 0, TimeSpan.Zero), RandomNumberGenerator.GetBytes(16));
        return issued.CopyWithPrivateKey(key);
    }

    private byte[] Authority(ReadOnlyMemory<byte> requestBytes)
    {
        Assert.True(Rfc3161TimestampRequest.TryDecode(requestBytes, out Rfc3161TimestampRequest? request, out _));
        var info = new Rfc3161TimestampTokenInfo(new Oid("1.3.6.1.4.1.32473.1"),
            request!.HashAlgorithmId, request.GetMessageHash(), "*"u8.ToArray(), Timestamp,
            nonce: request.GetNonce());
        var token = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, _authority)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
            IncludeOption = X509IncludeOption.EndCertOnly
        };
        var certificateId = new AsnWriter(AsnEncodingRules.DER);
        certificateId.PushSequence();
        certificateId.PushSequence();
        certificateId.PushSequence();
        certificateId.WriteOctetString(SHA256.HashData(_authority.RawData));
        certificateId.PopSequence();
        certificateId.PopSequence();
        certificateId.PopSequence();
        signer.SignedAttributes.Add(new AsnEncodedData("1.2.840.113549.1.9.16.2.47", certificateId.Encode()));
        token.ComputeSignature(signer, silent: true);
        var response = new AsnWriter(AsnEncodingRules.DER);
        response.PushSequence();
        response.PushSequence();
        response.WriteInteger(0);
        response.PopSequence();
        response.WriteEncodedValue(token.Encode());
        response.PopSequence();
        return response.Encode();
    }

    public void Dispose()
    {
        _authority.Dispose();
        _signer.Dispose();
        _root.Dispose();
        _authorityKey.Dispose();
        _signerKey.Dispose();
        _rootKey.Dispose();
    }
}
