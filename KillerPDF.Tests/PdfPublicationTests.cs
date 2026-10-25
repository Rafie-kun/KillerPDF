using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfPublicationTests
{
    [Fact]
    public void ExchangeReplacementPreservesExistingFileMetadata()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-publication-{Guid.NewGuid():N}.pdf");
        const string metadata = "Existing destination metadata";
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage().Build());
            File.WriteAllText(path + ":publication-test", metadata);
            byte[] replacement = new PdfDocumentBuilder().AddBlankPage().AddBlankPage().Build();

            PdfEngineIntegration.WriteExchangeFile(path, replacement);

            Assert.Equal(replacement, File.ReadAllBytes(path));
            Assert.Equal(2, PdfDocumentInformation.Read(PdfDocument.Open(File.ReadAllBytes(path))).PageCount);
            Assert.Equal(metadata, File.ReadAllText(path + ":publication-test"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AnnotationBurnPreservesExistingFileMetadata()
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-burn-publication-{Guid.NewGuid():N}.pdf");
        const string metadata = "Existing annotation destination metadata";
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage().Build());
            File.WriteAllText(path + ":publication-test", metadata);

            var ink = new InkAnnotation
            {
                PageIndex = 0, StrokeWidth = 6,
                Points = [new System.Windows.Point(100, 100), new System.Windows.Point(120, 120)]
            };
            PdfEngineBurn.Burn(path, new Dictionary<int, List<PageAnnotation>> { [0] = [ink] },
                new Dictionary<int, (int w, int h)> { [0] = (612, 792) });

            Assert.Equal(1, PdfDocumentInformation.Read(PdfDocument.Open(File.ReadAllBytes(path))).PageCount);
            Assert.Equal(metadata, File.ReadAllText(path + ":publication-test"));
        }
        finally { File.Delete(path); }
    }
}
