using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Parsing;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class FormRecognitionWorkflowTests
{
    [Fact]
    public void ReviewedRecognitionAddsOnlyAcceptedRenamedFields()
    {
        var content = new PdfContentStreamBuilder()
            .Rectangle(10, 10, 18, 18).Stroke()
            .Rectangle(40, 10, 140, 20).Stroke();
        byte[] source = new PdfDocumentBuilder().AddPage(240, 200, content).Build();
        PdfFormRecognitionReview review = PdfFormRecognizer.Recognize(PdfDocument.Open(source));
        PdfFormFieldProposal text = Assert.Single(review.Proposals, proposal => proposal.Kind == PdfRecognizedFieldKind.Text);
        PdfFormFieldProposal check = Assert.Single(review.Proposals, proposal => proposal.Kind == PdfRecognizedFieldKind.CheckBox);
        review = review.Accept(text.Id, name: "CustomerName").Reject(check.Id);
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-recognition-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, source);
            PdfEngineIntegration.ApplyRecognizedFields(path, review);
            PdfDocument result = PdfDocument.Open(File.ReadAllBytes(path));
            PdfFormWidgetInfo field = Assert.Single(PdfFormWidgetReader.ReadPage(result, 0));
            Assert.Equal("CustomerName", field.FieldName);
            Assert.Equal(PdfFormFieldKind.Text, field.FieldKind);
            Assert.Equal(2, new PdfPageContentReader(result).Read(0).Paths.Count);
        }
        finally { File.Delete(path); }
    }
}
