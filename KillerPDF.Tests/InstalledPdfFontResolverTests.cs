using KillerPdf.Engine.Fonts;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class InstalledPdfFontResolverTests
    {
        [Theory]
        [InlineData("ArialMT", "Helvetica")]
        [InlineData("ABCDEF+Helvetica-Bold", "Helvetica-Bold")]
        public void NamesForOneStandardFaceShareOneArray(string first, string second)
        {
            var resolver = new InstalledPdfFontResolver();

            byte[]? a = resolver.Resolve(new PdfFontRequest(first, "Adobe", "Standard", false));
            byte[]? b = resolver.Resolve(new PdfFontRequest(second, "Adobe", "Standard", false));

            Assert.NotNull(a);
            Assert.Same(a, b);
        }
    }
}
