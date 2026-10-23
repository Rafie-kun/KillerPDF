using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;

namespace KillerPDF.Services.Signing;

internal enum CertificateCatalogSource
{
    KillerPdf,
    WindowsCurrentUserRoot,
    WindowsLocalMachineRoot,
    WindowsCurrentUserIntermediate,
    WindowsLocalMachineIntermediate
}

internal sealed record CertificateCatalogEntry(
    string Fingerprint,
    string Subject,
    string Issuer,
    DateTime NotBefore,
    DateTime NotAfter,
    bool IsCertificateAuthority,
    bool IsTrustedRoot,
    CertificateCatalogSource Source,
    byte[] RawData)
{
    public bool IsReadOnly => Source != CertificateCatalogSource.KillerPdf;
    public string DisplayName => Subject;
}

/// <summary>What went wrong with an import or the stored catalog, so the dialog can say it in
/// the user's language; the message stays English for the command line and logs.</summary>
internal enum CertificateCatalogProblem
{
    WrongFileType,
    NotACertificate,
    PrivateKey,
    TooLarge,
    CatalogFull,
    CatalogDamaged
}

internal sealed class CertificateCatalog
{
    internal const int MaximumCertificates = 256;
    private const int MaximumCertificateBytes = 128 * 1024;
    private const int MaximumImportBytes = 4 * 1024 * 1024;
    private const int MaximumCatalogBytes = 8 * 1024 * 1024;
    private readonly string _directory;
    private readonly string _catalogPath;

    public CertificateCatalog(string? directory = null)
    {
        _directory = Path.GetFullPath(directory ?? AppDataPaths.UserRoot);
        _catalogPath = Path.Combine(_directory, "certificates.json");
    }

    private const string ProblemKey = "KillerPDF.CertificateCatalogProblem";

    // InvalidDataException is sealed, so the problem rides in its Data for the dialog to localize.
    private static InvalidDataException Problem(CertificateCatalogProblem problem, string message,
        Exception? innerException = null)
    {
        var error = new InvalidDataException(message, innerException);
        error.Data[ProblemKey] = problem;
        return error;
    }

    /// <summary>The import or catalog problem an exception reports, if it is one of the expected ones.</summary>
    public static CertificateCatalogProblem? ProblemOf(Exception exception) =>
        exception.Data[ProblemKey] is CertificateCatalogProblem problem ? problem : null;

    /// <summary>The stored catalog file; deleting it starts an empty catalog.</summary>
    public string CatalogPath => _catalogPath;

    public IReadOnlyList<CertificateCatalogEntry> Read(bool includeWindows = true)
    {
        List<CertificateCatalogEntry> entries = ReadPrivate();
        if (includeWindows && OperatingSystem.IsWindows())
        {
            ReadWindows(StoreName.Root, StoreLocation.CurrentUser, CertificateCatalogSource.WindowsCurrentUserRoot);
            ReadWindows(StoreName.Root, StoreLocation.LocalMachine, CertificateCatalogSource.WindowsLocalMachineRoot);
            ReadWindows(StoreName.CertificateAuthority, StoreLocation.CurrentUser,
                CertificateCatalogSource.WindowsCurrentUserIntermediate);
            ReadWindows(StoreName.CertificateAuthority, StoreLocation.LocalMachine,
                CertificateCatalogSource.WindowsLocalMachineIntermediate);
        }
        return Array.AsReadOnly(entries.OrderBy(entry => entry.Source)
            .ThenBy(entry => entry.Subject, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Fingerprint, StringComparer.Ordinal).ToArray());

        void ReadWindows(StoreName name, StoreLocation location, CertificateCatalogSource source)
        {
            using var store = new X509Store(name, location);
            try { store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly); }
            catch (CryptographicException) { return; }
            foreach (X509Certificate2 certificate in store.Certificates)
                using (certificate)
                    AddWindowsCertificate(entries, certificate.RawData, name == StoreName.Root, source);
        }
    }

    /// <summary>Lists a Windows store certificate, skipping one KillerPDF cannot read. The store is
    /// managed by Windows, and one odd entry must not hide the rest of the list.</summary>
    internal static void AddWindowsCertificate(List<CertificateCatalogEntry> entries, byte[] rawData,
        bool trusted, CertificateCatalogSource source)
    {
        try { entries.Add(FromDer(rawData, trusted, source)); }
        catch (InvalidDataException) { }
    }

    public int Import(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string extension = Path.GetExtension(path);
        if (!extension.Equals(".cer", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".crt", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".pem", StringComparison.OrdinalIgnoreCase))
            throw Problem(CertificateCatalogProblem.WrongFileType,
                "Choose a public certificate file (.cer, .crt, or .pem).");
        byte[] data = ReadBoundedFile(path, MaximumImportBytes);
        List<CertificateCatalogEntry> imported = ReadImport(data);
        using FileStream writeLock = AcquireWriteLock();
        List<CertificateCatalogEntry> entries = ReadPrivate();
        var fingerprints = new HashSet<string>(entries.Select(entry => entry.Fingerprint), StringComparer.Ordinal);
        int added = 0;
        foreach (CertificateCatalogEntry entry in imported)
            if (fingerprints.Add(entry.Fingerprint)) { entries.Add(entry); added++; }
        if (added == 0) return 0;
        if (entries.Count > MaximumCertificates)
            throw Problem(CertificateCatalogProblem.CatalogFull,
                $"KillerPDF can store up to {MaximumCertificates} imported certificates.");
        Save(entries);
        return added;
    }
    public void SetTrusted(string fingerprint, bool trusted)
    {
        fingerprint = NormalizeFingerprint(fingerprint);
        using FileStream writeLock = AcquireWriteLock();
        List<CertificateCatalogEntry> entries = ReadPrivate();
        int index = entries.FindIndex(entry => entry.Fingerprint == fingerprint);
        if (index < 0) throw new InvalidOperationException("The certificate is not in the KillerPDF catalog.");
        if (trusted && !entries[index].IsCertificateAuthority)
            throw new InvalidOperationException("Only a certificate authority can be trusted as a root.");
        if (entries[index].IsTrustedRoot == trusted) return;
        entries[index] = entries[index] with { IsTrustedRoot = trusted };
        Save(entries);
    }

    public void Remove(string fingerprint)
    {
        fingerprint = NormalizeFingerprint(fingerprint);
        using FileStream writeLock = AcquireWriteLock();
        List<CertificateCatalogEntry> entries = ReadPrivate();
        if (entries.RemoveAll(entry => entry.Fingerprint == fingerprint) == 0)
            throw new InvalidOperationException("The certificate is not in the KillerPDF catalog.");
        Save(entries);
    }

    public PdfSignatureInspectionReport Inspect(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        List<CertificateCatalogEntry> entries;
        // A catalog that cannot be read adds no certificates and no trust, so signatures are
        // checked against Windows trust alone instead of failing outright.
        try { entries = ReadPrivate(); }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            entries = [];
        }
        var certificates = new List<X509Certificate2>();
        var roots = new List<X509Certificate2>();
        try
        {
            foreach (CertificateCatalogEntry entry in entries)
            {
                X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(entry.RawData);
                certificates.Add(certificate);
                if (entry.IsTrustedRoot) roots.Add(certificate);
            }
            var policy = new PdfSignatureTrustOptions
            {
                ExtraCertificates = certificates,
                DisableCertificateDownloads = true
            };
            PdfSignatureInspectionReport system = PdfSignatureInspection.Inspect(document, policy);
            if (roots.Count == 0) return system;
            var customPolicy = policy with { CustomTrustRoots = roots };
            return new PdfSignatureInspectionReport(Array.AsReadOnly(system.Entries.Select(entry =>
            {
                PdfSignatureVerificationResult original = entry.Verification;
                // System trust remains authoritative when it succeeds. A local trust decision
                // cannot repair changed bytes, expired certificates, or failed revocation.
                if (original.CertificateTrustStatus != PdfCertificateTrustStatus.Untrusted
                    || !original.IsStructurallyValid || !original.IsCryptographicallyValid
                    || original.IsCertificateTimeValid != true
                    || original.RevocationStatus is PdfCertificateRevocationStatus.Revoked
                        or PdfCertificateRevocationStatus.Indeterminate)
                    return entry;
                try
                {
                    PdfSignatureVerificationResult alternative = PdfSignatureVerifier.VerifyTrust(
                        document, entry.Signature, customPolicy);
                    return alternative.IsCertificateTrusted && alternative.IsStructurallyValid
                        && alternative.IsCryptographicallyValid && alternative.IsCertificateTimeValid == true
                        && alternative.RevocationStatus == original.RevocationStatus
                        ? entry with { Verification = alternative } : entry;
                }
                catch (Exception error) when (error is CryptographicException or InvalidOperationException
                    or FormatException or ArgumentException or NotSupportedException)
                {
                    return entry;
                }
            }).ToArray()));
        }
        finally
        {
            foreach (X509Certificate2 certificate in certificates) certificate.Dispose();
        }
    }

    private static string NormalizeFingerprint(string fingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Choose a certificate with a valid SHA-256 fingerprint.", nameof(fingerprint));
        return fingerprint.ToUpperInvariant();
    }

    private List<CertificateCatalogEntry> ReadPrivate()
    {
        byte[] bytes;
        try { bytes = ReadBoundedFile(_catalogPath, MaximumCatalogBytes); }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (InvalidDataException error)
        {
            throw Problem(CertificateCatalogProblem.CatalogDamaged,
                "The certificate catalog cannot be read. It has been left unchanged.", error);
        }
        try
        {
            using JsonDocument json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = json.RootElement;
            RequireProperties(root, "version", "certificates");
            if (root.GetProperty("version").GetInt32() != 1)
                throw new InvalidDataException("The certificate catalog version is not supported.");
            JsonElement items = root.GetProperty("certificates");
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > MaximumCertificates)
                throw new InvalidDataException("The certificate catalog contains too many certificates or an invalid list.");
            var entries = new List<CertificateCatalogEntry>();
            var fingerprints = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonElement item in items.EnumerateArray())
            {
                RequireProperties(item, "der", "trusted");
                byte[] der = item.GetProperty("der").GetBytesFromBase64();
                bool trusted = item.GetProperty("trusted").GetBoolean();
                CertificateCatalogEntry entry = FromDer(der, trusted, CertificateCatalogSource.KillerPdf);
                if (!fingerprints.Add(entry.Fingerprint) || (trusted && !entry.IsCertificateAuthority))
                    throw new InvalidDataException("The certificate catalog contains duplicate or invalid trust entries.");
                entries.Add(entry);
            }
            return entries;
        }
        catch (Exception error) when (error is JsonException or FormatException
            or InvalidOperationException or CryptographicException or InvalidDataException)
        {
            throw Problem(CertificateCatalogProblem.CatalogDamaged,
                "The certificate catalog cannot be read. It has been left unchanged.", error);
        }
    }

    private void Save(IReadOnlyList<CertificateCatalogEntry> entries)
    {
        using var output = new MemoryStream();
        using (var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("version", 1);
            json.WriteStartArray("certificates");
            foreach (CertificateCatalogEntry entry in entries)
            {
                json.WriteStartObject();
                json.WriteBase64String("der", entry.RawData);
                json.WriteBoolean("trusted", entry.IsTrustedRoot);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        if (output.Length > MaximumCatalogBytes)
            throw Problem(CertificateCatalogProblem.CatalogFull,
                "The certificate catalog has reached its storage limit.");
        AtomicFileOutput.Write(_catalogPath, output.ToArray());
    }

    private FileStream AcquireWriteLock()
    {
        Directory.CreateDirectory(_directory);
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(Path.Combine(_directory, "certificates.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(3))
            {
                Thread.Sleep(25);
            }
        }
    }

    private static byte[] ReadBoundedFile(string path, int maximumBytes)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length is <= 0 || input.Length > maximumBytes)
            throw Problem(CertificateCatalogProblem.TooLarge,
                "The certificate file is empty or exceeds the size limit.");
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        return bytes;
    }

    private static List<CertificateCatalogEntry> ReadImport(byte[] data)
    {
        if (data[0] == 0x30)
            return [FromDer(data, false, CertificateCatalogSource.KillerPdf)];
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException error)
        {
            throw Problem(CertificateCatalogProblem.NotACertificate,
                "The file does not contain a public DER or PEM certificate.", error);
        }
        ReadOnlySpan<char> pending = text.AsSpan();
        if (!pending.IsEmpty && pending[0] == '\uFEFF') pending = pending[1..];
        var entries = new List<CertificateCatalogEntry>();
        // A key anywhere rejects the whole file, including a legacy encrypted key whose Proc-Type
        // headers keep it from parsing as a block.
        if (HasPrivateKeyBlock(pending))
            throw Problem(CertificateCatalogProblem.PrivateKey,
                "PEM files may contain only public CERTIFICATE blocks. Private keys are not accepted.");
        // Text outside the blocks, such as the headers openssl writes, is ignored. Every block must
        // be a public certificate.
        while (PemEncoding.TryFind(pending, out PemFields fields))
        {
            ReadOnlySpan<char> label = pending[fields.Label];
            if (!label.SequenceEqual("CERTIFICATE"))
                throw Problem(CertificateCatalogProblem.NotACertificate,
                    "PEM files may contain only public CERTIFICATE blocks.");
            if (entries.Count >= MaximumCertificates)
                throw Problem(CertificateCatalogProblem.CatalogFull,
                    $"KillerPDF can store up to {MaximumCertificates} imported certificates.");
            if (fields.DecodedDataLength > MaximumCertificateBytes)
                throw Problem(CertificateCatalogProblem.TooLarge,
                    "The certificate file exceeds the import limits.");
            byte[] der = Convert.FromBase64String(pending[fields.Base64Data].ToString());
            entries.Add(FromDer(der, false, CertificateCatalogSource.KillerPdf));
            pending = pending[fields.Location.End..];
        }
        if (entries.Count == 0)
            throw Problem(CertificateCatalogProblem.NotACertificate,
                "The PEM file contains no public certificates.");
        return entries;
    }

    private static bool HasPrivateKeyBlock(ReadOnlySpan<char> text)
    {
        const string begin = "-----BEGIN ";
        for (int at = text.IndexOf(begin, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(begin, StringComparison.Ordinal))
        {
            text = text[(at + begin.Length)..];
            int end = text.IndexOf("-----", StringComparison.Ordinal);
            if (end < 0) return false;
            if (text[..end].EndsWith("PRIVATE KEY", StringComparison.Ordinal)) return true;
            text = text[end..];
        }
        return false;
    }

    private static CertificateCatalogEntry FromDer(byte[] data, bool trusted, CertificateCatalogSource source)
    {
        if (data.Length is <= 0 or > MaximumCertificateBytes)
            throw Problem(CertificateCatalogProblem.TooLarge,
                "A certificate is empty or exceeds the size limit.");
        try
        {
            X509ContentType contentType = X509Certificate2.GetCertContentType(data);
            if (contentType == X509ContentType.Pfx)
                throw Problem(CertificateCatalogProblem.PrivateKey,
                    "Only public certificates can be imported. Private keys and PFX files are not accepted.");
            if (contentType != X509ContentType.Cert)
                throw Problem(CertificateCatalogProblem.NotACertificate,
                    "The file must contain a single public certificate, not a certificate bundle.");
            using X509Certificate2 certificate = X509CertificateLoader.LoadCertificate(data);
            if (certificate.HasPrivateKey)
                throw Problem(CertificateCatalogProblem.PrivateKey,
                    "Only public certificates can be imported. Private keys and PFX files are not accepted.");
            if (!data.AsSpan().SequenceEqual(certificate.RawData))
                throw Problem(CertificateCatalogProblem.NotACertificate,
                    "The file must contain only a public DER certificate.");
            bool authority = certificate.Extensions["2.5.29.19"] is X509BasicConstraintsExtension constraints
                && constraints.CertificateAuthority;
            return new CertificateCatalogEntry(certificate.GetCertHashString(HashAlgorithmName.SHA256),
                certificate.Subject, certificate.Issuer, certificate.NotBefore, certificate.NotAfter,
                authority, trusted, source, certificate.RawData);
        }
        catch (CryptographicException error)
        {
            throw Problem(CertificateCatalogProblem.NotACertificate,
                "The file does not contain a readable public certificate.", error);
        }
    }

    private static void RequireProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The certificate catalog has an invalid record.");
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
            if (!remaining.Remove(property.Name))
                throw new InvalidDataException("The certificate catalog has unknown or repeated fields.");
        if (remaining.Count > 0)
            throw new InvalidDataException("The certificate catalog is missing required fields.");
    }
}
