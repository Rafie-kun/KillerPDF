using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;
using KillerPDF.Services.Signing;
using Xunit;

namespace KillerPDF.Tests;

public sealed class CertificateCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        $"killerpdf-certificates-{Guid.NewGuid():N}");

    [Fact]
    public void Import_PublicCertificatePersistsWithoutGrantingTrust()
    {
        using X509Certificate2 certificate = CreateAuthority();
        string path = WriteCertificate("root.cer", certificate);
        var catalog = new CertificateCatalog(_directory);

        Assert.Equal(1, catalog.Import(path));

        CertificateCatalogEntry entry = Assert.Single(new CertificateCatalog(_directory).Read(false));
        Assert.Equal(certificate.Subject, entry.Subject);
        Assert.Equal(certificate.Issuer, entry.Issuer);
        Assert.Equal(certificate.RawData, entry.RawData);
        Assert.True(entry.IsCertificateAuthority);
        Assert.False(entry.IsTrustedRoot);
        Assert.False(entry.IsReadOnly);
        Assert.Equal(CertificateCatalogSource.KillerPdf, entry.Source);
    }

    [Fact]
    public void ExplicitTrustCanBeAddedRemovedAndDeletedWithoutChangingSignatureIntegrity()
    {
        using X509Certificate2 certificate = CreateAuthority();
        var catalog = new CertificateCatalog(_directory);
        catalog.Import(WriteCertificate("root.cer", certificate));
        string fingerprint = Assert.Single(catalog.Read(false)).Fingerprint;
        PdfDocument document = SignedDocument(certificate);
        Assert.False(Assert.Single(catalog.Inspect(document).Entries).Verification.IsCertificateTrusted);

        catalog.SetTrusted(fingerprint, true);

        var reloaded = new CertificateCatalog(_directory);
        Assert.True(Assert.Single(reloaded.Read(false)).IsTrustedRoot);
        PdfSignatureVerificationResult trusted = Assert.Single(reloaded.Inspect(document).Entries).Verification;
        Assert.True(trusted.IsCryptographicallyValid);
        Assert.True(trusted.IsCertificateTrusted);
        Assert.Equal(X509RevocationMode.Offline, trusted.RequestedRevocationMode);
        Assert.Equal(PdfCertificateRevocationStatus.Good, trusted.RevocationStatus);
        Assert.Equal(PdfSignatureValidationStatus.Valid, trusted.ValidationStatus);
        Assert.True(trusted.CertificateDownloadsDisabled);

        reloaded.SetTrusted(fingerprint, false);
        Assert.False(Assert.Single(catalog.Inspect(document).Entries).Verification.IsCertificateTrusted);
        catalog.SetTrusted(fingerprint, true);
        reloaded.Remove(fingerprint);
        Assert.Empty(catalog.Read(false));
        PdfSignatureVerificationResult removed = Assert.Single(catalog.Inspect(document).Entries).Verification;
        Assert.True(removed.IsCryptographicallyValid);
        Assert.False(removed.IsCertificateTrusted);
    }

    [Fact]
    public void PemBundleSuppliesAnIntermediateWithoutAutomaticallyTrustingIt()
    {
        using X509Certificate2 root = CreateAuthority();
        using X509Certificate2 intermediate = CreateIssued(root, true);
        using X509Certificate2 signer = CreateIssued(intermediate, false);
        Directory.CreateDirectory(_directory);
        string bundle = Path.Combine(_directory, "chain.pem");
        File.WriteAllText(bundle, root.ExportCertificatePem() + "\n" + intermediate.ExportCertificatePem());
        var catalog = new CertificateCatalog(_directory);
        PdfDocument document = SignedDocument(signer);

        Assert.Equal(2, catalog.Import(bundle));
        Assert.All(catalog.Read(false), entry => Assert.False(entry.IsTrustedRoot));
        Assert.False(Assert.Single(catalog.Inspect(document).Entries).Verification.IsCertificateTrusted);
        string rootFingerprint = catalog.Read(false).Single(entry => entry.Subject == root.Subject).Fingerprint;
        catalog.SetTrusted(rootFingerprint, true);

        PdfSignatureVerificationResult result = Assert.Single(catalog.Inspect(document).Entries).Verification;
        Assert.True(result.IsCertificateTrusted);
        Assert.Equal(3, result.CertificateChain.Count);
        Assert.Equal(intermediate.Subject, result.CertificateChain[1].Subject);
        Assert.Equal(X509RevocationMode.Offline, result.RequestedRevocationMode);
        Assert.Equal(PdfCertificateRevocationStatus.Indeterminate, result.RevocationStatus);
        Assert.Equal(PdfSignatureValidationStatus.Incomplete, result.ValidationStatus);
        Assert.False(catalog.Read(false).Single(entry => entry.Subject == intermediate.Subject).IsTrustedRoot);
        Assert.Equal(0, catalog.Import(bundle));
        Assert.True(catalog.Read(false).Single(entry => entry.Fingerprint == rootFingerprint).IsTrustedRoot);

        catalog.Import(WriteCertificate("signer.cer", signer));
        string signerFingerprint = catalog.Read(false).Single(entry => entry.Subject == signer.Subject).Fingerprint;
        Assert.Throws<InvalidOperationException>(() => catalog.SetTrusted(signerFingerprint, true));
    }

    [Fact]
    public void WindowsRootsAreVisibleReadOnlyAndCannotBeChangedThroughTheCatalog()
    {
        if (!OperatingSystem.IsWindows()) return;
        var catalog = new CertificateCatalog(_directory);

        IReadOnlyList<CertificateCatalogEntry> entries = catalog.Read();

        Assert.Contains(entries, entry => entry.Source == CertificateCatalogSource.WindowsLocalMachineRoot);
        Assert.All(entries, entry => Assert.True(entry.IsReadOnly));
        Assert.All(entries.Where(entry => entry.Source is CertificateCatalogSource.WindowsCurrentUserRoot
            or CertificateCatalogSource.WindowsLocalMachineRoot), entry => Assert.True(entry.IsTrustedRoot));
        Assert.All(entries.Where(entry => entry.Source is CertificateCatalogSource.WindowsCurrentUserIntermediate
            or CertificateCatalogSource.WindowsLocalMachineIntermediate), entry => Assert.False(entry.IsTrustedRoot));
        CertificateCatalogEntry windowsRoot = entries.First(entry => entry.IsTrustedRoot);
        Assert.Throws<InvalidOperationException>(() => catalog.SetTrusted(windowsRoot.Fingerprint, false));
        Assert.Throws<InvalidOperationException>(() => catalog.Remove(windowsRoot.Fingerprint));
        Assert.Empty(catalog.Read(false));
        Assert.Equal(entries.Select(entry => (entry.Source, entry.Fingerprint)),
            catalog.Read().Select(entry => (entry.Source, entry.Fingerprint)));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{\"version\":2,\"certificates\":[]}")]
    [InlineData("{\"version\":1,\"version\":1,\"certificates\":[]}")]
    [InlineData("{\"version\":1,\"certificates\":[{\"der\":\"!!!!\",\"trusted\":true}]}")]
    [InlineData("{\"version\":1,\"certificates\":[{\"der\":\"MAA=\",\"trusted\":false}]}")]
    public void DamagedCatalogIsNeverReplacedByAMutation(string damaged)
    {
        using X509Certificate2 root = CreateAuthority();
        var catalog = new CertificateCatalog(_directory);
        string input = WriteCertificate("root.cer", root);
        catalog.Import(input);
        string fingerprint = Assert.Single(catalog.Read(false)).Fingerprint;
        string stored = Path.Combine(_directory, "certificates.json");
        File.WriteAllText(stored, damaged);
        byte[] original = File.ReadAllBytes(stored);

        foreach (Action mutation in new Action[] { () => catalog.Read(false), () => catalog.Import(input),
                     () => catalog.SetTrusted(fingerprint, true), () => catalog.Remove(fingerprint) })
            Assert.Equal(CertificateCatalogProblem.CatalogDamaged,
                CertificateCatalog.ProblemOf(Assert.Throws<InvalidDataException>(mutation)));
        // Verification still runs, against Windows trust only, instead of failing every signature.
        PdfSignatureVerificationResult verified = Assert.Single(catalog.Inspect(SignedDocument(root)).Entries).Verification;
        Assert.True(verified.IsCryptographicallyValid);
        Assert.False(verified.IsCertificateTrusted);

        Assert.Equal(original, File.ReadAllBytes(stored));
    }

    [Theory]
    [InlineData("pfx")]
    [InlineData("renamed-pfx")]
    [InlineData("private-pem")]
    [InlineData("mixed-pem")]
    [InlineData("legacy-encrypted-pem")]
    public void PrivateKeyMaterialCannotBeImportedEvenWhenRenamedOrBundled(string kind)
    {
        using X509Certificate2 kept = CreateAuthority();
        using X509Certificate2 rejected = CreateAuthority();
        var catalog = new CertificateCatalog(_directory);
        catalog.Import(WriteCertificate("kept.cer", kept));
        string fingerprint = Assert.Single(catalog.Read(false)).Fingerprint;
        catalog.SetTrusted(fingerprint, true);
        string stored = Path.Combine(_directory, "certificates.json");
        byte[] original = File.ReadAllBytes(stored);
        string path = Path.Combine(_directory, kind == "pfx" ? "rejected.pfx"
            : kind == "renamed-pfx" ? "rejected.cer" : "rejected.pem");
        if (kind is "pfx" or "renamed-pfx")
            File.WriteAllBytes(path, rejected.Export(X509ContentType.Pkcs12));
        else if (kind == "legacy-encrypted-pem")
        {
            // OpenSSL's traditional encrypted key carries Proc-Type and DEK-Info headers inside the
            // block, which RFC 7468 parsing does not recognize as a block at all.
            using RSA key = rejected.GetRSAPrivateKey()!;
            File.WriteAllText(path, rejected.ExportCertificatePem()
                + "\n-----BEGIN RSA PRIVATE KEY-----\nProc-Type: 4,ENCRYPTED\n"
                + "DEK-Info: AES-128-CBC,00112233445566778899AABBCCDDEEFF\n\n"
                + Convert.ToBase64String(key.ExportRSAPrivateKey(), Base64FormattingOptions.InsertLineBreaks)
                + "\n-----END RSA PRIVATE KEY-----\n");
        }
        else
        {
            using RSA key = rejected.GetRSAPrivateKey()!;
            File.WriteAllText(path, (kind == "mixed-pem" ? rejected.ExportCertificatePem() + "\n" : "")
                + key.ExportPkcs8PrivateKeyPem());
        }

        Assert.Equal(kind == "pfx" ? CertificateCatalogProblem.WrongFileType : CertificateCatalogProblem.PrivateKey,
            CertificateCatalog.ProblemOf(Assert.Throws<InvalidDataException>(() => catalog.Import(path))));

        Assert.Equal(original, File.ReadAllBytes(stored));
        Assert.Equal(fingerprint, Assert.Single(catalog.Read(false)).Fingerprint);
    }

    [Fact]
    public void ImportLimitsRejectOversizedFilesAndBundlesWithoutPartialChanges()
    {
        using X509Certificate2 root = CreateAuthority();
        var catalog = new CertificateCatalog(_directory);
        catalog.Import(WriteCertificate("root.cer", root));
        string stored = Path.Combine(_directory, "certificates.json");
        byte[] original = File.ReadAllBytes(stored);
        string input = Path.Combine(_directory, "oversized.cer");
        using (var oversized = new FileStream(input, FileMode.Create, FileAccess.Write))
            oversized.SetLength(4 * 1024 * 1024 + 1);
        Assert.Equal(CertificateCatalogProblem.TooLarge,
            CertificateCatalog.ProblemOf(Assert.Throws<InvalidDataException>(() => catalog.Import(input))));
        string bundle = Path.Combine(_directory, "many.pem");
        File.WriteAllText(bundle, string.Join("\n", Enumerable.Repeat(root.ExportCertificatePem(), 257)));
        Assert.Equal(CertificateCatalogProblem.CatalogFull,
            CertificateCatalog.ProblemOf(Assert.Throws<InvalidDataException>(() => catalog.Import(bundle))));
        Assert.Equal(original, File.ReadAllBytes(stored));
    }

    [Fact]
    public async Task ConcurrentCatalogInstancesPreserveImportsAndExistingTrust()
    {
        using X509Certificate2 kept = CreateAuthority();
        var older = new CertificateCatalog(_directory);
        older.Import(WriteCertificate("kept.cer", kept));
        string fingerprint = Assert.Single(older.Read(false)).Fingerprint;
        older.SetTrusted(fingerprint, true);
        var paths = new List<string>();
        for (int index = 0; index < 8; index++)
        {
            using X509Certificate2 certificate = CreateAuthority();
            paths.Add(WriteCertificate($"root-{index}.cer", certificate));
        }

        int[] counts = await Task.WhenAll(paths.Select(path => Task.Run(() =>
            new CertificateCatalog(_directory).Import(path))));

        Assert.All(counts, count => Assert.Equal(1, count));
        IReadOnlyList<CertificateCatalogEntry> entries = older.Read(false);
        Assert.Equal(9, entries.Count);
        Assert.True(entries.Single(entry => entry.Fingerprint == fingerprint).IsTrustedRoot);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    // Public-only inputs were reported as private keys. An openssl export with header text imports;
    // other public formats are named as not being a certificate file.
    [Fact]
    public void PublicInputsAreNeverReportedAsPrivateKeys()
    {
        using X509Certificate2 root = CreateAuthority();
        var catalog = new CertificateCatalog(_directory);
        Directory.CreateDirectory(_directory);
        string withHeaders = Path.Combine(_directory, "openssl.pem");
        File.WriteAllText(withHeaders, "Bag Attributes\n    friendlyName: root\nsubject=CN=root\n" + root.ExportCertificatePem());
        Assert.Equal(1, catalog.Import(withHeaders));

        string trusted = Path.Combine(_directory, "trusted.pem");
        File.WriteAllText(trusted, root.ExportCertificatePem().Replace("CERTIFICATE", "TRUSTED CERTIFICATE"));
        string pkcs7 = Path.Combine(_directory, "bundle.cer");
        File.WriteAllBytes(pkcs7, new X509Certificate2Collection(root).Export(X509ContentType.Pkcs7)!);
        foreach (string path in new[] { trusted, pkcs7 })
            Assert.Equal(CertificateCatalogProblem.NotACertificate,
                CertificateCatalog.ProblemOf(Assert.Throws<InvalidDataException>(() => catalog.Import(path))));
    }

    // One unreadable certificate in a Windows store used to empty the whole list.
    [Fact]
    public void UnreadableWindowsCertificateIsSkipped()
    {
        using X509Certificate2 root = CreateAuthority();
        var entries = new List<CertificateCatalogEntry>();
        foreach (byte[] raw in new[] { root.RawData, [0x30, 0x03, 0x02, 0x01], new byte[200 * 1024] })
            CertificateCatalog.AddWindowsCertificate(entries, raw, true, CertificateCatalogSource.WindowsLocalMachineRoot);
        Assert.Equal(root.GetCertHashString(HashAlgorithmName.SHA256), Assert.Single(entries).Fingerprint);
    }

    // A refused import used to empty the list, Windows entries included, until Refresh.
    [Fact]
    public void RefusedChangeKeepsTheListAndLoadFailuresSayLoad()
    {
        string source = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!,
            "Controls", "CertificateCatalogDialog.cs"));
        string update = source[source.IndexOf("private async Task UpdateCatalogAsync(", StringComparison.Ordinal)..];
        update = update[..update.IndexOf("private void SetBusy(", StringComparison.Ordinal)];
        int refused = update.IndexOf("ShowError(ex, \"Str_CertCatalog_Error\");", StringComparison.Ordinal);
        Assert.True(refused >= 0);
        Assert.DoesNotContain("_entries = [];", update[..refused]);
        Assert.Equal(2, source.Split("ShowError(ex, \"Str_CertCatalog_LoadError\");").Length - 1);
    }

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    [Fact]
    public void LockedCatalogKeepsItsPreviousBytesAndTrustDecision()
    {
        using X509Certificate2 root = CreateAuthority();
        var catalog = new CertificateCatalog(_directory);
        catalog.Import(WriteCertificate("root.cer", root));
        string fingerprint = Assert.Single(catalog.Read(false)).Fingerprint;
        string stored = Path.Combine(_directory, "certificates.json");
        byte[] original = File.ReadAllBytes(stored);

        using (var reader = new FileStream(stored, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => catalog.SetTrusted(fingerprint, true));

        Assert.Equal(original, File.ReadAllBytes(stored));
        Assert.False(Assert.Single(catalog.Read(false)).IsTrustedRoot);
    }

    [Fact]
    public void ExplicitTrustKeepsExpirySeparateFromIdentityAndUsesOfflineRevocation()
    {
        using X509Certificate2 root = CreateAuthority(expired: true);
        var catalog = new CertificateCatalog(_directory);
        catalog.Import(WriteCertificate("expired.cer", root));
        catalog.SetTrusted(Assert.Single(catalog.Read(false)).Fingerprint, true);

        PdfSignatureVerificationResult result = Assert.Single(catalog.Inspect(SignedDocument(root)).Entries).Verification;

        Assert.True(result.IsCryptographicallyValid);
        Assert.False(result.IsCertificateTimeValid);
        Assert.True(result.IsCertificateTrusted);
        Assert.Equal(PdfCertificateTimeStatus.Expired, result.CertificateTimeStatus);
        Assert.Equal(X509RevocationMode.Offline, result.RequestedRevocationMode);
        Assert.Equal(PdfSignatureTimestampStatus.Missing, result.SignatureTimestampStatus);
        Assert.Equal(PdfSignatureValidationStatus.Incomplete, result.ValidationStatus);
    }

    [Fact]
    public void ExplicitTrustDoesNotClearChangedSignedBytes()
    {
        using X509Certificate2 root = CreateAuthority();
        var catalog = new CertificateCatalog(_directory);
        catalog.Import(WriteCertificate("root.cer", root));
        catalog.SetTrusted(Assert.Single(catalog.Read(false)).Fingerprint, true);
        byte[] signed = SignedBytes(root);
        int pageWidth = signed.AsSpan().IndexOf("/MediaBox [0 0 612 792]"u8);
        Assert.True(pageWidth >= 0);
        signed[pageWidth + "/MediaBox [0 0 61".Length] = (byte)'3';

        PdfSignatureVerificationResult result = Assert.Single(catalog.Inspect(PdfDocument.Open(signed)).Entries).Verification;

        Assert.False(result.IsCryptographicallyValid);
        Assert.False(result.IsCertificateTrusted);
        Assert.Equal(PdfSignatureValidationStatus.Invalid, result.ValidationStatus);
    }

    private static X509Certificate2 CreateIssued(X509Certificate2 issuer, bool authority)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=Catalog Issued {Guid.NewGuid():N}", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(authority, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(authority
            ? X509KeyUsageFlags.KeyCertSign : X509KeyUsageFlags.DigitalSignature, true));
        using X509Certificate2 publicCertificate = request.Create(issuer,
            issuer.NotBefore.ToUniversalTime().AddMinutes(1), issuer.NotAfter.ToUniversalTime().AddMinutes(-1),
            RandomNumberGenerator.GetBytes(16));
        return publicCertificate.CopyWithPrivateKey(key);
    }

    private static PdfDocument SignedDocument(X509Certificate2 certificate) => PdfDocument.Open(SignedBytes(certificate));

    private static byte[] SignedBytes(X509Certificate2 certificate) =>
        PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()), content =>
            {
                var cms = new SignedCms(new ContentInfo(content.ToArray()), detached: true);
                cms.ComputeSignature(new CmsSigner(certificate) { IncludeOption = X509IncludeOption.EndCertOnly });
                return cms.Encode();
            }, new PdfSignatureOptions { ReservedSignatureSize = 8192 });

    private static X509Certificate2 CreateAuthority(bool expired = false)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest($"CN=Catalog Root {Guid.NewGuid():N}", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign
            | X509KeyUsageFlags.DigitalSignature, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1));
    }

    private string WriteCertificate(string name, X509Certificate2 certificate)
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Cert));
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
