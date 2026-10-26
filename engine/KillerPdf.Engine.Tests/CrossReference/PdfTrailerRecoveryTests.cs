using System.Globalization;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Syntax;
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

    private static byte[] Fixture(string optionalOffset, bool includeXref)
    {
        const string content = "1 0 0 rg 4 4 24 24 re f";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 32 32] /Resources << >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
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
            text.Append("xref\n0 5\n0000000000 65535 f \n");
            foreach (int offset in offsets)
                text.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        }
        text.Append($"trailer\n<< /Size 5 /Root 1 0 R{optionalOffset} >>\nstartxref\n")
            .Append(includeXref ? xrefOffset : 0).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
