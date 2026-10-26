using System.Security.Cryptography;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Security;

public sealed class PdfEncryptionLengthRecoveryTests
{
    [Theory]
    [InlineData(false, "user", PdfPasswordAuthenticationRole.User)]
    [InlineData(true, "user", PdfPasswordAuthenticationRole.User)]
    [InlineData(false, "owner", PdfPasswordAuthenticationRole.Owner)]
    [InlineData(true, "owner", PdfPasswordAuthenticationRole.Owner)]
    public void CompatibilityAuthenticatesAes256WithMissingOrByteLength(
        bool byteLength, string password, PdfPasswordAuthenticationRole role)
    {
        byte[] healthy = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user", OwnerPassword = "owner",
                AllowContentCopying = false, AllowHighQualityPrinting = false
            })
            .AddPage(64, 64, "0 0 1 rg 2 3 4 5 re f"u8.ToArray()).Build();
        PdfDocument control = PdfDocument.Open(healthy, password);
        Assert.Equal(role, control.PasswordAuthenticationRole);
        Assert.Equal(new[] { "rg", "re", "f" }, new PdfPageContentReader(control).ReadInstructions(0)
            .Select(instruction => instruction.Operator));
        PdfDocument owner = PdfDocument.Open(healthy, "owner");
        var reference = Assert.IsType<PdfIndirectReference>(owner.Trailer[new PdfName("Encrypt"u8)]);
        var encryption = Assert.IsType<PdfDictionary>(owner.Resolve(reference));
        var entries = encryption.Where(entry => !entry.Key.Equals(new PdfName("Length"u8)));
        var modified = new PdfDictionary(byteLength
            ? entries.Append(new KeyValuePair<PdfName, PdfObject>(new PdfName("Length"u8), new PdfInteger(32)))
            : entries);
        byte[] malformed = new PdfIncrementalUpdateBuilder(owner)
            .ReplaceObject(reference.ObjectNumber, modified).Build();
        byte[] original = malformed.ToArray();
        Assert.Throws<InvalidOperationException>(() => PdfDocument.Open(malformed, password));
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(malformed, password);
        Assert.True(recovered.IsDecrypted);
        Assert.Equal(role, recovered.PasswordAuthenticationRole);
        Assert.Equal(control.DeclaredPermissions, recovered.DeclaredPermissions);
        Assert.False(Assert.IsType<PdfDocumentPermissions>(recovered.DeclaredPermissions).AllowContentCopying);
        Assert.Equal(new[] { "rg", "re", "f" }, new PdfPageContentReader(recovered).ReadInstructions(0)
            .Select(instruction => instruction.Operator));
        Assert.ThrowsAny<CryptographicException>(() => PdfDocument.OpenWithCompatibilityRecovery(malformed, "wrong"));
        Assert.Equal(original, malformed);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(64)]
    [InlineData(128)]
    [InlineData(512)]
    public void OtherExplicitKeyLengthsRemainRejected(long length)
    {
        byte[] bytes = RewriteEncryption(new PdfInteger(length));
        Assert.Throws<InvalidOperationException>(() => PdfDocument.Open(bytes, "user"));
        Assert.Throws<InvalidOperationException>(() => PdfDocument.OpenWithCompatibilityRecovery(bytes, "user"));
    }

    [Fact]
    public void NonintegerLengthIsNotCoerced()
    {
        byte[] bytes = RewriteEncryption(new PdfString("32"u8.ToArray(), PdfStringForm.Literal));
        Assert.Throws<InvalidOperationException>(() => PdfDocument.OpenWithCompatibilityRecovery(bytes, "user"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AesGcmLengthValidationIsNotRelaxed(bool byteLength)
    {
        byte[] bytes = RewriteEncryption(byteLength ? new PdfInteger(32) : null, gcm: true);
        Assert.Throws<InvalidOperationException>(() => PdfDocument.Open(bytes, "user"));
        Assert.Throws<InvalidOperationException>(() => PdfDocument.OpenWithCompatibilityRecovery(bytes, "user"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveredLengthStillRequiresAuthenticatedPermissions(bool byteLength)
    {
        byte[] bytes = RewriteEncryption(byteLength ? new PdfInteger(32) : null, tamperPermissions: true);
        Assert.ThrowsAny<CryptographicException>(() => PdfDocument.OpenWithCompatibilityRecovery(bytes, "user"));
    }

    private static byte[] RewriteEncryption(PdfObject? length, bool gcm = false, bool tamperPermissions = false)
    {
        byte[] source = new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user", OwnerPassword = "owner",
                Algorithm = gcm ? PdfPasswordEncryptionAlgorithm.Aes256Gcm : PdfPasswordEncryptionAlgorithm.Aes256Cbc
            }).AddBlankPage().Build();
        PdfDocument owner = PdfDocument.Open(source, "owner");
        var reference = Assert.IsType<PdfIndirectReference>(owner.Trailer[new PdfName("Encrypt"u8)]);
        var encryption = Assert.IsType<PdfDictionary>(owner.Resolve(reference));
        var entries = encryption.Where(entry => !entry.Key.Equals(new PdfName("Length"u8))
            && (!tamperPermissions || !entry.Key.Equals(new PdfName("P"u8)))).ToList();
        if (length is not null) entries.Add(new(new PdfName("Length"u8), length));
        if (tamperPermissions)
            entries.Add(new(new PdfName("P"u8), new PdfInteger(
                Assert.IsType<PdfInteger>(encryption[new PdfName("P"u8)]).Value ^ 16)));
        return new PdfIncrementalUpdateBuilder(owner)
            .ReplaceObject(reference.ObjectNumber, new PdfDictionary(entries)).Build();
    }
}
