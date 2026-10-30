using System;
using System.IO;
using System.Linq;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Writing;

namespace KillerPDF.Services;

/// <summary>What a damaged file carries that a rewrite would silently destroy.</summary>
internal enum DamagedSaveRefusal { Encrypted, Signed, Certified, UsageRights }

/// <summary>
/// Raised instead of rewriting a damaged file that is encrypted, signed, certified or carries
/// usage rights. The text comes from the string table when the app is running and falls back to
/// English for the command line, so the Save failed dialog shows one plain sentence.
/// </summary>
internal sealed class DamagedSaveRefusedException : InvalidOperationException
{
    internal DamagedSaveRefusedException(DamagedSaveRefusal reason) : base(TextFor(reason))
        => Reason = reason;

    internal DamagedSaveRefusal Reason { get; }

    private static string TextFor(DamagedSaveRefusal reason)
    {
        (string key, string english) = reason switch
        {
            DamagedSaveRefusal.Encrypted => ("Str_Save_DamagedEncrypted",
                "This PDF is damaged and password protected, so KillerPDF cannot repair or save it. The original file has not been changed."),
            DamagedSaveRefusal.Signed => ("Str_Save_DamagedSigned",
                "This PDF is damaged and carries a digital signature. Saving would break the signature, so the file was not changed."),
            DamagedSaveRefusal.Certified => ("Str_Save_DamagedCertified",
                "This PDF is damaged and certified by its author. Saving would break the certification, so the file was not changed."),
            _ => ("Str_Save_DamagedUsageRights",
                "This PDF is damaged and carries Reader usage rights. Saving would break them, so the file was not changed.")
        };
        return System.Windows.Application.Current?.TryFindResource(key) as string ?? english;
    }
}

internal static partial class PdfEngineIntegration
{
    /// <summary>Reports whether a file carries a signed signature field, which saving clears.</summary>
    internal static bool HasSignedSignatures(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // A damaged signed file still carries its signature, so it is read the way the viewer reads it.
        PdfDocument document = OpenForReading(File.ReadAllBytes(path));
        return PdfSignatureReader.Read(document).Any(signature => signature.IsSigned);
    }

    /// <summary>Repairs empty outlines and invalid direct crop boxes after serialization.</summary>
    internal static void RepairHarmlessSaveArtifacts(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] original = File.ReadAllBytes(path);
        PdfDocument document;
        try { document = PdfDocument.Open(original); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!TryNormalizeDamagedCopy(path, original)) throw;
            original = File.ReadAllBytes(path);
            document = PdfDocument.Open(original);
        }
        byte[] result = PdfSaveSanitizer.RepairHarmlessArtifacts(document);
        if (result.AsSpan().SequenceEqual(original)) return;
        ReplaceWithBuiltResult(path, result);
    }

    // The viewer opens damaged files through recovery and keeps their bytes, so every way out of
    // the app meets them here. Only this working copy changes: the file is rewritten at save time,
    // never at open. A damaged file with usage rights, a certification, a signature or encryption
    // is refused before anything is written, so the save fails and the original file stays.
    private static bool TryNormalizeDamagedCopy(string path, byte[] source)
        => TryNormalizeDamagedCopy(path, source,
            recovered => PdfDocumentWriter.Write(recovered,
                new PdfDocumentWriteOptions { ReplaceDanglingReferences = true }),
            copy => RebuildDocument(copy, copy));

    // The writer and the rebuild are parameters so a test can make the writer refuse.
    internal static bool TryNormalizeDamagedCopy(string path, byte[] source,
        Func<PdfDocument, byte[]> write, Action<string> rebuild)
    {
        try
        {
            PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(source);
            if (FindProtectedContent(recovered, source) is DamagedSaveRefusal refusal)
                throw new DamagedSaveRefusedException(refusal);
            try
            {
                byte[] rewritten = write(recovered);
                _ = PdfDocument.Open(rewritten);
                ReplaceWithBuiltResult(path, rewritten);
                return true;
            }
            catch (Exception error) when (error is not (OutOfMemoryException or DamagedSaveRefusedException))
            {
                // The rebuild drops signatures and encryption exactly as the writer refused to, so a
                // refusal for either is final. Only an ordinary rewrite failure reaches the rebuild.
                if (WriterProtectedContentRefusal(error) is DamagedSaveRefusal writerRefusal)
                    throw new DamagedSaveRefusedException(writerRefusal);
                rebuild(path);
                _ = PdfDocument.Open(File.ReadAllBytes(path));
                return true;
            }
        }
        catch (DamagedSaveRefusedException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    // What a rewrite of the recovered file would silently destroy, or null when there is none.
    // Recovery can lose the very entries that say so, so the raw bytes count as evidence too: the
    // encryption dictionary survives a lost trailer, and so does a signature dictionary.
    private static DamagedSaveRefusal? FindProtectedContent(PdfDocument recovered, ReadOnlyMemory<byte> source)
    {
        ReadOnlySpan<byte> bytes = source.Span;
        if (recovered.IsEncrypted || NamesEncryption(bytes))
            return DamagedSaveRefusal.Encrypted;
        if (PdfSignatureReader.Read(recovered).Any(signature => signature.IsSigned))
            return DamagedSaveRefusal.Signed;
        if (FindCatalogPermissions(recovered) is DamagedSaveRefusal permissions)
            return permissions;
        if (bytes.IndexOf("/DocMDP"u8) >= 0) return DamagedSaveRefusal.Certified;
        if (bytes.IndexOf("/UR3"u8) >= 0) return DamagedSaveRefusal.UsageRights;
        return bytes.IndexOf("/ByteRange"u8) >= 0 ? DamagedSaveRefusal.Signed : null;
    }

    // The trailer's /Encrypt entry, or failing that the encryption dictionary it points to, whose
    // /Filter names the security handler. Uncompressed page text could name these too, and the
    // only cost of that is a refused save on a file that was already damaged.
    private static bool NamesEncryption(ReadOnlySpan<byte> bytes)
    {
        return bytes.IndexOf("/Encrypt"u8) >= 0
            || FollowsFilterKey(bytes, "/Standard"u8) || FollowsFilterKey(bytes, "/Adobe.PubSec"u8);
    }

    private static bool FollowsFilterKey(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> handler)
    {
        int from = 0;
        while (from < bytes.Length)
        {
            int index = bytes[from..].IndexOf(handler);
            if (index < 0) return false;
            int at = from + index;
            if (bytes[Math.Max(0, at - 16)..at].IndexOf("/Filter"u8) >= 0) return true;
            from = at + handler.Length;
        }
        return false;
    }

    // Any catalog /Perms entry is a certification (DocMDP) or Reader usage rights (UR, UR3), and
    // either one stops being valid the moment the file is rewritten.
    private static DamagedSaveRefusal? FindCatalogPermissions(PdfDocument recovered)
    {
        try
        {
            if (!recovered.CrossReferences.TryGetTrailerValue(new PdfName("Root"u8), out PdfObject root)
                || ResolveObject(recovered, root) is not PdfDictionary catalog
                || !catalog.TryGetValue(new PdfName("Perms"u8), out PdfObject permissionsValue))
                return null;
            if (ResolveObject(recovered, permissionsValue) is not PdfDictionary permissions)
                return DamagedSaveRefusal.UsageRights;
            if (permissions.ContainsKey(new PdfName("DocMDP"u8))) return DamagedSaveRefusal.Certified;
            return permissions.Count > 0 ? DamagedSaveRefusal.UsageRights : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    private static PdfObject ResolveObject(PdfDocument document, PdfObject value) =>
        value is PdfIndirectReference reference ? document.Resolve(reference) : value;

    // The writer's own refusals to rewrite a signed or protected document, by their wording.
    private static DamagedSaveRefusal? WriterProtectedContentRefusal(Exception error)
    {
        if (error is not InvalidOperationException) return null;
        if (error.Message.StartsWith("A full rewrite invalidates existing signatures", StringComparison.Ordinal))
            return DamagedSaveRefusal.Signed;
        if (error.Message.StartsWith("An encrypted PDF must be opened with a password", StringComparison.Ordinal)
            || error.Message.StartsWith("The PDF user password does not permit", StringComparison.Ordinal))
            return DamagedSaveRefusal.Encrypted;
        return null;
    }

    /// <summary>
    /// True when the bytes name an /Encrypt entry but even recovery reads the file as unprotected,
    /// because the trailer that held it is gone and the pages are ciphertext.
    /// </summary>
    internal static bool HidesEncryption(ReadOnlyMemory<byte> source)
    {
        if (!NamesEncryption(source.Span)) return false;
        try
        {
            _ = PdfDocument.Open(source);
            return false;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        try { return !PdfDocument.OpenWithCompatibilityRecovery(source).IsEncrypted; }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    /// <summary>Authenticates and fully rewrites a PDF without password encryption.</summary>
    internal static void RemoveEncryption(
        string sourcePath, string destinationPath, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(password);
        byte[] source = File.ReadAllBytes(sourcePath);
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(source, password);
        // Without the trailer's /Encrypt the pages are ciphertext, and writing them out would
        // produce a file that has no encryption and cannot be decrypted.
        if (!document.IsEncrypted && HidesEncryption(source))
            throw new DamagedSaveRefusedException(DamagedSaveRefusal.Encrypted);
        byte[] result = PdfDocumentWriter.Write(document,
            new PdfDocumentWriteOptions { RemoveEncryption = true });
        ReplaceWithBuiltResult(destinationPath, result);
    }

    /// <summary>Attempts owner-only encryption removal through the engine.</summary>
    internal static bool TryRemoveEncryptionWithoutPassword(
        string sourcePath, string destinationPath)
    {
        try
        {
            RemoveEncryption(sourcePath, destinationPath, string.Empty);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }
}
