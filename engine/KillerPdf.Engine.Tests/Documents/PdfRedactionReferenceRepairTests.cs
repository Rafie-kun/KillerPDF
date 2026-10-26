using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfRedactionReferenceRepairTests
{
    private const string Sentinel = "SensitiveRemovedKey78642";

    [Fact]
    public void ContentRedactionReportsRepairsWithoutExportingSensitiveDictionaryKeys()
    {
        byte[] bytes = new PdfDocumentBuilder().AddPage(200, 200,
            new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(20, 150).ShowLatin1Text("Remove").MoveText(0, -100)
                .ShowLatin1Text("Keep").EndText()).Build();
        PdfDocument source = DamageTrailer(bytes);
        byte[] original = source.Source.ToArray();

        PdfContentRedactionResult result = PdfContentRedaction.Apply(source,
            [new PdfRedactionRegion(0, new PdfContentBounds(0, 130, 100, 175))],
            new PdfContentRedactionOptions { ProhibitedText = ["Remove"] });

        Assert.True(result.Verification.Succeeded);
        Assert.Equal("Keep", new PdfPageContentReader(PdfDocument.Open(result.Document)).Read(0).Text);
        Assert.DoesNotContain("Remove", Encoding.Latin1.GetString(result.Document.Span));
        Assert.Equal(original, source.Source.ToArray());
        Assert.DoesNotContain(Sentinel, result.Verification.ToText());
        Assert.DoesNotContain(Sentinel, result.Verification.ToJson());
        using JsonDocument report = JsonDocument.Parse(result.Verification.ToJson());
        JsonElement repair = Assert.Single(report.RootElement.GetProperty("referenceRepairs").EnumerateArray());
        Assert.Equal(999, repair.GetProperty("targetObjectNumber").GetInt32());
        Assert.Equal("missing", repair.GetProperty("reason").GetString());
        Assert.Equal("removeDictionaryEntry", repair.GetProperty("action").GetString());
        Assert.Equal(999, Assert.Single(result.Verification.ReferenceRepairs).TargetObjectNumber);
    }

    [Fact]
    public void CommentRemovalRepairsTrailerBeforeEditingAndKeepsReportPrivate()
    {
        PdfDocument clean = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        PdfDocument source = DamageTrailer(new PdfIncrementalAnnotationEditor(clean)
            .AddTextNote(0, 20, 30, "Remove", name: "remove")
            .AddTextNote(0, 60, 70, "Keep", name: "keep").Build());
        byte[] original = source.Source.ToArray();
        PdfRedactionReview review = PdfRedactionReview.FromComments(source).Exclude("comment:0:1");

        PdfCommentRedactionResult result = PdfPermanentRedaction.ApplyReviewedComments(source, review);

        Assert.Equal("Keep", Assert.Single(PdfCommentReader.Read(PdfDocument.Open(result.Document))).Contents);
        Assert.DoesNotContain("Remove", Encoding.Latin1.GetString(result.Document.Span));
        Assert.Equal(original, source.Source.ToArray());
        Assert.DoesNotContain(Sentinel, result.ToJson());
        using JsonDocument report = JsonDocument.Parse(result.ToJson());
        Assert.NotEmpty(report.RootElement.GetProperty("referenceRepairs").EnumerateArray());
    }

    [Fact]
    public void AttachmentRemovalRepairsTrailerBeforeEditingAndKeepsReportPrivate()
    {
        PdfDocument source = DamageTrailer(new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("remove.txt", "Remove"u8.ToArray())
            .AddAttachment("keep.txt", "Keep"u8.ToArray()).Build());
        byte[] original = source.Source.ToArray();
        PdfRedactionReview all = PdfRedactionReview.FromAttachments(source);
        PdfRedactionReview review = all.Exclude(Assert.Single(all.Matches,
            match => match.Text == "keep.txt").Id);

        PdfAttachmentRedactionResult result = PdfPermanentRedaction.ApplyReviewedAttachments(source, review);

        Assert.Equal("keep.txt", Assert.Single(PdfAttachmentReader.Read(PdfDocument.Open(result.Document))).FileName);
        Assert.DoesNotContain("Remove", Encoding.Latin1.GetString(result.Document.Span));
        Assert.Equal(original, source.Source.ToArray());
        Assert.DoesNotContain(Sentinel, result.ToJson());
        using JsonDocument report = JsonDocument.Parse(result.ToJson());
        Assert.NotEmpty(report.RootElement.GetProperty("referenceRepairs").EnumerateArray());
    }

    [Fact]
    public void EmptyReviewedRemovalPreservesDamagedSourceWithoutRepairs()
    {
        PdfDocument source = DamageTrailer(new PdfDocumentBuilder().AddBlankPage().Build());
        PdfCommentRedactionResult comments = PdfPermanentRedaction.ApplyReviewedComments(
            source, PdfRedactionReview.FromComments(source));
        PdfAttachmentRedactionResult attachments = PdfPermanentRedaction.ApplyReviewedAttachments(
            source, PdfRedactionReview.FromAttachments(source));

        Assert.Equal(source.Source.ToArray(), comments.Document.ToArray());
        Assert.Equal(source.Source.ToArray(), attachments.Document.ToArray());
        Assert.Empty(comments.ReferenceRepairs);
        Assert.Empty(attachments.ReferenceRepairs);
    }

    [Fact]
    public void DamagedEncryptedRemovalRequiresCredentialsWithoutRemovingEncryption()
    {
        PdfDocument plain = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("remove.txt", "Remove"u8.ToArray()).Build());
        PdfDocument damaged = DamageTrailer(PdfDocumentEncryptionWriter.EncryptWithPasswords(plain,
            new PdfPasswordEncryptionOptions { UserPassword = "open", OwnerPassword = "owner" }));
        PdfDocument source = PdfDocument.Open(damaged.Source, "owner");
        byte[] original = source.Source.ToArray();

        NotSupportedException error = Assert.Throws<NotSupportedException>(() =>
            PdfPermanentRedaction.ApplyReviewedAttachments(source, PdfRedactionReview.FromAttachments(source)));

        Assert.Contains("credentials", error.Message);
        Assert.True(source.IsEncrypted);
        Assert.Equal(original, source.Source.ToArray());
        Assert.Throws<NotSupportedException>(() => PdfContentRedaction.Apply(source,
            [new PdfRedactionRegion(0, new PdfContentBounds(0, 0, 20, 20))]));
    }

    private static PdfDocument DamageTrailer(byte[] bytes)
    {
        string text = Encoding.Latin1.GetString(bytes);
        int start = text.LastIndexOf("startxref", StringComparison.Ordinal);
        int end = text.LastIndexOf(">>", start, StringComparison.Ordinal);
        return PdfDocument.Open(Encoding.Latin1.GetBytes(text.Insert(end, $" /{Sentinel} 999 0 R ")));
    }
}
