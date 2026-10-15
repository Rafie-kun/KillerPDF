using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;
using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// A PDF whose owner password forbids editing opens read-only and only becomes editable with the
// owner password, the way Acrobat treats it.
public sealed class OwnerRestrictionTests
{
    private static string WriteOwnerRestricted()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-owner-unlock-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder()
            .SetPasswordEncryption(new PdfPasswordEncryptionOptions
            {
                UserPassword = string.Empty,
                OwnerPassword = "owner",
                AllowDocumentModification = false
            })
            .AddBlankPage(320, 480)
            .Build());
        return path;
    }

    [Fact]
    public void OpeningWithoutThePassword_DoesNotStripTheRestriction()
    {
        string source = WriteOwnerRestricted();
        string output = source + ".unlocked.pdf";
        try
        {
            Assert.False(PdfEngineIntegration.TryRemoveEncryptionWithoutPassword(source, output));
            Assert.True(PdfEngineIntegration.CanReadWithoutPassword(source));
        }
        finally
        {
            File.Delete(source);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Fact]
    public void TheOwnerPassword_WritesAnUnrestrictedCopy()
    {
        string source = WriteOwnerRestricted();
        string output = source + ".unlocked.pdf";
        try
        {
            PdfEngineIntegration.RemoveEncryption(source, output, "owner");

            PdfDocument unlocked = PdfDocument.Open(File.ReadAllBytes(output));
            Assert.False(unlocked.IsEncrypted);
            Assert.Single(PdfPageInformation.Read(unlocked));
        }
        finally
        {
            File.Delete(source);
            if (File.Exists(output)) File.Delete(output);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("wrong")]
    public void AnyOtherPassword_LeavesItRestricted(string password)
    {
        string source = WriteOwnerRestricted();
        string output = source + ".unlocked.pdf";
        try
        {
            Assert.ThrowsAny<Exception>(() => PdfEngineIntegration.RemoveEncryption(source, output, password));
        }
        finally
        {
            File.Delete(source);
            if (File.Exists(output)) File.Delete(output);
        }
    }
}
