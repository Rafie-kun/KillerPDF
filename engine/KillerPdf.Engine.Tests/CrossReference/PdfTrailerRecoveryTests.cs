using System.Globalization;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.CrossReference;

public sealed class PdfTrailerRecoveryTests
{
    [Theory]
    [InlineData("Prev", "1.5", false)]
    [InlineData("Prev", "(bad)", false)]
    [InlineData("Prev", "2 1 R", false)]
    [InlineData("Prev", "-1", false)]
    [InlineData("XRefStm", "1.5", false)]
    [InlineData("XRefStm", "(bad)", false)]
    [InlineData("XRefStm", "2 1 R", false)]
    [InlineData("XRefStm", "null", false)]
    [InlineData("Prev", "1.5", true)]
    [InlineData("Prev", "(bad)", true)]
    [InlineData("Prev", "2 1 R", true)]
    [InlineData("Prev", "-1", true)]
    [InlineData("XRefStm", "1.5", true)]
    [InlineData("XRefStm", "(bad)", true)]
    [InlineData("XRefStm", "2 1 R", true)]
    [InlineData("XRefStm", "null", true)]
    public void RecoveryIgnoresInvalidOptionalOffsetsAndRetainsPage(string key, string value, bool includeXref)
    {
        byte[] bytes = Fixture($" /{key} {value}", includeXref);
        Assert.Throws<PdfSyntaxException>(() => PdfDocument.Open(bytes));

        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(bytes);

        Assert.Equal(new[] { "rg", "re", "f" }, new PdfPageContentReader(recovered)
            .ReadInstructions(0).Select(instruction => instruction.Operator));
        Assert.Equal(bytes, recovered.Source.ToArray());
    }

    [Fact]
    public void CompleteClassicTableRemainsStrictlyReadable()
    {
        PdfDocument document = PdfDocument.Open(Fixture("", true));
        Assert.Equal(new[] { "rg", "re", "f" }, new PdfPageContentReader(document)
            .ReadInstructions(0).Select(instruction => instruction.Operator));
    }

    // The trailer keyword is the only thing the damage touches, so the dictionary behind it still
    // names the document information and the identifier, which recovery has to carry over.
    [Fact]
    public void RecoveryKeepsInfoAndIdentifierWhenTheTrailerKeywordIsDamaged()
    {
        byte[] bytes = DamageTrailerKeyword(Fixture("", true, " /Info 5 0 R /ID [<0102><0304>]"));
        Assert.Throws<PdfSyntaxException>(() => PdfDocument.Open(bytes));

        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(bytes);

        Assert.Equal("Salvaged", PdfDocumentInformation.Read(recovered).Title);
        Assert.True(recovered.Trailer.ContainsKey(new PdfName("ID"u8)));
        Assert.Single(PdfPageInformation.Read(recovered));
    }

    // Without the trailer's /Encrypt the pages read as ciphertext and the file looks unprotected,
    // so the password prompt never appears and a later save writes the ciphertext out as plain.
    [Fact]
    public void RecoveryKeepsTheEncryptionWhenTheTrailerKeywordIsDamaged()
    {
        byte[] encrypted = PdfDocumentEncryptionWriter.EncryptWithPasswords(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()),
            new PdfPasswordEncryptionOptions { UserPassword = "secret", OwnerPassword = "owner" });
        byte[] bytes = DamageTrailerKeyword(encrypted);
        Assert.Throws<PdfSyntaxException>(() => PdfDocument.Open(bytes));

        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(bytes);

        Assert.True(recovered.IsEncrypted);
        Assert.False(recovered.CanReadPageContent);
        PdfDocument opened = PdfDocument.OpenWithCompatibilityRecovery(bytes, "secret");
        Assert.Single(PdfPageInformation.Read(opened));
    }

    private static byte[] DamageTrailerKeyword(byte[] bytes)
    {
        byte[] damaged = (byte[])bytes.Clone();
        int index = damaged.AsSpan().LastIndexOf("trailer"u8);
        Assert.True(index >= 0);
        damaged[index + 2] = (byte)'@';
        return damaged;
    }

    private static byte[] Fixture(string optionalOffset, bool includeXref, string trailerExtra = "")
    {
        const string content = "1 0 0 rg 4 4 24 24 re f";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 32 32] /Resources << >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Title (Salvaged) >>"
        ];
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(text.Length);
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xrefOffset = text.Length;
        if (includeXref)
        {
            text.Append("xref\n0 6\n0000000000 65535 f \n");
            foreach (int offset in offsets)
                text.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        text.Append($"trailer\n<< /Size 6 /Root 1 0 R{optionalOffset}{trailerExtra} >>\nstartxref\n")
            .Append(includeXref ? xrefOffset : 0).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
