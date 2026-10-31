using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class SourceFontKindTests
    {
        [Theory]
        [InlineData("WWUWBR+NimbusRomNo9L-Regu", "Times New Roman")]
        [InlineData("ABCDEF+CMR10", "Times New Roman")]
        [InlineData("SFRM1000", "Times New Roman")]
        [InlineData("MinionPro-Regular", "Times New Roman")]
        [InlineData("ABCDEF+CMTT10", "Consolas")]
        [InlineData("DejaVuSansMono", "Consolas")]
        [InlineData("CourierNewPSMT", "Consolas")]
        [InlineData("ABCDEF+CMSS10", "Arial")]
        [InlineData("Overpass-Regular", "Arial")]
        [InlineData("OpenSans-Regular", "Arial")]
        [InlineData("SourceSansPro-Regular", "Arial")]
        public void AKindOfFontIsToldFromItsName(string name, string first) =>
            Assert.Equal(first, SourceFontKind.Families(name)[0]);

        // A name that says nothing leaves every kind open, and the widths decide.
        [Fact]
        public void AnUnknownNameOffersEveryProportionalKind()
        {
            string[] families = SourceFontKind.Families("ZXTLES+Parisian");
            Assert.Contains("Arial", families);
            Assert.Contains("Times New Roman", families);
            Assert.DoesNotContain("Consolas", families);
        }

        [Fact]
        public void MathCanBeCarriedInEveryProportionalKind()
        {
            Assert.Contains("Cambria Math", SourceFontKind.Families("NimbusRomNo9L-Regu"));
            Assert.Contains("Segoe UI Symbol", SourceFontKind.Families("Arial"));
        }
    }
}
