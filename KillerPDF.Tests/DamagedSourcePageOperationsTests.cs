using System.IO;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// A file the viewer opens through compatibility recovery must also work as a page source for
// Extract, Split, Merge and Rebuild, instead of failing with a damaged-structure error (#406).
public sealed class DamagedSourcePageOperationsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "killerpdf-damaged-source-" + Guid.NewGuid().ToString("N"));

    public DamagedSourcePageOperationsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    // Save, Save As, Print, Extract and Reduce all start here, so a file the viewer shows has to
    // come out strict-readable or none of them can finish.
    [Fact]
    public void TheSavePipelineNormalizesAFileTheViewerRecovers()
    {
        string path = Write("damaged.pdf", DamagedTwoPagePdf());

        PdfEngineIntegration.RepairHarmlessSaveArtifacts(path);

        Assert.Equal(2, PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(path))).Count);
    }

    [Fact]
    public void TheSavePipelineLeavesAHealthyFileAlone()
    {
        byte[] healthy = new PdfDocumentBuilder().AddBlankPage().Build();
        string path = Write("healthy.pdf", healthy);

        PdfEngineIntegration.RepairHarmlessSaveArtifacts(path);

        Assert.Equal(healthy, File.ReadAllBytes(path));
    }

    [Fact]
    public void ExtractPages_ReadsASourceTheViewerRecovers()
    {
        byte[] damaged = DamagedTwoPagePdf();

        byte[] extracted = PdfEngineIntegration.ExtractPages(damaged, [1]);

        Assert.Single(PdfPageInformation.Read(PdfDocument.Open(extracted)));
    }

    [Fact]
    public void SplitPages_ReadsASourceTheViewerRecovers()
    {
        byte[] damaged = DamagedTwoPagePdf();

        IReadOnlyList<byte[]> pages = PdfEngineIntegration.SplitPages(damaged);

        Assert.Equal(2, pages.Count);
        Assert.All(pages, page => Assert.Single(PdfPageInformation.Read(PdfDocument.Open(page))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MergeFiles_ReadsASourceTheViewerRecoversInEitherPosition(bool damagedFirst)
    {
        string damaged = Write("damaged.pdf", DamagedTwoPagePdf());
        string healthy = Write("healthy.pdf", new PdfDocumentBuilder().AddBlankPage().Build());
        byte[] original = File.ReadAllBytes(damaged);

        byte[] merged = PdfEngineIntegration.MergeFiles(damagedFirst ? [damaged, healthy] : [healthy, damaged]);

        Assert.Equal(3, PdfPageInformation.Read(PdfDocument.Open(merged)).Count);
        Assert.Equal(original, File.ReadAllBytes(damaged));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MergeDocuments_ReadsASourceTheViewerRecoversInEitherPosition(bool damagedFirst)
    {
        byte[] damaged = DamagedTwoPagePdf();
        byte[] healthy = new PdfDocumentBuilder().AddBlankPage().Build();

        byte[] merged = PdfEngineIntegration.MergeDocuments(damagedFirst ? [damaged, healthy] : [healthy, damaged]);

        Assert.Equal(3, PdfPageInformation.Read(PdfDocument.Open(merged)).Count);
    }

    [Fact]
    public void MergeFiles_KeepsAHealthyFirstFileAsTheUnchangedPrefix()
    {
        byte[] first = new PdfDocumentBuilder().AddBlankPage().Build();
        string healthy = Write("first.pdf", first);
        string damaged = Write("damaged.pdf", DamagedTwoPagePdf());

        byte[] merged = PdfEngineIntegration.MergeFiles([healthy, damaged]);

        Assert.True(merged.AsSpan(0, first.Length).SequenceEqual(first));
    }

    [Fact]
    public void RebuildDocument_ReadsASourceTheViewerRecovers()
    {
        string damaged = Write("damaged.pdf", DamagedTwoPagePdf());
        string rebuilt = Path.Combine(_directory, "rebuilt.pdf");

        PdfEngineIntegration.RebuildDocument(damaged, rebuilt);

        Assert.Equal(2, PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(rebuilt))).Count);
    }

    // A file with usage rights, a certification, a signature or a password is never rewritten by a
    // save once it is damaged: the rewrite would drop what the author put there, and the user keeps
    // the tab, the edits and the original file instead.
    [Fact]
    public void TheSavePipelineRefusesADamagedFileWithUsageRights()
    {
        byte[] damaged = DamageStartXref(RawPdf("/Perms << /UR3 5 0 R >>", "", SignatureObject("UR3")));
        string path = Write("rights.pdf", damaged);

        var refusal = Assert.Throws<DamagedSaveRefusedException>(
            () => PdfEngineIntegration.RepairHarmlessSaveArtifacts(path));

        Assert.Equal(DamagedSaveRefusal.UsageRights, refusal.Reason);
        Assert.Contains("usage rights", refusal.Message);
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    // The catalog link to the rights can be the part the damage took, which leaves the signature
    // dictionary itself as the only evidence.
    [Fact]
    public void TheSavePipelineRefusesADamagedFileWhoseUsageRightsLostTheirCatalogLink()
    {
        byte[] damaged = DamageStartXref(RawPdf("", "", SignatureObject("UR3")));
        string path = Write("orphan-rights.pdf", damaged);

        var refusal = Assert.Throws<DamagedSaveRefusedException>(
            () => PdfEngineIntegration.RepairHarmlessSaveArtifacts(path));

        Assert.Equal(DamagedSaveRefusal.UsageRights, refusal.Reason);
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    // A certification whose signature field is not listed under /Fields is still a certification.
    [Fact]
    public void TheSavePipelineRefusesADamagedCertifiedFileWhoseSignatureFieldIsNotListed()
    {
        byte[] damaged = DamageStartXref(RawPdf("/Perms << /DocMDP 5 0 R >> /AcroForm 6 0 R", "",
            SignatureObject("DocMDP"), "<< /Fields [] >>"));
        string path = Write("certified.pdf", damaged);

        var refusal = Assert.Throws<DamagedSaveRefusedException>(
            () => PdfEngineIntegration.RepairHarmlessSaveArtifacts(path));

        Assert.Equal(DamagedSaveRefusal.Certified, refusal.Reason);
        Assert.Contains("certified", refusal.Message);
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    [Fact]
    public void TheSavePipelineRefusesADamagedSignedFile()
    {
        byte[] damaged = DamageStartXref(SignedPdf());
        string path = Write("signed.pdf", damaged);

        var refusal = Assert.Throws<DamagedSaveRefusedException>(
            () => PdfEngineIntegration.RepairHarmlessSaveArtifacts(path));

        Assert.Equal(DamagedSaveRefusal.Signed, refusal.Reason);
        Assert.Contains("signature", refusal.Message);
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    [Fact]
    public void HasSignedSignaturesSeesTheSignatureOfADamagedFile()
    {
        string path = Write("signed.pdf", DamageStartXref(SignedPdf()));

        Assert.True(PdfEngineIntegration.HasSignedSignatures(path));
    }

    // With the trailer keyword damaged the recovered trailer still names /Encrypt, and with the
    // trailer cut off in the middle only the raw bytes do. A file cut off before any of it leaves
    // ciphertext pages as the only evidence.
    [Theory]
    [InlineData("trailer keyword damaged")]
    [InlineData("trailer cut after the encryption entry")]
    [InlineData("trailer gone")]
    public void TheSavePipelineRefusesADamagedPasswordProtectedFile(string damage)
    {
        byte[] damaged = DamageEncrypted(EncryptedPdf("secret"), damage);
        string path = Write("protected.pdf", damaged);

        var refusal = Assert.Throws<DamagedSaveRefusedException>(
            () => PdfEngineIntegration.RepairHarmlessSaveArtifacts(path));

        Assert.Equal(DamagedSaveRefusal.Encrypted, refusal.Reason);
        Assert.Contains("password protected", refusal.Message);
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    [Fact]
    public void ADamagedPasswordProtectedFileWithItsTrailerKeywordDamagedStillAsksForThePassword()
    {
        byte[] damaged = DamageEncrypted(EncryptedPdf("secret"), "trailer keyword damaged");
        string path = Write("protected.pdf", damaged);

        Assert.True(PdfDocument.OpenWithCompatibilityRecovery(damaged).IsEncrypted);
        Assert.False(PdfEngineIntegration.CanReadWithoutPassword(path));
        Assert.Single(PdfPageInformation.Read(PdfDocument.OpenWithCompatibilityRecovery(damaged, "secret")));
    }

    // Opening a protected file whose trailer is gone must fail rather than hand back ciphertext
    // as a plain document that a later save would write over the original.
    [Theory]
    [InlineData("")]
    [InlineData("secret")]
    public void RemovingEncryptionFromADamagedProtectedFileWhoseTrailerIsGoneIsRefused(string password)
    {
        byte[] damaged = DamageEncrypted(EncryptedPdf("secret"), "trailer cut after the encryption entry");
        string source = Write("protected.pdf", damaged);
        string destination = Path.Combine(_directory, "stripped.pdf");

        Assert.True(PdfEngineIntegration.HidesEncryption(damaged));
        var refusal = Assert.Throws<DamagedSaveRefusedException>(
            () => PdfEngineIntegration.RemoveEncryption(source, destination, password));
        Assert.Equal(DamagedSaveRefusal.Encrypted, refusal.Reason);
        Assert.False(PdfEngineIntegration.TryRemoveEncryptionWithoutPassword(source, destination));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public void AHealthyFileThatMentionsEncryptionInItsTextIsNotTreatedAsHidingIt()
    {
        byte[] healthy = RawPdf("", "", "<< /Note (see /Encrypt in the trailer) >>");

        Assert.False(PdfEngineIntegration.HidesEncryption(healthy));
        Assert.False(PdfEngineIntegration.HidesEncryption(DamagedTwoPagePdf()));
    }

    // The guard runs before the writer, but a refusal from the writer itself, such as the rewrite
    // of a signed document, must not be answered by the rebuild that does the same thing.
    [Theory]
    [InlineData("A full rewrite invalidates existing signatures. Set AllowSignatureInvalidation explicitly to proceed.", "Signed")]
    [InlineData("An encrypted PDF must be opened with a password before it can be rewritten.", "Encrypted")]
    public void AWriterRefusalNeverFallsBackToTheRebuild(string writerMessage, string expected)
    {
        byte[] damaged = DamagedTwoPagePdf();
        string path = Write("damaged.pdf", damaged);
        bool rebuilt = false;

        var refusal = Assert.Throws<DamagedSaveRefusedException>(() => PdfEngineIntegration.TryNormalizeDamagedCopy(
            path, damaged, _ => throw new InvalidOperationException(writerMessage), _ => rebuilt = true));

        Assert.Equal(Enum.Parse<DamagedSaveRefusal>(expected), refusal.Reason);
        Assert.False(rebuilt);
        Assert.Equal(damaged, File.ReadAllBytes(path));
    }

    [Fact]
    public void AnOrdinaryWriterFailureStillFallsBackToTheRebuild()
    {
        byte[] damaged = DamagedTwoPagePdf();
        string path = Write("damaged.pdf", damaged);

        bool normalized = PdfEngineIntegration.TryNormalizeDamagedCopy(path, damaged,
            _ => throw new NotSupportedException("The PDF object-number range is too large to rewrite."),
            copy => PdfEngineIntegration.RebuildDocument(copy, copy));

        Assert.True(normalized);
        Assert.Equal(2, PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(path))).Count);
    }

    // A valid two-page file whose startxref points past the end, which the strict parser refuses
    // and compatibility recovery rebuilds. The asserts keep the fixture honest.
    private static byte[] DamagedTwoPagePdf()
    {
        byte[] valid = new PdfDocumentBuilder().AddBlankPage(200, 300).AddBlankPage(300, 200).Build();
        byte[] damaged = DamageStartXref(valid);
        Assert.Equal(2, PdfPageInformation.Read(PdfDocument.OpenWithCompatibilityRecovery(damaged)).Count);
        return damaged;
    }

    private static byte[] DamageStartXref(byte[] valid)
    {
        string text = Encoding.Latin1.GetString(valid);
        int marker = text.LastIndexOf("startxref", StringComparison.Ordinal);
        int numberStart = text.IndexOf('\n', marker) + 1;
        int numberEnd = text.IndexOf('\n', numberStart);
        byte[] damaged = Encoding.Latin1.GetBytes(
            text[..numberStart] + (valid.Length + 5000).ToString() + text[numberEnd..]);
        Assert.ThrowsAny<Exception>(() => PdfDocument.Open(damaged));
        return damaged;
    }

    // A one-page file with a content stream, then the given objects numbered from 5.
    private static byte[] RawPdf(string catalogExtra, string pageExtra = "", params string[] extraObjects)
    {
        // Compressed like real page content, so ciphertext in its place fails to inflate.
        var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write("0 0 1 rg 0 0 10 10 re f"u8);
        string content = Encoding.Latin1.GetString(compressed.ToArray());
        var objects = new List<string>
        {
            $"<< /Type /Catalog /Pages 2 0 R {catalogExtra} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R {pageExtra} >>",
            $"<< /Filter /FlateDecode /Length {content.Length} >>\nstream\n{content}\nendstream"
        };
        objects.AddRange(extraObjects);
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(text.Length);
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xrefOffset = text.Length;
        text.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    // A signature dictionary, with the transform of a certification (DocMDP), of usage rights (UR3)
    // or none for an ordinary signature.
    private static string SignatureObject(string? transform) =>
        "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 10 20 30] /Contents <0000>"
        + (transform is null ? "" : $" /Reference [<< /Type /SigRef /TransformMethod /{transform} "
            + "/TransformParams << /Type /TransformParams /P 1 /V /1.2 >> >>]")
        + " >>";

    private static byte[] SignedPdf() => RawPdf("/AcroForm 6 0 R", "/Annots [7 0 R]",
        SignatureObject(null), "<< /Fields [7 0 R] >>",
        "<< /Type /Annot /Subtype /Widget /FT /Sig /T (s) /V 5 0 R /Rect [0 0 0 0] /P 3 0 R >>");

    private static byte[] EncryptedPdf(string userPassword) =>
        PdfDocumentEncryptionWriter.EncryptWithPasswords(PdfDocument.Open(RawPdf("")),
            new PdfPasswordEncryptionOptions { UserPassword = userPassword, OwnerPassword = "owner" });

    private static byte[] DamageEncrypted(byte[] encrypted, string damage)
    {
        int trailer = encrypted.AsSpan().LastIndexOf("trailer"u8);
        Assert.True(trailer >= 0);
        byte[] damaged;
        switch (damage)
        {
            case "trailer keyword damaged":
                damaged = (byte[])encrypted.Clone();
                damaged[trailer + 2] = (byte)'@';
                break;
            case "trailer cut after the encryption entry":
                int entry = encrypted.AsSpan(trailer).IndexOf("/Encrypt"u8);
                Assert.True(entry >= 0);
                int end = Encoding.Latin1.GetString(encrypted).IndexOf(" R", trailer + entry, StringComparison.Ordinal) + 2;
                damaged = encrypted[..end];
                break;
            default:
                damaged = encrypted[..encrypted.AsSpan(0, trailer).LastIndexOf("xref"u8)];
                break;
        }
        Assert.ThrowsAny<Exception>(() => PdfDocument.Open(damaged));
        return damaged;
    }

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
