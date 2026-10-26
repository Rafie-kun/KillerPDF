using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Syntax;
using Xunit;

namespace KillerPdf.Engine.Tests.Syntax;

public sealed class PdfLexicalCompatibilityTests
{
    [Fact]
    public void RecoverySkipsNonHexBytesWithoutLosingPendingNibbleOrLaterPage()
    {
        byte[] source = Document("<4Z1 4?2 4G>");
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(source);
        PdfArray probe = Assert.IsType<PdfArray>(recovered.Resolve(new PdfIndirectReference(2, 0)));
        Assert.Equal(new byte[] { 0x41, 0x42, 0x40 }, Assert.IsType<PdfString>(probe[0]).Bytes.ToArray());
        AssertFollowingObjectsAndPage(recovered, probe);

        PdfDocument strict = PdfDocument.Open(source);
        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() => strict.Resolve(new PdfIndirectReference(2, 0)));
        Assert.Equal(Encoding.Latin1.GetString(source).IndexOf('Z'), error.Offset);
    }

    [Theory]
    [InlineData("/Bad#2GName", "Bad#2GName")]
    [InlineData("/Trailing#", "Trailing#")]
    [InlineData("/Truncated#A", "Truncated#A")]
    [InlineData("/Mixed#2G#20Name", "Mixed#2G Name")]
    public void RecoveryPreservesMalformedNameEscapeAndLaterPage(string token, string expected)
    {
        byte[] source = Document(token);
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(source);
        PdfArray probe = Assert.IsType<PdfArray>(recovered.Resolve(new PdfIndirectReference(2, 0)));
        Assert.Equal(expected, Assert.IsType<PdfName>(probe[0]).ValueAsLatin1());
        AssertFollowingObjectsAndPage(recovered, probe);

        PdfDocument strict = PdfDocument.Open(source);
        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() => strict.Resolve(new PdfIndirectReference(2, 0)));
        Assert.Equal(Encoding.Latin1.GetString(source).IndexOf('#'), error.Offset);
    }

    [Theory]
    [InlineData("<Z41>", "A")]
    [InlineData("<4Z1>", "A")]
    [InlineData("<41Z>", "A")]
    [InlineData("<4Z>", "@")]
    [InlineData("<Z?>", "")]
    [InlineData("<4/1>", "A")]
    public void RecoverySkipsOnlyOffendingHexBytes(string token, string expected)
    {
        byte[] source = Document(token);
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(source);
        PdfArray probe = Assert.IsType<PdfArray>(recovered.Resolve(new PdfIndirectReference(2, 0)));
        Assert.Equal(Encoding.Latin1.GetBytes(expected), Assert.IsType<PdfString>(probe[0]).Bytes.ToArray());
        AssertFollowingObjectsAndPage(recovered, probe);
        Assert.Throws<PdfSyntaxException>(() => PdfDocument.Open(source).Resolve(new PdfIndirectReference(2, 0)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidEscapesAndOddHexPaddingRemainUnchanged(bool compatibility)
    {
        byte[] source = Document("[/Foo#bar /A#20Name#23Tag#2f <41 4>]");
        PdfDocument document = compatibility ? PdfDocument.OpenWithCompatibilityRecovery(source) : PdfDocument.Open(source);
        PdfArray probe = Assert.IsType<PdfArray>(document.Resolve(new PdfIndirectReference(2, 0)));
        PdfArray values = Assert.IsType<PdfArray>(probe[0]);
        Assert.Equal(new byte[] { 0x46, 0x6f, 0x6f, 0xba, 0x72 }, Assert.IsType<PdfName>(values[0]).Bytes.ToArray());
        Assert.Equal("A Name#Tag/", Assert.IsType<PdfName>(values[1]).ValueAsLatin1());
        Assert.Equal(new byte[] { 0x41, 0x40 }, Assert.IsType<PdfString>(values[2]).Bytes.ToArray());
        AssertFollowingObjectsAndPage(document, probe);
    }

    [Fact]
    public void RecoveredContentPreservesOperatorByteOffsetsAndFollowingInstructions()
    {
        byte[] source = "  <4Z1> Tj /Bad#2G Do 2 w"u8.ToArray();
        var instructions = PdfContentStreamReader.Read(source, compatibilityRecovery: true);
        Assert.Equal(new[] { "Tj", "Do", "w" }, instructions.Select(value => value.Operator));
        Assert.Equal(new[] { 8, 19, 24 }, instructions.Select(value => value.Offset));
        Assert.Equal("A"u8.ToArray(), Assert.IsType<PdfString>(Assert.Single(instructions[0])).Bytes.ToArray());
        Assert.Equal("Bad#2G", Assert.IsType<PdfName>(Assert.Single(instructions[1])).ValueAsLatin1());
        Assert.Equal(2, Assert.IsType<PdfInteger>(Assert.Single(instructions[2])).Value);
        Assert.Equal(4, Assert.Throws<PdfSyntaxException>(() => PdfContentStreamReader.Read(source)).Offset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnterminatedHexStringStillFailsAtOpeningByte(bool compatibility)
    {
        var parser = new PdfObjectParser("  <41"u8.ToArray(), 2, allowDuplicateDictionaryKeys: compatibility);
        Assert.Equal(2, Assert.Throws<PdfSyntaxException>(() => parser.ParseSingleObject()).Offset);
    }

    private static void AssertFollowingObjectsAndPage(PdfDocument document, PdfArray probe)
    {
        Assert.Equal(3, probe.Count);
        Assert.Equal("After", Assert.IsType<PdfName>(probe[1]).ValueAsLatin1());
        Assert.Equal(42, Assert.IsType<PdfInteger>(probe[2]).Value);
        var reader = new PdfPageContentReader(document);
        Assert.Equal(1, reader.PageCount);
        Assert.Equal(new[] { "rg", "re", "f" }, reader.ReadInstructions(0).Select(instruction => instruction.Operator));
    }

    private static byte[] Document(string token)
    {
        const string content = "1 0 0 rg 4 4 24 24 re f";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 3 0 R /Probe 2 0 R >>",
            $"[{token} /After 42]",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 32 32] /Resources << >> /Contents 5 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
        ];
        var source = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(source.Length);
            source.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = source.Length;
        source.Append("xref\n0 6\n0000000000 65535 f \n");
        foreach (int offset in offsets) source.Append($"{offset:D10} 00000 n \n");
        source.Append($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(source.ToString());
    }
}
