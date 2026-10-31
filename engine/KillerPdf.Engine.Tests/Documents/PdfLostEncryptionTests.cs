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
    [Theory]
    [InlineData("/F#69lter /Standard")]
    [InlineData("/Filter /Sta#6Edard")]
    [InlineData("/F#69lter % a security handler can follow a long comment\n /Sta#6Edard")]
    public void EscapedSecurityNamesStillProtectCiphertextAfterTheTrailerEntryIsLost(string marker)
    {
        string original = Encoding.Latin1.GetString(Encrypted());
        string changed = original.Replace("/Filter /Standard", marker, StringComparison.Ordinal);
        Assert.NotEqual(original, changed);
        byte[] protectedSource = Reindex(changed);
        Assert.True(PdfDocument.Open(protectedSource, "secret").IsDecrypted);
        byte[] lost = WithoutEncryptEntry(protectedSource);
        Assert.Throws<PdfLostEncryptionException>(() => PdfDocumentWriter.Write(PdfDocument.Open(lost)));
    }

    private static byte[] Reindex(string source)
    {
        int xref = source.LastIndexOf("\nxref\n", StringComparison.Ordinal) + 1;
        int trailer = source.IndexOf("trailer", xref, StringComparison.Ordinal);
        int count = int.Parse(System.Text.RegularExpressions.Regex.Match(source[trailer..], @"/Size\s+(\d+)").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
        var table = new StringBuilder($"xref\n0 {count}\n0000000000 65535 f \n");
        for (int index = 1; index < count; index++)
        {
            int offset = source.IndexOf($"\n{index} 0 obj", StringComparison.Ordinal) + 1;
            Assert.True(offset > 0);
            table.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        string tail = source[trailer..];
        int start = tail.IndexOf("startxref", StringComparison.Ordinal);
        return Encoding.Latin1.GetBytes(source[..xref] + table + tail[..start] + $"startxref\n{xref}\n%%EOF\n");
    }

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
