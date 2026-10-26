using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

public sealed class PdfEncryptionRepairReportTests
{
    [Fact]
    public void PasswordOptInRepairsOptionalCatalogReferenceAndEncryptsLiveText()
    {
        PdfDocument source = Source();
        var options = new PdfPasswordEncryptionOptions
            { UserPassword = "synthetic-open", OwnerPassword = "synthetic-owner", AllowContentCopying = false };
        Assert.Throws<InvalidOperationException>(() =>
            PdfDocumentEncryptionWriter.EncryptWithPasswords(source, options));

        PdfDocumentWriteResult result = PdfDocumentEncryptionWriter.EncryptWithPasswordsWithReport(
            source, options, replaceDanglingReferences: true);

        AssertRepair(result);
        Assert.Equal(-1, result.Data.Span.IndexOf("Keep encrypted text"u8));
        Assert.ThrowsAny<CryptographicException>(() => PdfDocument.Open(result.Data, "wrong"));
        PdfDocument output = PdfDocument.Open(result.Data, "synthetic-open");
        Assert.True(output.IsEncrypted);
        Assert.True(output.IsDecrypted);
        Assert.False(output.DeclaredPermissions!.AllowContentCopying);
        AssertOutput(output);
        Assert.True(PdfDocument.Open(result.Data, "synthetic-owner").IsDecrypted);
        Assert.Throws<InvalidOperationException>(() =>
            PdfDocumentEncryptionWriter.EncryptWithPasswords(source, options));
    }

    [Fact]
    public void RecipientOptInRepairsOptionalCatalogReferenceAndEncryptsLiveText()
    {
        using X509Certificate2 allowed = Certificate("CN=Synthetic repair recipient");
        using X509Certificate2 denied = Certificate("CN=Synthetic other recipient");
        PdfDocument source = Source();
        var options = new PdfCertificateEncryptionOptions { Recipients = [allowed] };
        Assert.Throws<InvalidOperationException>(() =>
            PdfDocumentEncryptionWriter.EncryptForRecipients(source, options));

        PdfDocumentWriteResult result = PdfDocumentEncryptionWriter.EncryptForRecipientsWithReport(
            source, options, replaceDanglingReferences: true);

        AssertRepair(result);
        Assert.Equal(-1, result.Data.Span.IndexOf("Keep encrypted text"u8));
        Assert.ThrowsAny<CryptographicException>(() => PdfDocument.Open(result.Data, denied));
        PdfDocument output = PdfDocument.Open(result.Data, allowed);
        Assert.True(output.IsEncrypted);
        Assert.True(output.IsDecrypted);
        AssertOutput(output);
        Assert.Throws<InvalidOperationException>(() =>
            PdfDocumentEncryptionWriter.EncryptForRecipients(source, options));
    }

    private static X509Certificate2 Certificate(string subject)
    {
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static void AssertRepair(PdfDocumentWriteResult result)
    {
        PdfReferenceRepair repair = Assert.Single(result.ReferenceRepairs);
        Assert.Equal("/Optional", repair.Path);
        Assert.NotNull(repair.OwnerObjectNumber);
        Assert.Equal(0, repair.OwnerGeneration);
        Assert.Equal(999, repair.TargetObjectNumber);
        Assert.Equal(0, repair.TargetGeneration);
        Assert.Equal(PdfReferenceRepairReason.Missing, repair.Reason);
        Assert.Equal(PdfReferenceRepairAction.RemoveDictionaryEntry, repair.Action);
    }

    private static void AssertOutput(PdfDocument output)
    {
        Assert.Equal("Keep encrypted text", new PdfPageContentReader(output).Read(0).Text);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(output.Resolve(
            Assert.IsType<PdfIndirectReference>(output.Trailer[new PdfName("Root"u8)])));
        Assert.False(catalog.ContainsKey(new PdfName("Optional"u8)));
    }

    private static PdfDocument Source()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
            new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(10, 50).ShowLatin1Text("Keep encrypted text").EndText()).Build());
        PdfIndirectReference root = Assert.IsType<PdfIndirectReference>(original.Trailer[new PdfName("Root"u8)]);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(original.Resolve(root));
        var update = new PdfIncrementalUpdateBuilder(original);
        return PdfDocument.Open(update.ReplaceObject(root.ObjectNumber, new PdfDictionary(catalog.Append(
            new KeyValuePair<PdfName, PdfObject>(new PdfName("Optional"u8), new PdfIndirectReference(999, 0)))))
            .Build());
    }
}
