using System.Security.Cryptography.X509Certificates;

namespace KillerPdf.Engine.Signing;

/// <summary>Certificate-chain policy for signature trust verification.</summary>
public sealed record PdfSignatureTrustOptions
{
    /// <summary>Gets additional intermediate certificates available to chain building.</summary>
    public IReadOnlyList<X509Certificate2> ExtraCertificates { get; init; } = [];
    /// <summary>Gets exclusive trust anchors. A nonempty list replaces system trust and disables additional anchors.</summary>
    public IReadOnlyList<X509Certificate2> CustomTrustRoots { get; init; } = [];
    /// <summary>Gets local anchors tried separately after system trust when exclusive custom roots are not specified.</summary>
    public IReadOnlyList<X509Certificate2> AdditionalTrustRoots { get; init; } = [];
    /// <summary>Gets the requested online, offline, or disabled revocation behavior.</summary>
    public X509RevocationMode RevocationMode { get; init; } = X509RevocationMode.NoCheck;
    /// <summary>Gets the time at which certificate validity is evaluated.</summary>
    public DateTime VerificationTime { get; init; } = DateTime.Now;
    /// <summary>Gets whether a trusted signature timestamp may select a historical certificate evaluation time.</summary>
    public bool UseTrustedSignatureTimestamp { get; init; }
    /// <summary>Gets the maximum time allowed for online revocation retrieval.</summary>
    public TimeSpan UrlRetrievalTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Gets whether certificate and revocation downloads are prohibited.</summary>
    public bool DisableCertificateDownloads { get; init; }
}
