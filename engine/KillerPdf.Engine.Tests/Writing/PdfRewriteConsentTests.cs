using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

// A host with no dialog to ask in (a macro step, the PDF 2.0 upgrade) needs a way to say that losing
// signatures and Reader usage rights is wanted. Refusing stays the default, and the refusal names the way.
public sealed class PdfRewriteConsentTests
{
    private const string Setting = "invalidateSignatures";

    [Theory]
    [InlineData(PdfMacroOperation.Optimize)]
    [InlineData(PdfMacroOperation.RemoveAttachments)]
    public void AMacroStepRefusesUsageRightsAndNamesTheSettingThatAllowsIt(PdfMacroOperation operation)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfMacroDispatcher().Execute(new PdfMacroStep(operation), WithUsageRights()));

        Assert.Contains("Reader usage rights", error.Message, StringComparison.Ordinal);
        Assert.Contains(Setting, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowSignatureInvalidation", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfMacroOperation.Optimize)]
    [InlineData(PdfMacroOperation.RemoveAttachments)]
    public void AMacroStepRefusesASignatureAndNamesTheSettingThatAllowsIt(PdfMacroOperation operation)
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfMacroDispatcher().Execute(new PdfMacroStep(operation), Signed()));

        Assert.Contains("signatures", error.Message, StringComparison.Ordinal);
        Assert.Contains(Setting, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowSignatureInvalidation", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(PdfMacroOperation.Optimize)]
    [InlineData(PdfMacroOperation.RemoveAttachments)]
    public void AMacroStepWithTheSettingRewritesAFileWithUsageRightsAndDropsThem(PdfMacroOperation operation)
    {
        PdfMacroOperationResult result = new PdfMacroDispatcher().Execute(
            new PdfMacroStep(operation, Allow("true")), WithUsageRights());

        PdfDocument output = PdfDocument.Open(result.Data);
        Assert.False(PdfPageTree.Read(output).Catalog.ContainsKey(new PdfName("Perms"u8)));
    }

    [Theory]
    [InlineData(PdfMacroOperation.Optimize)]
    [InlineData(PdfMacroOperation.RemoveAttachments)]
    public void AMacroStepWithTheSettingRewritesASignedFileAndClearsTheSignature(PdfMacroOperation operation)
    {
        PdfMacroOperationResult result = new PdfMacroDispatcher().Execute(
            new PdfMacroStep(operation, Allow("true")), Signed());

        Assert.All(PdfSignatureReader.Read(PdfDocument.Open(result.Data)), signature => Assert.False(signature.IsSigned));
    }

    [Theory]
    [InlineData(PdfMacroOperation.Optimize)]
    [InlineData(PdfMacroOperation.RemoveAttachments)]
    public void ASettingOfFalseKeepsTheRefusal(PdfMacroOperation operation)
    {
        Assert.Throws<InvalidOperationException>(() => new PdfMacroDispatcher().Execute(
            new PdfMacroStep(operation, Allow("false")), WithUsageRights()));
    }

    [Theory]
    [InlineData(PdfMacroOperation.Optimize, "maybe")]
    [InlineData(PdfMacroOperation.RemoveAttachments, "")]
    public void ASettingThatIsNotTrueOrFalseIsRejected(PdfMacroOperation operation, string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => new PdfMacroDispatcher().Execute(
            new PdfMacroStep(operation, Allow(value)), WithUsageRights()));
    }

    [Theory]
    [InlineData(PdfMacroOperation.Optimize)]
    [InlineData(PdfMacroOperation.RemoveAttachments)]
    public void AnyOtherSettingIsStillRejected(PdfMacroOperation operation)
    {
        var settings = new Dictionary<string, string> { ["level"] = "9" };

        Assert.ThrowsAny<Exception>(() => new PdfMacroDispatcher().Execute(
            new PdfMacroStep(operation, settings), WithUsageRights()));
    }

    [Fact]
    public void TheAttachmentStepBuilderCarriesTheSetting()
    {
        PdfMacroStep step = PdfAttachmentMacro.RemoveStep(invalidateSignatures: true);

        Assert.Equal(PdfMacroOperation.RemoveAttachments, step.Operation);
        Assert.Equal("true", step.Settings![Setting]);
        Assert.Null(PdfAttachmentMacro.RemoveStep().Settings);
    }

    [Fact]
    public void TheSettingSurvivesSavingAndReadingTheMacro()
    {
        var macro = new PdfMacro("Sharing", [new PdfMacroStep(PdfMacroOperation.Optimize, Allow("true"))]);

        PdfMacro reread = PdfMacro.FromJson(macro.ToJson());

        Assert.Equal("true", reread.Steps[0].Settings![Setting]);
    }

    [Fact]
    public void ThePdf20UpgradeRefusesUsageRightsAndNamesTheOptionThatAllowsIt()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfVersion2Upgrade.Apply(PdfDocument.Open(WithUsageRights())));

        Assert.Contains("Reader usage rights", error.Message, StringComparison.Ordinal);
        Assert.Contains("allowSignatureInvalidation", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePdf20UpgradeWithConsentWritesPdf20WithoutUsageRights()
    {
        byte[] upgraded = PdfVersion2Upgrade.Apply(PdfDocument.Open(WithUsageRights()), allowSignatureInvalidation: true);

        PdfDocument output = PdfDocument.Open(upgraded);
        Assert.Equal(PdfVersion.Pdf20, output.Header.Version);
        Assert.False(PdfPageTree.Read(output).Catalog.ContainsKey(new PdfName("Perms"u8)));
    }

    [Fact]
    public void ThePdf20UpgradeWithConsentClearsASignature()
    {
        byte[] upgraded = PdfVersion2Upgrade.Apply(PdfDocument.Open(Signed()), allowSignatureInvalidation: true);

        Assert.All(PdfSignatureReader.Read(PdfDocument.Open(upgraded)), signature => Assert.False(signature.IsSigned));
    }

    [Fact]
    public void ThePdf20UpgradeStillUpgradesAFileWithNothingToLose()
    {
        byte[] plain = new PdfDocumentBuilder().AddBlankPage().Build();

        PdfDocument output = PdfDocument.Open(PdfVersion2Upgrade.Apply(PdfDocument.Open(plain)));

        Assert.Equal(PdfVersion.Pdf20, output.Header.Version);
    }

    [Fact]
    public void RestatingARefusalReplacesTheEngineSentenceWithTheHostsOwn()
    {
        InvalidOperationException signed = Assert.Throws<InvalidOperationException>(() =>
            PdfDocumentWriter.Write(PdfDocument.Open(Signed())));

        Assert.True(PdfRewriteRefusal.Matches(signed));
        InvalidOperationException restated = PdfRewriteRefusal.Restate(signed, "Add --go to continue.");

        Assert.EndsWith("Add --go to continue.", restated.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AllowSignatureInvalidation", restated.Message, StringComparison.Ordinal);
        Assert.Same(signed, restated.InnerException);
        Assert.False(PdfRewriteRefusal.Matches(new InvalidOperationException("Something else failed.")));
        Assert.False(PdfRewriteRefusal.Matches(new ArgumentException("A full rewrite invalidates existing signatures")));
    }

    private static Dictionary<string, string> Allow(string value) => new() { [Setting] = value };

    private static byte[] Signed() => PdfDetachedSignatureWriter.Sign(
        PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build()),
        _ => [0x30, 0x00], new PdfSignatureOptions { ReservedSignatureSize = 16 });

    // A catalog /Perms with a UR3 signature and nothing else signed, as Acrobat leaves a Reader-extended form.
    private static byte[] WithUsageRights()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /Perms << /UR3 5 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Contents 4 0 R >>",
            "<< /Length 15 >>\nstream\n0 0 10 10 re f \nendstream",
            "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 100 200 100] /Contents <00> "
            + "/Reference [<< /Type /SigRef /TransformMethod /UR3 /TransformParams << /Type /TransformParams /V /2.2 >> >>] >>"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
