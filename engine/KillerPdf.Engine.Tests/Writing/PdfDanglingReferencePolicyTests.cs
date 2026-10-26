using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

public sealed class PdfDanglingReferencePolicyTests
{
    [Theory]
    [InlineData("owner", PdfCrossReferenceFormat.Table)]
    [InlineData("owner", PdfCrossReferenceFormat.Stream)]
    [InlineData("user", PdfCrossReferenceFormat.Table)]
    [InlineData("user", PdfCrossReferenceFormat.Stream)]
    public void WriteWithReport_RecoveryPreservesEncryptionAndRequiredIdentifiers(
        string password, PdfCrossReferenceFormat format)
    {
        byte[] encrypted = EncryptedSource(allowModification: true);
        PdfDocument source = PdfDocument.Open(encrypted, password);

        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(source,
            new PdfDocumentWriteOptions
            {
                ReplaceDanglingReferences = true,
                PreserveDocumentIdentifiers = false,
                CrossReferenceFormat = format,
                UseObjectStreams = format == PdfCrossReferenceFormat.Stream,
                CompressStructuralStreams = format == PdfCrossReferenceFormat.Stream
            });

        byte[] output = result.Data.ToArray();
        Assert.Empty(result.ReferenceRepairs);
        Assert.Equal(-1, output.AsSpan().IndexOf("policy protected content"u8));
        Assert.ThrowsAny<CryptographicException>(() => PdfDocument.Open(output, "wrong"));
        PdfDocument reopened = PdfDocument.Open(output, password);
        Assert.True(reopened.IsEncrypted);
        Assert.True(reopened.IsDecrypted);
        Assert.Equal("policy protected content", new PdfPageContentReader(reopened).Read(0).Text);
        Assert.Equal(Identifiers(source), Identifiers(reopened));
        Assert.Equal(source.PasswordAuthenticationRole, reopened.PasswordAuthenticationRole);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteWithReport_RecoveryRequiresAuthenticationEvenWhenRemovingEncryption(
        bool removeEncryption)
    {
        PdfDocument locked = PdfDocument.Open(EncryptedSource(allowModification: true));
        Assert.True(locked.IsEncrypted);
        Assert.False(locked.IsDecrypted);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfDocumentWriter.WriteWithReport(locked, new PdfDocumentWriteOptions
            {
                ReplaceDanglingReferences = true,
                RemoveEncryption = removeEncryption
            }));

        Assert.Contains("must be opened with a password", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteWithReport_RecoveryRejectsReadOnlyUserRewriteAndEncryptionRemoval(
        bool removeEncryption)
    {
        PdfDocument restricted = PdfDocument.Open(
            EncryptedSource(allowModification: false), "user");
        Assert.Equal(PdfPasswordAuthenticationRole.User, restricted.PasswordAuthenticationRole);
        Assert.False(restricted.DeclaredPermissions!.AllowDocumentModification);
        Assert.Equal("policy protected content", new PdfPageContentReader(restricted).Read(0).Text);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfDocumentWriter.WriteWithReport(restricted, new PdfDocumentWriteOptions
            {
                ReplaceDanglingReferences = true,
                RemoveEncryption = removeEncryption
            }));

        Assert.Contains("user password does not permit", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteWithReport_RecoveryAllowsOwnerRemovalAndHonorsIdentifierChoice(
        bool preserveIdentifiers)
    {
        PdfDocument owner = PdfDocument.Open(EncryptedSource(allowModification: false), "owner");
        Assert.Equal(PdfPasswordAuthenticationRole.Owner, owner.PasswordAuthenticationRole);
        Assert.False(owner.DeclaredPermissions!.AllowDocumentModification);

        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(owner,
            new PdfDocumentWriteOptions
            {
                ReplaceDanglingReferences = true,
                RemoveEncryption = true,
                PreserveDocumentIdentifiers = preserveIdentifiers
            });

        PdfDocument reopened = PdfDocument.Open(result.Data);
        Assert.Empty(result.ReferenceRepairs);
        Assert.False(reopened.IsEncrypted);
        Assert.False(reopened.Trailer.ContainsKey(Name("Encrypt")));
        Assert.Equal("policy protected content", new PdfPageContentReader(reopened).Read(0).Text);
        if (preserveIdentifiers)
            Assert.Equal(Identifiers(owner), Identifiers(reopened));
        else
            Assert.False(reopened.Trailer.ContainsKey(Name("ID")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteWithReport_RecoveryRequiresExplicitSignatureInvalidation(bool certified)
    {
        PdfDocument signed = PdfDocument.Open(SignedSource(certified));
        Assert.True(Assert.Single(PdfSignatureReader.Read(signed)).IsSigned);
        Assert.Equal(certified, PdfSignatureReader.ReadCertificationPermission(signed).HasValue);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfDocumentWriter.WriteWithReport(signed, new PdfDocumentWriteOptions
            {
                ReplaceDanglingReferences = true
            }));

        Assert.Contains("Set AllowSignatureInvalidation explicitly", error.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WriteWithReport_RecoveryWithConsentClearsSignaturesAndRetainsWidgets(bool certified)
    {
        PdfDocument signed = PdfDocument.Open(SignedSource(certified));
        Assert.True(Assert.Single(PdfSignatureReader.Read(signed)).IsSigned);
        PdfFormWidgetInfo before = Assert.Single(PdfFormWidgetReader.ReadPage(signed, 0),
            widget => widget.FieldName == "approval");

        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(signed,
            new PdfDocumentWriteOptions
            {
                ReplaceDanglingReferences = true,
                AllowSignatureInvalidation = true
            });

        PdfDocument reopened = PdfDocument.Open(result.Data);
        Assert.Empty(result.ReferenceRepairs);
        PdfSignatureInfo signature = Assert.Single(PdfSignatureReader.Read(reopened));
        Assert.Equal("approval", signature.FieldName);
        Assert.False(signature.IsSigned);
        Assert.Empty(signature.Contents.ToArray());
        Assert.Null(signature.ByteRange);
        Assert.Null(PdfSignatureReader.ReadCertificationPermission(reopened));
        IReadOnlyList<PdfFormWidgetInfo> widgets = PdfFormWidgetReader.ReadPage(reopened, 0);
        Assert.Equal(2, widgets.Count);
        PdfFormWidgetInfo approval = Assert.Single(widgets, widget => widget.FieldName == "approval");
        Assert.Equal(PdfFormFieldKind.Signature, approval.FieldKind);
        Assert.Equal(string.Empty, approval.Value);
        Assert.Equal((before.Left, before.Bottom, before.Right, before.Top),
            (approval.Left, approval.Bottom, approval.Right, approval.Top));
        Assert.Equal("ALPHA", Assert.Single(widgets, widget => widget.FieldName == "customer").Value);
        Assert.Equal("signature policy content", new PdfPageContentReader(reopened).Read(0).Text);
    }

    private static byte[] SignedSource(bool certified) =>
        PdfDetachedSignatureWriter.Sign(PdfDocument.Open(new PdfDocumentBuilder()
                .AddPage(300, 400, Text("signature policy content"))
                .AddTextField(0, "customer", 20, 80, 120, 30, "ALPHA").Build()),
            _ => [1], new PdfSignatureOptions
            {
                FieldName = "approval",
                ReservedSignatureSize = 16,
                SigningTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                CertificationPermission = certified
                    ? PdfSignatureCertificationPermission.FormFillingAndSignatures : null
            });

    private static byte[] EncryptedSource(bool allowModification) =>
        new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = "user",
                OwnerPassword = "owner",
                AllowDocumentModification = allowModification
            })
            .AddPage(300, 400, Text("policy protected content"))
            .Build();

    private static PdfContentStreamBuilder Text(string text) =>
        new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .MoveText(20, 30).ShowLatin1Text(text).EndText();

    private static string[] Identifiers(PdfDocument document) =>
        Assert.IsType<PdfArray>(document.Trailer[Name("ID")])
            .Select(item => Convert.ToHexString(Assert.IsType<PdfString>(item).Bytes.Span))
            .ToArray();

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
