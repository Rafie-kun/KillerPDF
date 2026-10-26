using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

public sealed class PdfResourcePruningSafetyTests
{
    [Fact]
    public void OptimizationPreservesPageResourcesUsedByAResourceLessForm()
    {
        PdfDocument source = Create("/Fm Do",
            "/XObject << /Fm 5 0 R >> /ColorSpace << /CS0 /DeviceRGB >>",
            Stream("/CS0 cs 1 0 0 sc 10 10 60 60 re f",
                "/Type /XObject /Subtype /Form /BBox [0 0 100 100]"));
        byte[] before = Render(source);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, before.AsSpan((50 * 100 + 50) * 4, 4).ToArray());
        AssertTargetRemovalControl(source);

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(source,
            new PdfOptimizationOptions { PruneUnusedPageResources = true,
                PackObjects = false, CompressStructure = false });
        PdfDocument output = PdfDocument.Open(plan.Apply().Data);

        Assert.Equal(before, Render(output));
        Assert.Empty(plan.ResourcePageIndexes);
    }

    [Theory]
    [InlineData("CS", "/CS0", "\u00ff\0\0")]
    [InlineData("ColorSpace", "/CS0", "\u00ff\0\0")]
    [InlineData("CS", "[/Indexed /CS0 1 <000000ff0000>]", "\u0001")]
    public void OptimizationPreservesNamedInlineImageColorSpaces(string key, string colorSpace, string data)
    {
        PdfDocument source = Create(
            $"q 60 0 0 60 10 10 cm BI /W 1 /H 1 /BPC 8 /{key} {colorSpace} ID {data} EI Q",
            "/ColorSpace << /CS0 /DeviceRGB >>");
        byte[] before = Render(source);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, before.AsSpan((50 * 100 + 50) * 4, 4).ToArray());
        AssertTargetRemovalControl(source);

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(source,
            new PdfOptimizationOptions { PruneUnusedPageResources = true,
                PackObjects = false, CompressStructure = false });

        Assert.Equal(before, Render(PdfDocument.Open(plan.Apply().Data)));
        Assert.Empty(plan.ResourcePageIndexes);
    }

    [Theory]
    [InlineData("form")]
    [InlineData("inline")]
    public void DirectContentPruningPreservesIndirectResourceConsumers(string kind)
    {
        PdfDocument source = kind == "form"
            ? Create("/Fm Do", "/XObject << /Fm 5 0 R >> /ColorSpace << /CS0 /DeviceRGB >>",
                Stream("/CS0 cs 1 0 0 sc 10 10 60 60 re f",
                    "/Type /XObject /Subtype /Form /BBox [0 0 100 100]"))
            : Create("q 60 0 0 60 10 10 cm BI /W 1 /H 1 /BPC 8 /CS /CS0 ID \u00ff\0\0 EI Q",
                "/ColorSpace << /CS0 /DeviceRGB >>");
        byte[] before = Render(source);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, before.AsSpan((50 * 100 + 50) * 4, 4).ToArray());
        AssertTargetRemovalControl(source);

        byte[] output = new PdfIncrementalPageEditor(source)
            .SetPageContentAndPruneResources(0, new PdfPageContentReader(source).ReadInstructions(0))
            .Build();

        Assert.Equal(before, Render(PdfDocument.Open(output)));
    }

    [Fact]
    public void GraphicsStateFontRemainsReachableWhenItsUnusedAliasIsPruned()
    {
        PdfDocument source = Create("BT /GS0 gs 10 40 Td (VISIBLE) Tj ET",
            "/Font << /F1 5 0 R >> /ExtGState << /GS0 << /Font [5 0 R 12] >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        byte[] before = Render(source);
        Assert.Contains(before.Chunk(4), pixel => pixel[0] < 128 && pixel[1] < 128 && pixel[2] < 128);
        byte[] empty = Render(PdfDocument.Open(new PdfIncrementalPageEditor(source)
            .SetPageContent(0, ReadOnlyMemory<byte>.Empty).Build()));
        Assert.DoesNotContain(empty.Chunk(4), pixel => pixel[0] < 128 && pixel[1] < 128 && pixel[2] < 128);

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(source,
            new PdfOptimizationOptions { PruneUnusedPageResources = true,
                PruneUnreachableObjects = true, PackObjects = false, CompressStructure = false });
        PdfDocument output = PdfDocument.Open(plan.Apply().Data);

        Assert.Equal(before, Render(output));
        Assert.Equal("VISIBLE", new PdfPageContentReader(output).Read(0).Text);
        Assert.Equal(new[] { 0 }, plan.ResourcePageIndexes);
    }

    private static void AssertTargetRemovalControl(PdfDocument source)
    {
        PdfDocument absent = PdfDocument.Open(new PdfIncrementalPageEditor(source)
            .SetPageContent(0, ReadOnlyMemory<byte>.Empty).Build());
        Assert.Equal(new byte[] { 255, 255, 255, 255 },
            Render(absent).AsSpan((50 * 100 + 50) * 4, 4).ToArray());
    }

    private static byte[] Render(PdfDocument document) => new PdfPageRenderer(document)
        .Render(0, new PdfRenderOptions(100, 100)).Pixels.ToArray();

    private static string Stream(string data, string entries = "") =>
        $"<< {entries} /Length {Encoding.Latin1.GetByteCount(data)} >>\nstream\n{data}\nendstream";

    private static PdfDocument Create(string content, string resources, params string[] extraObjects)
    {
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << {resources} >> /Contents 4 0 R >>",
            Stream(content), .. extraObjects];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }
}
