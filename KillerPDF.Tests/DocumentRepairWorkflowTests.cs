using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Parsing;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class DocumentRepairWorkflowTests
{
    [Fact]
    public void DocumentRepairPreservesContentAndRejectsInvalidLanguageWithoutWriting()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(240, 200,
            new PdfContentStreamBuilder().Rectangle(10, 20, 30, 40).Stroke()).Build();
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-repair-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, source);
            Assert.Throws<ArgumentException>(() => PdfEngineIntegration.ApplyDocumentRepair(path, document =>
                PdfPreflightCorrectionPlan.SetDocumentLanguage(document, "not a language").Apply()));
            Assert.Equal(source, File.ReadAllBytes(path));
            PdfEngineIntegration.ApplyDocumentRepair(path, document =>
                PdfPreflightCorrectionPlan.SetDocumentLanguage(document, "en-US").Apply());
            PdfDocument result = PdfDocument.Open(File.ReadAllBytes(path));
            Assert.Equal("en-US", PdfDocumentInformation.Read(result).Language);
            Assert.Single(new PdfPageContentReader(result).Read(0).Paths);
            Assert.DoesNotContain(PdfAccessibilityInspector.Inspect(result).Findings,
                finding => finding.Code == PdfAccessibilityFindingCode.MissingDocumentLanguage);
        }
        finally { File.Delete(path); }
    }
}
