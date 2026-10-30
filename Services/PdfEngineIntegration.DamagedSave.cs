using System;
using System.IO;
using System.Linq;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Writing;

namespace KillerPDF.Services;

internal static partial class PdfEngineIntegration
{
    /// <summary>Reports whether a file carries a signed signature field, which saving clears.</summary>
    internal static bool HasSignedSignatures(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(path));
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
    // never at open, and a signed or encrypted file keeps its original failure.
    private static bool TryNormalizeDamagedCopy(string path, byte[] source)
    {
        try
        {
            PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(source);
            if (recovered.IsEncrypted || PdfSignatureReader.Read(recovered).Any(signature => signature.IsSigned))
                return false;
            try
            {
                byte[] rewritten = PdfDocumentWriter.Write(recovered,
                    new PdfDocumentWriteOptions { ReplaceDanglingReferences = true });
                _ = PdfDocument.Open(rewritten);
                ReplaceWithBuiltResult(path, rewritten);
                return true;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                RebuildDocument(path, path);
                _ = PdfDocument.Open(File.ReadAllBytes(path));
                return true;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    /// <summary>Authenticates and fully rewrites a PDF without password encryption.</summary>
    internal static void RemoveEncryption(
        string sourcePath, string destinationPath, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(password);
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(
            File.ReadAllBytes(sourcePath), password);
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
