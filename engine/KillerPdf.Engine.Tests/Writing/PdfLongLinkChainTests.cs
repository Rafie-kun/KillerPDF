using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

// A long /Next chain is one deep path through the object graph, not deep nesting. These
// tests pin that healthy files with long chains rewrite, and that the walkers stay
// iterative so a very long chain cannot overflow the stack.
public sealed class PdfLongLinkChainTests
{
    private static byte[] FlatBookmarks(int count)
    {
        PdfDocumentBuilder builder = new PdfDocumentBuilder().AddBlankPage();
        for (int index = 0; index < count; index++)
            builder.AddBookmark("Item " + index.ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
        return builder.Build();
    }

    [Theory]
    [InlineData(125)]
    [InlineData(127)]
    [InlineData(600)]
    public void FullRewriteAcceptsLongSiblingBookmarkChain(int count)
    {
        PdfDocument document = PdfDocument.Open(FlatBookmarks(count));

        byte[] rewritten = PdfDocumentWriter.Write(document);

        _ = PdfDocument.Open(rewritten);
    }

    [Fact]
    public void FullRewriteAcceptsLongNextPageLinkChain()
    {
        const int pages = 300;
        var source = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Add(string body)
        {
            offsets.Add(source.Length);
            source.Append(offsets.Count).Append(" 0 obj ").Append(body).Append(" endobj\n");
        }
        Add("<< /Type /Catalog /Pages 2 0 R >>");
        Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Link 4 0 R >>");
        for (int index = 0; index < pages; index++)
            Add(index == pages - 1 ? "<< /Last true >>" : $"<< /Next {5 + index} 0 R >>");
        int xref = source.Length;
        source.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) source.Append(offset.ToString("D10")).Append(" 00000 n \n");
        source.Append("trailer << /Size ").Append(offsets.Count + 1)
            .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        byte[] rewritten = PdfDocumentWriter.Write(
            PdfDocument.Open(Encoding.ASCII.GetBytes(source.ToString())));

        _ = PdfDocument.Open(rewritten);
    }

    [Fact]
    public async Task JavaScriptWalkersSurviveVeryLongOutlineOnThreadPoolThread()
    {
        PdfDocument document = PdfDocument.Open(FlatBookmarks(8000));
        PdfDocument? cleaned = null;
        bool found = true;

        await Task.Run(() =>
        {
            found = PdfOptimizer.HasJavaScriptActions(document);
            cleaned = PdfOptimizer.RemoveJavaScriptActions(document);
        });

        Assert.False(found);
        Assert.NotNull(cleaned);
    }

    [Fact]
    public async Task JavaScriptWalkersStillFindActionAtEndOfLongOutlineChain()
    {
        const int count = 6000;
        var source = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Add(string body)
        {
            offsets.Add(source.Length);
            source.Append(offsets.Count).Append(" 0 obj ").Append(body).Append(" endobj\n");
        }
        Add("<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>");
        Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>");
        for (int index = 0; index < count; index++)
            Add(index == count - 1 ? "<< /A << /S /JavaScript /JS (app.alert(1)) >> >>"
                : $"<< /Next {5 + index} 0 R >>");
        int xref = source.Length;
        source.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) source.Append(offset.ToString("D10")).Append(" 00000 n \n");
        source.Append("trailer << /Size ").Append(offsets.Count + 1)
            .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        PdfDocument document = PdfDocument.Open(Encoding.ASCII.GetBytes(source.ToString()));
        PdfDocument? cleaned = null;
        bool found = false;

        await Task.Run(() =>
        {
            found = PdfOptimizer.HasJavaScriptActions(document);
            cleaned = PdfOptimizer.RemoveJavaScriptActions(document);
        });

        Assert.True(found);
        Assert.False(PdfOptimizer.HasJavaScriptActions(cleaned!));
    }

    [Fact]
    public void CyclicLinkChainStillEndsSafely()
    {
        var source = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Add(string body)
        {
            offsets.Add(source.Length);
            source.Append(offsets.Count).Append(" 0 obj ").Append(body).Append(" endobj\n");
        }
        Add("<< /Type /Catalog /Pages 2 0 R /Loop 4 0 R >>");
        Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>");
        Add("<< /Next 5 0 R >>");
        Add("<< /Next 4 0 R >>");
        int xref = source.Length;
        source.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) source.Append(offset.ToString("D10")).Append(" 00000 n \n");
        source.Append("trailer << /Size ").Append(offsets.Count + 1)
            .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        PdfDocument document = PdfDocument.Open(Encoding.ASCII.GetBytes(source.ToString()));

        Assert.False(PdfOptimizer.HasJavaScriptActions(document));
        _ = PdfDocument.Open(PdfDocumentWriter.Write(document));
    }

    [Fact]
    public void ReduceKeepsFileWithIndirectLengthAndUnreachableObject()
    {
        var source = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Add(string body)
        {
            offsets.Add(source.Length);
            source.Append(offsets.Count).Append(" 0 obj ").Append(body).Append(" endobj\n");
        }
        const string content = "0 0 m 10 10 l S";
        Add("<< /Type /Catalog /Pages 2 0 R >>");
        Add("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>");
        Add($"<< /Length 5 0 R >>\nstream\n{content}\nendstream");
        Add(content.Length.ToString());
        Add("<< /Orphan true >>");
        int xref = source.Length;
        source.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) source.Append(offset.ToString("D10")).Append(" 00000 n \n");
        source.Append("trailer << /Size ").Append(offsets.Count + 1)
            .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        PdfDocument document = PdfDocument.Open(Encoding.ASCII.GetBytes(source.ToString()));

        PdfOptimizationResult result = PdfOptimizer.CreatePlan(document, new PdfOptimizationOptions
        {
            PruneUnreachableObjects = true,
            PackObjects = false,
            CompressStructure = false
        }).Apply();

        Assert.Equal(0, PdfDocumentWriter.CountUnreachableObjects(PdfDocument.Open(result.Data)));
    }
}
