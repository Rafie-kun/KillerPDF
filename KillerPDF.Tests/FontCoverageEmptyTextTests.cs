using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class FontCoverageEmptyTextTests
    {
        // Deleting a paragraph draws nothing, so its font being missing must not stop it.
        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        public void AFamilyThatIsNotInstalledStillGetsAFontForEmptyText(string text) =>
            Assert.NotNull(PdfEngineIntegration.LoadTextFont("NoSuchFamilyInstalled", text, false, false));

        [Fact]
        public void AnInstalledFamilyStaysForEmptyText() =>
            Assert.Equal("Arial", PdfEngineIntegration.LoadTextFont("Arial", "", false, false)?.Family);
    }
}
