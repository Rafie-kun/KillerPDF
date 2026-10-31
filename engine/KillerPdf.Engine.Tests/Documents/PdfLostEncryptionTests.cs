using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

// A tool can rewrite a trailer and drop /Encrypt while the rest of the file stays sound, so the strict
// read saw a healthy unprotected PDF whose pages were ciphertext. Failing that read sends the file to
// recovery, where the app already refuses it on every route.
public sealed class PdfLostEncryptionTests
{
    [Fact]
    public void AnEncryptedFileWhoseTrailerLostItsEntryFailsTheStrictRead()
    {
        byte[] lost = WithoutEncryptEntry(Encrypted());

        var error = Assert.Throws<PdfLostEncryptionException>(() => PdfDocument.Open(lost));
        Assert.Contains("/Encrypt", error.Message, StringComparison.Ordinal);
        Assert.False(PdfDocument.OpenWithCompatibilityRecovery(lost).IsEncrypted);
    }

    [Fact]
    public void AnEncryptedFileWithItsEntryStillOpens() =>
        Assert.True(PdfDocument.Open(Encrypted()).IsEncrypted);

    // Decrypting tools can leave the old dictionary behind in a file that is perfectly readable.
    [Fact]
    public void ALeftoverEncryptionDictionaryOverPlainPagesStillOpens() =>
        Assert.False(PdfDocument.Open(WithLeftoverDictionary(Plain())).IsEncrypted);

    // Nothing to read proves nothing either way, and a blank page has nothing to lose.
    [Fact]
    public void ABlankPageWithALeftoverDictionaryStillOpens() =>
        Assert.False(PdfDocument.Open(WithLeftoverDictionary(new PdfDocumentBuilder().AddBlankPage().Build())).IsEncrypted);

    private static byte[] WithLeftoverDictionary(byte[] source)
    {
        var update = new PdfIncrementalUpdateBuilder(PdfDocument.Open(source));
        update.AddObject(new PdfDictionary([new(new PdfName("Filter"u8), new PdfName("Standard"u8))]));
        return update.Build();
    }

    private static byte[] Plain() => new PdfDocumentBuilder()
        .AddPage(200, 200, new PdfContentStreamBuilder().DrawImage(PdfImage.FromBitonal(2, 1, new byte[] { 0, 255 }), 0, 0, 100, 100))
        .Build();

    private static byte[] Encrypted() => PdfDocumentEncryptionWriter.EncryptWithPasswords(PdfDocument.Open(Plain()),
        new PdfPasswordEncryptionOptions { UserPassword = "secret", OwnerPassword = "owner" });

    // Spaces keep every offset, so the file stays structurally sound without the entry.
    private static byte[] WithoutEncryptEntry(byte[] source)
    {
        string text = Encoding.Latin1.GetString(source);
        int trailer = text.LastIndexOf("trailer", StringComparison.Ordinal);
        int entry = text.IndexOf("/Encrypt", trailer, StringComparison.Ordinal);
        Assert.True(entry > 0, "The fixture has no trailer /Encrypt entry.");
        int end = text.IndexOf(" R", entry, StringComparison.Ordinal) + 2;
        return Encoding.Latin1.GetBytes(text[..entry] + new string(' ', end - entry) + text[end..]);
    }
}
