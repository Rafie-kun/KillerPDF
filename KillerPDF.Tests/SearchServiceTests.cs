using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class SearchServiceTests
    {
        private readonly SearchService _svc = new();

        [Fact]
        public void Search_EmptyQuery_ReturnsEmpty()
        {
            var result = SearchService.Search("irrelevant.pdf", "");
            Assert.Empty(result.ResultPages);
            Assert.Equal(0, result.TotalHits);
        }

        [Fact]
        public void Search_WhitespaceQuery_ReturnsEmpty()
        {
            var result = SearchService.Search("irrelevant.pdf", "   ");
            Assert.Empty(result.ResultPages);
            Assert.Equal(0, result.TotalHits);
        }

        [Fact]
        public void Search_EmptyFilePath_ReturnsEmpty()
        {
            var result = SearchService.Search("", "hello");
            Assert.Empty(result.ResultPages);
            Assert.Equal(0, result.TotalHits);
        }

        [Fact]
        public void Search_MissingFile_Throws()
        {
            // An empty result would tell the user the term is absent from a file that was never read.
            Assert.ThrowsAny<Exception>(() => SearchService.Search(@"C:\does\not\exist.pdf", "hello"));
        }

        [Fact]
        public void Search_UnreadableFile_ThrowsInsteadOfReportingNoHits()
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".pdf");
            try
            {
                System.IO.File.WriteAllText(path, "this is not a pdf, so no text can be read from it");
                Assert.ThrowsAny<Exception>(() => SearchService.Search(path, "pdf"));
            }
            finally { System.IO.File.Delete(path); }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Search_FindsVisibleWordsInAFileOnlyRecoveryCanOpen(bool withoutMediaBox)
        {
            string text = System.Text.Encoding.ASCII.GetString(PdfTextSizeTests.BuildPdf());
            // The renamed key keeps every offset, and the wrong startxref leaves only recovery able to read it.
            if (withoutMediaBox) text = text.Replace("/MediaBox", "/MediaBoy");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"startxref\n\d+", "startxref\n7");
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(text);
            Assert.ThrowsAny<Exception>(() => KillerPdf.Engine.Documents.PdfDocument.Open(bytes));
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid() + ".pdf");
            try
            {
                System.IO.File.WriteAllBytes(path, bytes);
                var result = SearchService.Search(path, "Conventional");
                Assert.Equal(1, result.TotalHits);
                Assert.Equal([0], result.ResultPages);
                Assert.IsType<PageTextRuns>(new TextRunService().GetPage(path, 0));
            }
            finally { System.IO.File.Delete(path); }
        }

        [Fact]
        public void SearchResult_PageRects_EmptyByDefault()
        {
            var result = new SearchResult();
            Assert.Empty(result.PageRects);
            Assert.Empty(result.ResultPages);
            Assert.Equal(0, result.TotalHits);
        }
    }
}
