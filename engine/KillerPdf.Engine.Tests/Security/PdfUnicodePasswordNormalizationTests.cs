using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using Xunit;

namespace KillerPdf.Engine.Tests.Security;

public sealed class PdfUnicodePasswordNormalizationTests
{
    [Theory]
    [InlineData("\U0002F868", "\U0002136A", "\u36FC")]
    [InlineData("\U0002F874", "\u5F33", "\u5F53")]
    [InlineData("\U0002F91F", "\u43AB", "\U000243AB")]
    [InlineData("\U0002F95F", "\u7AAE", "\u7AEE")]
    [InlineData("\U0002F9BF", "\u4D57", "\u45D7")]
    public void Unicode32CanonicalMappingsAuthenticateExistingEncryptedContent(
        string compatibilityCharacter, string unicode32Mapping, string laterMapping)
    {
        // Encrypt with an already normalized Unicode 3.2 password, independently of
        // normalization of the compatibility character used when reopening the file.
        byte[] bytes = new PdfDocumentBuilder().SetPasswordEncryption(new PdfPasswordEncryptionOptions {
            UserPassword = "Password" + unicode32Mapping + "!", OwnerPassword = "owner"
        }).AddPage(16, 16, "1 0 0 rg 0 0 16 16 re f"u8.ToArray()).Build();
        PdfDocument document = PdfDocument.Open(bytes, "Password" + compatibilityCharacter + "!");
        Assert.True(document.IsDecrypted);
        var rendered = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(16, 16));
        Assert.Equal(new byte[] {0, 0, 255, 255}, rendered.Pixels.Slice(0, 4).ToArray());
        Assert.ThrowsAny<CryptographicException>(() => PdfDocument.Open(bytes, "Password" + laterMapping + "!"));
    }
    [Fact]
    public void CompatibilityAuthenticatesTheExactPublishedLegacyPasswordWithoutWeakeningStrictOpen()
    {
        // Authentication vector from the retained SafeDocs corrigendum5 once fixture,
        // SHA256 628F77AF77AFFC4CE8768AFBD8D4E073760B46393F2C5FB17783CF5686CC9AAB.
        // It hashes the pre-normalized Hangul sequence literally. Keep this malformed
        // producer case separate from the strict Unicode 3.2 acceptance tests above.
        const string dictionary = """
            << /Filter /Standard /P -4 /Length 256 /V 5 /R 6 /EncryptMetadata true
            /StmF /StdCF /StrF /StdCF /CF << /StdCF << /AuthEvent /DocOpen /CFM /AESV3 /Length 32 >> >>
            /O <ee07130f603cc934703f77c92d7e055cff9326b91ff4e3a7de028cd9df134034fed7816b444875b7a50dc9c0c110bcd9>
            /U <fc93eb232590e7145dd89de90973da223545d849502ec453e0b259e8a618ea166cc3395302a915568a2707cc9aeb8333>
            /OE <89ef46ebf848e00c6440594a0a3bb6f05c586ac0714cde9aa2649a31e348216a>
            /UE <6005591b591f46c2212387539215758ffd53423742ced1c77ef8f62e33c83c40>
            /Perms <9414700a08893c970bdd1de5bd95fdc8> >>
            """;
        var encryption = Assert.IsType<PdfDictionary>(new PdfObjectParser(Encoding.ASCII.GetBytes(dictionary)).ParseSingleObject());
        const string password = "\uAC00\u0300\u0323";
        Assert.ThrowsAny<CryptographicException>(() => PdfStandardSecurityHandler.Create(encryption, password, ReadOnlyMemory<byte>.Empty));
        var recovered = PdfStandardSecurityHandler.Create(encryption, password, ReadOnlyMemory<byte>.Empty, compatibilityRecovery: true);
        Assert.True(recovered.RecoveredUnnormalizedPassword);
        Assert.Equal(PdfPasswordAuthenticationRole.Owner, recovered.AuthenticationRole);
        Assert.ThrowsAny<CryptographicException>(() => PdfStandardSecurityHandler.Create(encryption, "wrong", ReadOnlyMemory<byte>.Empty, true));
        var invalidPerms = new PdfDictionary(encryption.Where(pair => pair.Key.ValueAsLatin1() != "Perms")
            .Append(new(new PdfName("Perms"u8), new PdfString(new byte[16], PdfStringForm.Hexadecimal))));
        Assert.ThrowsAny<CryptographicException>(() => PdfStandardSecurityHandler.Create(invalidPerms, password, ReadOnlyMemory<byte>.Empty, true));
    }
}
