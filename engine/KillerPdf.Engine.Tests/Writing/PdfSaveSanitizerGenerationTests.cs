using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

public sealed class PdfSaveSanitizerGenerationTests
{
    [Theory]
    [InlineData(2, 0)]
    [InlineData(0, 3)]
    [InlineData(2, 3)]
    public void RepairsRetainCatalogAndPageGenerations(int catalogGeneration, int pageGeneration)
    {
        byte[] source = CreateSource(catalogGeneration, pageGeneration);
        PdfSaveRepairPlan plan = PdfSaveSanitizer.CreateRepairPlan(PdfDocument.Open(source));
        Assert.Equal(2, plan.Changes.Count);

        PdfDocument repaired = PdfDocument.Open(plan.Apply());

        PdfPageTree tree = PdfPageTree.Read(repaired);
        Assert.Equal(catalogGeneration, tree.CatalogReference.Generation);
        Assert.Equal(pageGeneration, tree.Pages[0].Reference.Generation);
        Assert.False(tree.Catalog.ContainsKey(new PdfName("Outlines"u8)));
        Assert.False(tree.Pages[0].Dictionary.ContainsKey(new PdfName("CropBox"u8)));
    }

    private static byte[] CreateSource(int catalogGeneration, int pageGeneration)
    {
        string[] objects =
        [
            $"1 {catalogGeneration} obj\n<< /Type /Catalog /Pages 2 0 R /Outlines << /Count 0 >> >>\nendobj\n",
            $"2 0 obj\n<< /Type /Pages /Kids [3 {pageGeneration} R] /Count 1 >>\nendobj\n",
            $"3 {pageGeneration} obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /CropBox [0 0 0 0] >>\nendobj\n"
        ];
        var source = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string value in objects)
        {
            offsets.Add(source.Length);
            source.Append(value);
        }
        int xrefOffset = source.Length;
        source.Append("xref\n0 4\n0000000000 65535 f \n");
        int[] generations = [catalogGeneration, 0, pageGeneration];
        for (int index = 0; index < offsets.Count; index++)
            source.Append($"{offsets[index]:0000000000} {generations[index]:00000} n \n");
        source.Append($"trailer\n<< /Size 4 /Root 1 {catalogGeneration} R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(source.ToString());
    }
}
