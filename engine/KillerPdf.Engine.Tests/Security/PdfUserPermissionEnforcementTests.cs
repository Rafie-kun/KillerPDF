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
