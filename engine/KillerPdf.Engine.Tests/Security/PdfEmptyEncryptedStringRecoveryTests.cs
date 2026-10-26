using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Security;

public sealed class PdfEmptyEncryptedStringRecoveryTests
{
    [Theory]
    [InlineData(0, "user")]
    [InlineData(16, "user")]
    [InlineData(0, "owner")]
    [InlineData(16, "owner")]
    public void CompatibilityReadsEmptyOrIvOnlyAesStringsWithADiagnostic(int length, string password)
    {
        byte[] bytes = Fixture(new byte[length]);
        byte[] original = bytes.ToArray();
        PdfDocument strict = PdfDocument.Open(bytes, password);
        Assert.Throws<CryptographicException>(() => PdfDocumentInformation.Read(strict));
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(bytes, password);

        Assert.Equal("", PdfDocumentInformation.Read(recovered).Title);
        Assert.Contains(recovered.RecoveryDiagnostics, value => value.Contains("AES string", StringComparison.Ordinal));
        Assert.Equal(new[] { "rg", "re", "f" }, new PdfPageContentReader(recovered).ReadInstructions(0)
            .Select(instruction => instruction.Operator));
        Assert.Equal(password == "owner" ? PdfPasswordAuthenticationRole.Owner : PdfPasswordAuthenticationRole.User,
            recovered.PasswordAuthenticationRole);
        Assert.False(Assert.IsType<PdfDocumentPermissions>(recovered.DeclaredPermissions).AllowContentCopying);
        Assert.ThrowsAny<CryptographicException>(() => PdfDocument.OpenWithCompatibilityRecovery(bytes, "wrong"));
        Assert.Equal(original, bytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void CompatibilityAlsoRecoversLegacyAes128Strings(int length)
    {
        byte[] bytes = Fixture(new byte[length], legacy: true);
        PdfDocument strict = PdfDocument.Open(bytes, "user-password");
        Assert.Throws<CryptographicException>(() => PdfDocumentInformation.Read(strict));
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(bytes, "user-password");
        Assert.Equal("", PdfDocumentInformation.Read(recovered).Title);
        Assert.Contains(recovered.RecoveryDiagnostics, value => value.Contains("AES string", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(31)]
    [InlineData(33)]
    public void OtherIncompleteLengthsRemainRejected(int length)
    {
        byte[] bytes = Fixture(new byte[length]);
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(bytes, "user");
        Assert.Throws<CryptographicException>(() => PdfDocumentInformation.Read(recovered));
        Assert.Empty(recovered.RecoveryDiagnostics);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16)]
    public void AesGcmNeverUsesEmptyCbcRecovery(int length)
    {
        byte[] bytes = Fixture(new byte[length], gcm: true);
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(bytes, "user");
        Assert.Throws<CryptographicException>(() => PdfDocumentInformation.Read(recovered));
        Assert.Empty(recovered.RecoveryDiagnostics);
    }

    [Fact]
    public void ProperlyEncryptedEmptyStringsNeedNoRecovery()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions { UserPassword = "user", OwnerPassword = "owner" })
            .AddBlankPage().Build();
        var update = new PdfIncrementalUpdateBuilder(PdfDocument.Open(source, "owner"));
        var information = update.AddObject(new PdfDictionary(new Dictionary<PdfName, PdfObject>
            {
                [new PdfName("Title"u8)] = new PdfString([], PdfStringForm.Literal)
            }));
        byte[] bytes = update.SetDocumentInformation(information).Build();
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(bytes, "user");
        Assert.Equal("", PdfDocumentInformation.Read(recovered).Title);
        Assert.Empty(recovered.RecoveryDiagnostics);
    }

    [Fact]
    public void InvalidPaddingIsNeverReplacedWithAnEmptyString()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "A" })
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions { UserPassword = "user", OwnerPassword = "owner" })
            .AddBlankPage().Build();
        PdfDocument original = PdfDocument.Open(source, "owner");
        var information = Assert.IsType<PdfIndirectReference>(original.Trailer[new PdfName("Info"u8)]);
        var raw = Assert.IsType<PdfDictionary>(new PdfObjectParser(source,
            checked((int)original.CrossReferences[information.ObjectNumber].Field1)).ParseIndirectObject().Value);
        byte[] ciphertext = Assert.IsType<PdfString>(raw[new PdfName("Title"u8)]).Bytes.ToArray();
        Assert.Equal(32, ciphertext.Length);
        // Changing the last IV byte flips the final padding byte into an impossible value.
        ciphertext[15] ^= 128;
        byte[] damaged = Fixture(ciphertext, suppliedSource: source);
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(damaged, "user");
        Assert.Throws<CryptographicException>(() => PdfDocumentInformation.Read(recovered));
        Assert.Empty(recovered.RecoveryDiagnostics);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RewritingRecoveredEmptyStringsProducesValidEncryption(bool incremental)
    {
        byte[] source = Fixture([]);
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(source, "owner");
        Assert.Equal("", PdfDocumentInformation.Read(recovered).Title);
        byte[] saved;
        if (incremental)
        {
            var reference = Assert.IsType<PdfIndirectReference>(recovered.Trailer[new PdfName("Info"u8)]);
            saved = new PdfIncrementalUpdateBuilder(recovered)
                .ReplaceObject(reference.ObjectNumber, recovered.Resolve(reference)).Build();
            Assert.True(saved.AsSpan(0, source.Length).SequenceEqual(source));
        }
        else saved = PdfDocumentWriter.Write(recovered);

        PdfDocument reopened = PdfDocument.Open(saved, "user");
        Assert.Equal("", PdfDocumentInformation.Read(reopened).Title);
        Assert.Empty(reopened.RecoveryDiagnostics);
        Assert.False(Assert.IsType<PdfDocumentPermissions>(reopened.DeclaredPermissions).AllowContentCopying);
    }

    private static byte[] Fixture(byte[] rawString, bool legacy = false, bool gcm = false,
        byte[]? suppliedSource = null)
    {
        byte[] source = suppliedSource ?? (legacy ? PdfEncryptionTests.Revision4Fixture() : new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user", OwnerPassword = "owner", AllowContentCopying = false,
                Algorithm = gcm ? PdfPasswordEncryptionAlgorithm.Aes256Gcm : PdfPasswordEncryptionAlgorithm.Aes256Cbc
            }).AddPage(64, 64, "0 0 1 rg 2 3 4 5 re f"u8.ToArray()).Build());
        PdfDocument document = PdfDocument.Open(source, legacy ? "owner-password" : "owner");
        int number = document.CrossReferences.Keys.Max() + 1;
        using var output = new MemoryStream();
        output.Write(source);
        output.WriteByte((byte)'\n');
        long objectOffset = output.Position;
        Write($"{number} 0 obj\n");
        PdfObjectWriter.Write(output, new PdfDictionary(new Dictionary<PdfName, PdfObject>
        {
            [new PdfName("Title"u8)] = new PdfString(rawString, PdfStringForm.Hexadecimal)
        }));
        Write("\nendobj\n");
        long xref = output.Position;
        Write($"xref\n{number} 1\n{objectOffset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \ntrailer\n");
        var trailer = document.Trailer.ToDictionary(item => item.Key, item => item.Value);
        trailer[new PdfName("Info"u8)] = new PdfIndirectReference(number, 0);
        trailer[new PdfName("Size"u8)] = new PdfInteger(number + 1);
        trailer[new PdfName("Prev"u8)] = new PdfInteger(PdfStartXref.Find(source).Offset);
        PdfObjectWriter.Write(output, new PdfDictionary(trailer));
        Write($"\nstartxref\n{xref}\n%%EOF\n");
        return output.ToArray();

        void Write(string value) => output.Write(Encoding.ASCII.GetBytes(value));
    }
}
