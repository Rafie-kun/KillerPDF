using System.Security.Cryptography.X509Certificates;

namespace KillerPdf.Engine.Signing;

/// <summary>One certificate in the evaluated signing chain.</summary>
public sealed record PdfCertificateChainElement(
    string Subject, string Issuer, string SerialNumber, string Sha256Fingerprint,
    DateTimeOffset NotBefore, DateTimeOffset NotAfter,
    IReadOnlyList<string> StatusMessages);

/// <summary>Cryptographic verification outcome for one structurally inspected signature.</summary>
public sealed record PdfSignatureVerificationResult
{
    /// <summary>Gets the overall validation status across integrity, trust, time, and revocation.</summary>
    public PdfSignatureValidationStatus ValidationStatus
    {
        get
        {
            if (!IsStructurallyValid || !IsCryptographicallyValid
                || CertificateTrustStatus == PdfCertificateTrustStatus.Untrusted
                || (IsCertificateTimeValid == false && CertificateTimeStatus != PdfCertificateTimeStatus.Expired)
                || RevocationStatus == PdfCertificateRevocationStatus.Revoked)
                return PdfSignatureValidationStatus.Invalid;
            return CertificateTrustStatus == PdfCertificateTrustStatus.Trusted
                && IsCertificateTimeValid == true
                && RevocationStatus == PdfCertificateRevocationStatus.Good
                && (!TrustedTimestampTime.HasValue || TimestampRevocationStatus == PdfCertificateRevocationStatus.Good)
                    ? PdfSignatureValidationStatus.Valid
                    : PdfSignatureValidationStatus.Incomplete;
        }
    }
    /// <summary>Gets whether the signature dictionary and byte ranges are structurally valid.</summary>
    public bool IsStructurallyValid { get; init; }
    /// <summary>Gets whether the cryptographic signature matches the signed bytes.</summary>
    public bool IsCryptographicallyValid { get; init; }
    /// <summary>Gets whether certificate-chain trust was evaluated.</summary>
    public bool CertificateTrustWasChecked { get; init; }
    /// <summary>Gets whether the signing certificate chains to a trusted root.</summary>
    public bool IsCertificateTrusted => CertificateTrustStatus == PdfCertificateTrustStatus.Trusted;
    /// <summary>Gets the independently evaluated certificate-chain trust outcome.</summary>
    public PdfCertificateTrustStatus CertificateTrustStatus { get; init; }
    /// <summary>Gets whether the signing certificate was within its validity period.</summary>
    public bool? IsCertificateTimeValid { get; init; }
    /// <summary>Gets the certificate validity classification at the evaluated time.</summary>
    public PdfCertificateTimeStatus CertificateTimeStatus { get; init; }
    /// <summary>Gets the independently evaluated certificate revocation outcome.</summary>
    public PdfCertificateRevocationStatus RevocationStatus { get; init; }
    /// <summary>Gets the revocation mode requested for this trust evaluation.</summary>
    public X509RevocationMode? RequestedRevocationMode { get; init; }
    /// <summary>Gets the certificate verification time requested for this trust evaluation.</summary>
    public DateTime? RequestedVerificationTime { get; init; }
    /// <summary>Gets the time actually used to evaluate the signing-certificate chain.</summary>
    public DateTime? EvaluatedVerificationTime { get; init; }
    /// <summary>Gets the independently evaluated signature-timestamp evidence.</summary>
    public PdfSignatureTimestampStatus SignatureTimestampStatus { get; init; }
    /// <summary>Gets the trusted timestamp used for historical certificate evaluation.</summary>
    public DateTimeOffset? TrustedTimestampTime { get; init; }
    /// <summary>Gets the timestamp authority's independently evaluated revocation status.</summary>
    public PdfCertificateRevocationStatus TimestampRevocationStatus { get; init; }
    /// <summary>Gets the subject of the cryptographically bound timestamp signing certificate.</summary>
    public string? TimestampAuthoritySubject { get; init; }
    /// <summary>Gets the uppercase SHA-256 fingerprint of the bound timestamp signing certificate.</summary>
    public string? TimestampAuthorityCertificateSha256 { get; init; }
    /// <summary>Gets why optional timestamp evidence could not be used.</summary>
    public string? TimestampError { get; init; }
    /// <summary>Gets whether the requested trust policy prohibited network downloads.</summary>
    public bool? CertificateDownloadsDisabled { get; init; }
    /// <summary>Gets certificate-chain status details supplied by the platform.</summary>
    public IReadOnlyList<string> CertificateChainErrors { get; init; } = [];
    /// <summary>Gets certificates in evaluated leaf-to-root chain order.</summary>
    public IReadOnlyList<PdfCertificateChainElement> CertificateChain { get; init; } = [];
    /// <summary>Gets the CMS digest algorithm object identifier.</summary>
    public string? DigestAlgorithmOid { get; init; }
    /// <summary>Gets the CMS signature algorithm object identifier.</summary>
    public string? SignatureAlgorithmOid { get; init; }
    /// <summary>Gets CAdES signature policy identifiers declared by the signer.</summary>
    public IReadOnlyList<string> SignaturePolicyOids { get; init; } = [];
    /// <summary>Gets whether the signer declared an implied signature policy (a NULL policy value).</summary>
    public bool SignaturePolicyImplied { get; init; }
    /// <summary>
    /// Gets why the signature policy attribute could not be read, or null. It is optional
    /// metadata, so a bad one is reported here and never changes the integrity result.
    /// </summary>
    public string? SignaturePolicyError { get; init; }
    /// <summary>Gets the signing certificate subject.</summary>
    public string? SignerSubject { get; init; }
    /// <summary>Gets the signing certificate issuer.</summary>
    public string? SignerIssuer { get; init; }
    /// <summary>Gets the signing certificate serial number.</summary>
    public string? SignerSerialNumber { get; init; }
    /// <summary>Gets the uppercase SHA-256 fingerprint of the signing certificate.</summary>
    public string? SignerCertificateSha256 { get; init; }
    /// <summary>Gets the beginning of the signing certificate validity period.</summary>
    public DateTimeOffset? CertificateNotBefore { get; init; }
    /// <summary>Gets the end of the signing certificate validity period.</summary>
    public DateTimeOffset? CertificateNotAfter { get; init; }
    /// <summary>Gets the verification failure message, if any.</summary>
    public string? Error { get; init; }
}

/// <summary>Overall signature validation status across independent evidence categories.</summary>
public enum PdfSignatureValidationStatus
{
    /// <summary>One or more required validation categories failed.</summary>
    Invalid,
    /// <summary>Available evidence passes, but at least one required category was not conclusive.</summary>
    Incomplete,
    /// <summary>Integrity, identity trust, certificate time, and revocation all passed.</summary>
    Valid
}

/// <summary>Trust result for the signing certificate identity.</summary>
public enum PdfCertificateTrustStatus
{
    /// <summary>Certificate trust was not requested.</summary>
    NotChecked,
    /// <summary>The certificate chains to an accepted trust anchor.</summary>
    Trusted,
    /// <summary>The certificate chain was evaluated and rejected.</summary>
    Untrusted,
    /// <summary>The certificate chain could not be evaluated conclusively.</summary>
    Indeterminate
}

/// <summary>Outcome of optional signature-timestamp verification.</summary>
public enum PdfSignatureTimestampStatus
{
    /// <summary>Timestamp trust was not evaluated.</summary>
    NotChecked,
    /// <summary>The signer has no signature timestamp.</summary>
    Missing,
    /// <summary>The timestamp did not pass binding, trust or validity checks.</summary>
    Rejected,
    /// <summary>The requested policy could not establish sufficient timestamp evidence.</summary>
    Indeterminate,
    /// <summary>The timestamp passes the requested identity and validity policy.</summary>
    Trusted
}

/// <summary>Certificate validity at the time used for chain evaluation.</summary>
public enum PdfCertificateTimeStatus
{
    /// <summary>Certificate time was not evaluated.</summary>
    NotChecked,
    /// <summary>The evaluated chain is within its validity periods.</summary>
    Valid,
    /// <summary>Only elapsed certificate expiry prevents a time-valid chain.</summary>
    Expired,
    /// <summary>A certificate in the chain is not yet valid.</summary>
    NotYetValid,
    /// <summary>The time failure cannot be classified conclusively.</summary>
    Indeterminate
}

/// <summary>Revocation result for the signing certificate chain.</summary>
public enum PdfCertificateRevocationStatus
{
    /// <summary>Revocation was not requested.</summary>
    NotChecked,
    /// <summary>The checked chain contains no revoked certificate.</summary>
    Good,
    /// <summary>A certificate in the chain is revoked.</summary>
    Revoked,
    /// <summary>Revocation evidence was unavailable or inconclusive.</summary>
    Indeterminate
}
