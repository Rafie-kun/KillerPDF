using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Security;

// A PDF opened with its user password carries the author's permissions, as in Acrobat. Editors that
// check their own finer permission keep doing so; every other change written directly through the
// incremental writer needs the document-modification permission. The owner password lifts them all.
public sealed class PdfUserPermissionEnforcementTests
{
    [Fact]
    public void DirectUpdatesNeedTheModifyPermissionUnderTheUserPassword()
    {
        byte[] restricted = Encrypt(modify: false);

        var error = Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalUpdateBuilder(PdfDocument.Open(restricted, "reader")));
        Assert.Contains("does not permit", error.Message, StringComparison.Ordinal);
        _ = new PdfIncrementalUpdateBuilder(PdfDocument.Open(restricted, "owner"));
        _ = new PdfIncrementalUpdateBuilder(PdfDocument.Open(Encrypt(modify: true), "reader"));
    }

    [Fact]
    public void AddingALayerToAUserRestrictedFileIsRefusedButTheOwnerCanAddIt()
    {
        byte[] restricted = Encrypt(modify: false);

        Assert.Throws<InvalidOperationException>(() =>
            PdfOptionalContentEditor.AddGroup(PdfDocument.Open(restricted, "reader"), "Notes"));
        PdfDocument layered = PdfDocument.Open(
            PdfOptionalContentEditor.AddGroup(PdfDocument.Open(restricted, "owner"), "Notes"), "owner");
        Assert.Contains(PdfOptionalContentReader.Read(layered).Groups, group => group.Name == "Notes");
    }

    [Fact]
    public void EditorsWithTheirOwnPermissionChecksStillAllowWhatTheyPermit()
    {
        byte[] restricted = Encrypt(modify: false);

        PdfDocument rotated = PdfDocument.Open(new PdfIncrementalPageEditor(PdfDocument.Open(restricted, "reader"))
            .RotateClockwise(0).Build(), "reader");
        Assert.Equal(90, PdfPageInformation.Read(rotated)[0].Rotation);
        PdfDocument annotated = PdfDocument.Open(new PdfIncrementalAnnotationEditor(PdfDocument.Open(restricted, "reader"))
            .AddTextNote(0, 20, 30, "allowed note").Build(), "reader");
        Assert.Equal("allowed note", Assert.Single(PdfCommentReader.Read(annotated)).Contents);
    }

    // Many published forms open without a password, forbid editing, and allow filling. As in Acrobat,
    // filling them works and keeps their encryption and permissions; ISO 32000-1 lets either the
    // form-filling or the annotation permission allow it.
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void FillingAFormNeedsOnlyTheFillOrAnnotationPermission(bool fill, bool annotate, bool allowed)
    {
        byte[] form = PdfDocumentEncryptionWriter.EncryptWithPasswords(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(200, 300)
                .AddTextField(0, "name", 20, 20, 160, 24, string.Empty, 11).Build()),
            new PdfPasswordEncryptionOptions
            {
                UserPassword = string.Empty,
                OwnerPassword = "owner",
                AllowDocumentModification = false,
                AllowFormFilling = fill,
                AllowAnnotationModification = annotate
            });
        var editor = new PdfIncrementalPageEditor(PdfDocument.Open(form, string.Empty))
            .SetTextFieldValue("name", "Filled in");

        if (!allowed)
        {
            Assert.Contains("does not permit", Assert.Throws<InvalidOperationException>(() => editor.Build()).Message,
                StringComparison.Ordinal);
            return;
        }
        PdfDocument filled = PdfDocument.Open(editor.Build(), string.Empty);
        Assert.True(filled.IsEncrypted);
        Assert.False(filled.DeclaredPermissions!.AllowDocumentModification);
        Assert.Equal("Filled in", Assert.Single(PdfFormWidgetReader.ReadPage(filled, 0)).Value);
    }

    [Fact]
    public void FillingDoesNotLetStructuralFieldChangesThrough()
    {
        byte[] form = PdfDocumentEncryptionWriter.EncryptWithPasswords(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(200, 300)
                .AddTextField(0, "name", 20, 20, 160, 24, string.Empty, 11).Build()),
            new PdfPasswordEncryptionOptions
            {
                UserPassword = string.Empty, OwnerPassword = "owner",
                AllowDocumentModification = false, AllowFormFilling = true, AllowAnnotationModification = false
            });

        Assert.Throws<InvalidOperationException>(() =>
            new PdfIncrementalPageEditor(PdfDocument.Open(form, string.Empty)).RemoveFormField("name").Build());
    }

    private static byte[] Encrypt(bool modify) =>
        PdfDocumentEncryptionWriter.EncryptWithPasswords(
            PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(200, 300).Build()),
            new PdfPasswordEncryptionOptions
            {
                UserPassword = "reader",
                OwnerPassword = "owner",
                AllowDocumentModification = modify,
                AllowDocumentAssembly = true,
                AllowAnnotationModification = true
            });
}
