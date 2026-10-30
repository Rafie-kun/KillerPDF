using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfAttachmentMacroTests
{
    [Fact]
    public void AuditStepReportsUnsafeContentWithoutChangingTheDocument()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddAttachment("notes.txt", new byte[] { (byte)'M', (byte)'Z', 0, 0 })
            .Build();
        PdfMacroStep step = PdfAttachmentMacro.AuditStep();

        PdfPreflightFinding finding = Assert.Single(
            PdfAttachmentMacro.Inspect(step, source));
        ReadOnlyMemory<byte> output = PdfAttachmentMacro.Execute(step, source);

        Assert.Equal(PdfMacroOperation.AuditAttachments, step.Operation);
        Assert.Equal("Attachment.ExecutableContent", finding.Code);
        Assert.Equal(source, output.ToArray());
    }

    [Fact]
    public void RemoveStepRemovesDocumentAndPagePlacedAttachments()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddAttachment("private.txt", "private"u8.ToArray())
            .AddFileAttachmentAnnotation(0, 20, 20, 24, "private.txt")
            .Build();
        PdfMacroStep step = PdfAttachmentMacro.RemoveStep();

        PdfDocument sanitized = PdfDocument.Open(
            PdfAttachmentMacro.Execute(step, source));

        Assert.Equal(PdfMacroOperation.RemoveAttachments, step.Operation);
        Assert.Empty(PdfAttachmentReader.Read(sanitized));
        Assert.Empty(PdfAttachmentReader.ReadPageAnnotations(sanitized, 0));
        Assert.Throws<ArgumentException>(() => PdfAttachmentMacro.Execute(
            new PdfMacroStep(PdfMacroOperation.Save), source));
    }

    [Fact]
    public void MetadataEditStepsRoundTripWithoutAttachmentPayloads()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("notes.txt", "payload"u8.ToArray(),
                description: "Old description")
            .Build();
        var macro = PdfMacro.FromJson(new PdfMacro("Edit attachments", [
            PdfAttachmentMacro.RenameStep("notes.txt", "evidence.txt"),
            PdfAttachmentMacro.DescriptionStep("evidence.txt", "Case notes"),
            PdfAttachmentMacro.ClassificationStep("evidence.txt", "text/plain",
                PdfAssociatedFileRelationship.Supplement)
        ]).ToJson());

        ReadOnlyMemory<byte> output = source;
        foreach (PdfMacroStep step in macro.Steps)
            output = PdfAttachmentMacro.Execute(step, output);
        PdfAttachmentInfo attachment = Assert.Single(
            PdfAttachmentReader.Read(PdfDocument.Open(output)));

        Assert.Equal("evidence.txt", attachment.FileName);
        Assert.Equal("Case notes", attachment.Description);
        Assert.Equal("text/plain", attachment.MimeType);
        Assert.Equal(PdfAssociatedFileRelationship.Supplement,
            attachment.Relationship);
        Assert.Equal("payload"u8.ToArray(), attachment.Data.ToArray());
        Assert.DoesNotContain("payload", macro.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void RemoveStepStripsEveryAttachmentWhenListedNamesRepeat()
    {
        (byte[] source, _, _) = PdfAttachmentReaderTests.KeyedScenario("duplicate");

        PdfDocument sanitized = PdfDocument.Open(
            PdfAttachmentMacro.Execute(PdfAttachmentMacro.RemoveStep(), source));

        Assert.Empty(PdfAttachmentReader.Read(sanitized));
    }

    [Fact]
    public void EditStepsPickTheListedNameAndRefuseOneSeveralFilesShare()
    {
        (byte[] crossed, _, _) = PdfAttachmentReaderTests.KeyedScenario("cross");
        (byte[] repeated, _, _) = PdfAttachmentReaderTests.KeyedScenario("duplicate");

        // "Y" is the name listed for the file at key X, even though another file is stored under key Y.
        PdfAttachmentInfo[] renamed = [.. PdfAttachmentReader.Read(PdfDocument.Open(
            PdfAttachmentMacro.Execute(PdfAttachmentMacro.RenameStep("Y", "Q.txt"), crossed)))];
        Assert.Equal(["PAYLOAD1", "PAYLOAD2"], renamed.OrderBy(item => item.FileName, StringComparer.Ordinal)
            .Select(item => System.Text.Encoding.ASCII.GetString(item.Data.Span)));
        Assert.Equal(["Q.txt", "Z"], renamed.Select(item => item.FileName).OrderBy(name => name, StringComparer.Ordinal));

        ArgumentException error = Assert.Throws<ArgumentException>(() => PdfAttachmentMacro.Execute(
            PdfAttachmentMacro.DescriptionStep("dup.txt", "Note"), repeated));
        Assert.Contains("Several attachments are named dup.txt.", error.Message);
    }
}
