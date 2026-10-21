using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Documents;

namespace KillerPdf.Engine.Signing;

/// <summary>Verifies detached CMS signatures over bytes reconstructed from PDF byte ranges.</summary>
public static class PdfSignatureVerifier
{
    /// <summary>Verifies signature structure and cryptographic integrity without checking trust.</summary>
    public static PdfSignatureVerificationResult VerifyIntegrity(
        PdfDocument document, PdfSignatureInfo signature) =>
        Verify(document, signature, checkCertificateTrust: false);

    /// <summary>Verifies signature integrity and evaluates the signing certificate's system trust.</summary>
    public static PdfSignatureVerificationResult VerifyTrust(
        PdfDocument document, PdfSignatureInfo signature) =>
        VerifyTrust(document, signature, new PdfSignatureTrustOptions());

    /// <summary>Verifies signature integrity using a configurable certificate trust policy.</summary>
    public static PdfSignatureVerificationResult VerifyTrust(
        PdfDocument document, PdfSignatureInfo signature, PdfSignatureTrustOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return Verify(document, signature, checkCertificateTrust: true, options);
    }

    private static PdfSignatureVerificationResult Verify(
        PdfDocument document, PdfSignatureInfo signature, bool checkCertificateTrust,
        PdfSignatureTrustOptions? trustOptions = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(signature);
        bool structurallyValid = signature.IsSigned
            && signature.HasValidByteRange
            && signature.HasValidCmsEncoding;
        if (!structurallyValid)
            return new PdfSignatureVerificationResult
            {
                IsStructurallyValid = false,
                CertificateTrustWasChecked = checkCertificateTrust,
                RequestedRevocationMode = trustOptions?.RevocationMode,
                RequestedVerificationTime = trustOptions?.VerificationTime,
                CertificateDownloadsDisabled = trustOptions?.DisableCertificateDownloads,
                Error = "The signature does not contain a valid byte range and CMS value."
            };
        X509Certificate2? signer = null;
        string? digestAlgorithm = null;
        string? signatureAlgorithm = null;
        IReadOnlyList<string> signaturePolicies = [];
        bool policyImplied = false;
        string? policyError = null;
        try
        {
            byte[] content = PdfSignatureReader.GetSignedContent(document, signature);
            SignedCms cms;
            if (signature.IsDocumentTimestamp)
            {
                if (!Rfc3161TimestampToken.TryDecode(
                        signature.Cms, out Rfc3161TimestampToken? token, out int consumed)
                    || token is null || consumed != signature.Cms.Length)
                    throw new CryptographicException(
                        "The RFC 3161 document timestamp token is invalid.");
                if (!token.VerifySignatureForData(content, out signer, extraCandidates: null))
                    throw new CryptographicException(
                        "The RFC 3161 document timestamp does not match the signed PDF bytes.");
                cms = token.AsSignedCms();
            }
            else
            {
                cms = new SignedCms(new ContentInfo(content), detached: true);
                cms.Decode(signature.Cms.Span);
                cms.CheckSignature(verifySignatureOnly: true);
            }
            SignerInfo? signerInfo = cms.SignerInfos.Count > 0 ? cms.SignerInfos[0] : null;
            signer ??= signerInfo?.Certificate;
            digestAlgorithm = signerInfo?.DigestAlgorithm.Value;
            signatureAlgorithm = signerInfo?.SignatureAlgorithm.Value;
            if (signerInfo is not null)
                (signaturePolicies, policyImplied, policyError) = SignaturePolicies(signerInfo);
            if (checkCertificateTrust)
            {
                if (signer is null)
                    return TrustFailure("The CMS signature does not contain a signing certificate.",
                        PdfCertificateTrustStatus.Indeterminate, null,
                        PdfCertificateRevocationStatus.Indeterminate, []);
                PdfSignatureTrustOptions policy = trustOptions ?? new();
                using var chain = new X509Chain();
                chain.ChainPolicy.RevocationMode = policy.RevocationMode;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.VerificationTime = policy.VerificationTime;
                chain.ChainPolicy.UrlRetrievalTimeout = policy.UrlRetrievalTimeout;
                chain.ChainPolicy.DisableCertificateDownloads =
                    policy.DisableCertificateDownloads;
                // Embedded certificates are chain candidates, never trust anchors.
                chain.ChainPolicy.ExtraStore.AddRange(cms.Certificates);
                foreach (X509Certificate2 certificate in policy.ExtraCertificates)
                    chain.ChainPolicy.ExtraStore.Add(certificate);
                if (policy.CustomTrustRoots.Count > 0)
                {
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    foreach (X509Certificate2 certificate in policy.CustomTrustRoots)
                        chain.ChainPolicy.CustomTrustStore.Add(certificate);
                }
                bool trusted = chain.Build(signer);
                X509ChainStatusFlags flags = chain.ChainStatus.Aggregate(
                    X509ChainStatusFlags.NoError, (current, status) => current | status.Status);
                bool timeValid = (flags & X509ChainStatusFlags.NotTimeValid) == 0;
                PdfCertificateRevocationStatus revocation = policy.RevocationMode == X509RevocationMode.NoCheck
                    ? PdfCertificateRevocationStatus.NotChecked
                    : (flags & X509ChainStatusFlags.Revoked) != 0
                        ? PdfCertificateRevocationStatus.Revoked
                        : (flags & (X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) != 0
                            ? PdfCertificateRevocationStatus.Indeterminate
                            : PdfCertificateRevocationStatus.Good;
                string[] errors = [.. chain.ChainStatus.Select(status => status.StatusInformation.Trim()).Where(message => message.Length > 0)];
                PdfCertificateChainElement[] chainElements = [.. chain.ChainElements
                    .Select(element => ChainElement(element))];
                if (!trusted)
                    return TrustFailure(errors.FirstOrDefault() ?? "The signing certificate is not trusted.",
                        revocation == PdfCertificateRevocationStatus.Indeterminate
                            ? PdfCertificateTrustStatus.Indeterminate : PdfCertificateTrustStatus.Untrusted,
                        timeValid, revocation, errors, chainElements);
                return new PdfSignatureVerificationResult
                {
                    IsStructurallyValid = true,
                    IsCryptographicallyValid = true,
                    CertificateTrustWasChecked = true,
                    CertificateTrustStatus = PdfCertificateTrustStatus.Trusted,
                    IsCertificateTimeValid = true,
                    RevocationStatus = revocation,
                    RequestedRevocationMode = policy.RevocationMode,
                    RequestedVerificationTime = policy.VerificationTime,
                    CertificateDownloadsDisabled = policy.DisableCertificateDownloads,
                    CertificateChain = Array.AsReadOnly(chainElements),
                    DigestAlgorithmOid = digestAlgorithm,
                    SignatureAlgorithmOid = signatureAlgorithm,
                    SignaturePolicyOids = signaturePolicies,
                    SignaturePolicyImplied = policyImplied,
                    SignaturePolicyError = policyError,
                    SignerSubject = signer.Subject,
                    SignerIssuer = signer.Issuer,
                    SignerSerialNumber = signer.SerialNumber,
                    SignerCertificateSha256 = CertificateFingerprint(signer),
                    CertificateNotBefore = signer.NotBefore,
                    CertificateNotAfter = signer.NotAfter
                };
            }
            return new PdfSignatureVerificationResult
            {
                IsStructurallyValid = true,
                IsCryptographicallyValid = true,
                CertificateTrustWasChecked = checkCertificateTrust,
                CertificateTrustStatus = PdfCertificateTrustStatus.NotChecked,
                RevocationStatus = PdfCertificateRevocationStatus.NotChecked,
                DigestAlgorithmOid = digestAlgorithm,
                SignatureAlgorithmOid = signatureAlgorithm,
                SignaturePolicyOids = signaturePolicies,
                SignaturePolicyImplied = policyImplied,
                SignaturePolicyError = policyError,
                SignerSubject = signer?.Subject,
                SignerIssuer = signer?.Issuer,
                SignerSerialNumber = signer?.SerialNumber,
                SignerCertificateSha256 = CertificateFingerprint(signer),
                CertificateNotBefore = signer?.NotBefore,
                CertificateNotAfter = signer?.NotAfter
            };
        }
        catch (CryptographicException exception)
        {
            return new PdfSignatureVerificationResult
            {
                IsStructurallyValid = true,
                CertificateTrustWasChecked = checkCertificateTrust,
                RequestedRevocationMode = trustOptions?.RevocationMode,
                RequestedVerificationTime = trustOptions?.VerificationTime,
                CertificateDownloadsDisabled = trustOptions?.DisableCertificateDownloads,
                Error = exception.Message
            };
        }

        PdfSignatureVerificationResult TrustFailure(string error,
            PdfCertificateTrustStatus trustStatus, bool? timeValid,
            PdfCertificateRevocationStatus revocationStatus, IReadOnlyList<string> chainErrors,
            IReadOnlyList<PdfCertificateChainElement>? certificateChain = null) =>
            new()
            {
                IsStructurallyValid = true,
                IsCryptographicallyValid = true,
                CertificateTrustWasChecked = true,
                CertificateTrustStatus = trustStatus,
                IsCertificateTimeValid = timeValid,
                RevocationStatus = revocationStatus,
                RequestedRevocationMode = trustOptions?.RevocationMode,
                RequestedVerificationTime = trustOptions?.VerificationTime,
                CertificateDownloadsDisabled = trustOptions?.DisableCertificateDownloads,
                CertificateChainErrors = chainErrors,
                CertificateChain = certificateChain ?? [],
                DigestAlgorithmOid = digestAlgorithm,
                SignatureAlgorithmOid = signatureAlgorithm,
                SignaturePolicyOids = signaturePolicies,
                SignaturePolicyImplied = policyImplied,
                SignaturePolicyError = policyError,
                SignerSubject = signer?.Subject,
                SignerIssuer = signer?.Issuer,
                SignerSerialNumber = signer?.SerialNumber,
                SignerCertificateSha256 = CertificateFingerprint(signer),
                CertificateNotBefore = signer?.NotBefore,
                CertificateNotAfter = signer?.NotAfter,
                Error = error
            };
    }

    private static string? CertificateFingerprint(X509Certificate2? certificate) =>
        certificate is null ? null : Convert.ToHexString(SHA256.HashData(certificate.RawData));

    private static PdfCertificateChainElement ChainElement(X509ChainElement element)
    {
        X509Certificate2 certificate = element.Certificate;
        string[] status = [.. element.ChainElementStatus
            .Select(item => item.StatusInformation.Trim())
            .Where(message => message.Length > 0)];
        return new PdfCertificateChainElement(certificate.Subject, certificate.Issuer,
            certificate.SerialNumber, CertificateFingerprint(certificate)!,
            certificate.NotBefore, certificate.NotAfter, Array.AsReadOnly(status));
    }

    // SignaturePolicyIdentifier is either a SignaturePolicyId sequence or NULL for an implied
    // policy (RFC 5126 5.8.1). The attribute is optional metadata, so a value that cannot be read
    // is reported beside the result instead of failing the integrity check.
    private static (IReadOnlyList<string> Policies, bool Implied, string? Error) SignaturePolicies(SignerInfo signer)
    {
        const string signaturePolicyAttribute = "1.2.840.113549.1.9.16.2.15";
        var result = new List<string>();
        bool implied = false;
        string? error = null;
        foreach (CryptographicAttributeObject attribute in signer.SignedAttributes)
        {
            if (attribute.Oid?.Value != signaturePolicyAttribute) continue;
            foreach (AsnEncodedData value in attribute.Values)
            {
                try
                {
                    var reader = new AsnReader(value.RawData, AsnEncodingRules.DER);
                    if (reader.PeekTag().HasSameClassAndValue(Asn1Tag.Null))
                    {
                        reader.ReadNull();
                        implied = true;
                    }
                    else
                    {
                        AsnReader sequence = reader.ReadSequence();
                        string policy = sequence.ReadObjectIdentifier();
                        if (string.IsNullOrWhiteSpace(policy))
                            throw new AsnContentException("The signature policy identifier is empty.");
                        result.Add(policy);
                    }
                    if (reader.HasData)
                        throw new AsnContentException("The signature policy value has trailing data.");
                }
                catch (Exception exception) when (exception is AsnContentException or CryptographicException)
                {
                    error ??= "The CAdES signature policy attribute is malformed: " + exception.Message;
                }
            }
        }
        return (Array.AsReadOnly(result.Distinct(StringComparer.Ordinal).ToArray()), implied, error);
    }
}
