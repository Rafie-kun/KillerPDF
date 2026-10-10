using System;
using System.IO;
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

        [Theory]
        [InlineData("ArialNarrow", "arialn.ttf")]
        [InlineData("Arial-Black", "ariblk.ttf")]
        public void WidthAndWeightFamiliesResolveToTheirOwnInstalledFace(string name, string file)
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts", file);
            if (!File.Exists(path)) return;

            byte[]? resolved = new InstalledPdfFontResolver().Resolve(new PdfFontRequest(name, "Adobe", "Standard", false));

            Assert.Equal(File.ReadAllBytes(path), resolved);
        }
    }
}
