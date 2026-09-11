using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Syntax;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfDocumentInformationTests
{
    [Fact]
    public void Read_ReturnsMetadataVersionAndPageCount()
    {
        byte[] bytes = new PdfDocumentBuilder(PdfVersion.Pdf20)
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = "Technical overview",
                Author = "Steve",
                Subject = "The KillerPDF.Engine",
                Keywords = "PDF 2.0, PDF/A",
                Creator = "Tests",
                Producer = "The KillerPDF.Engine",
                Language = "en-US",
                CreationDate = new DateTimeOffset(2026, 8, 24, 10, 11, 12, TimeSpan.FromHours(-7)),
                ModificationDate = new DateTimeOffset(2026, 8, 24, 11, 12, 13, TimeSpan.Zero),
                Trapped = PdfTrappedStatus.False
            })
            .AddBlankPage()
            .AddBlankPage()
            .Build();

        PdfDocumentInformation info = PdfDocumentInformation.Read(PdfDocument.Open(bytes));

        Assert.Equal("Technical overview", info.Title);
        Assert.Equal("Steve", info.Author);
        Assert.Equal("The KillerPDF.Engine", info.Subject);
        Assert.Equal("PDF 2.0, PDF/A", info.Keywords);
        Assert.Equal("Tests", info.Creator);
        Assert.Equal("The KillerPDF.Engine", info.Producer);
        Assert.Equal("en-US", info.Language);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 10, 11, 12, TimeSpan.FromHours(-7)), info.CreationDate);
        Assert.Equal(new DateTimeOffset(2026, 8, 24, 11, 12, 13, TimeSpan.Zero), info.ModificationDate);
        Assert.Equal(PdfTrappedStatus.False, info.Trapped);
        Assert.Equal(PdfVersion.Pdf20, info.Version);
        Assert.Equal(2, info.PageCount);
    }

    [Fact]
    public void Read_AllowsMissingInformationDictionary()
    {
        byte[] bytes = new PdfDocumentBuilder().AddBlankPage().Build();

        PdfDocumentInformation info = PdfDocumentInformation.Read(PdfDocument.Open(bytes));

        Assert.Null(info.Title);
        Assert.Null(info.Author);
        Assert.Equal(1, info.PageCount);
    }
}
