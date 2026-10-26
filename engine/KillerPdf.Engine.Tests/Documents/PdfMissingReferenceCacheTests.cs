using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfMissingReferenceCacheTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RepeatedUnresolvableReferencesHaveBoundedAllocation(bool compatibility, bool freeEntry)
    {
        byte[] bytes = Fixture(65_536);
        PdfDocument document = compatibility ? PdfDocument.OpenWithCompatibilityRecovery(bytes) : PdfDocument.Open(bytes);
        var reference = new PdfIndirectReference(freeEntry ? 2 : 99, 0);
        for (int index = 0; index < 16; index++) Assert.Same(PdfNull.Instance, document.Resolve(reference));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1_000; index++)
            if (document.Resolve(reference) is not PdfNull)
                throw new InvalidOperationException("An absent object unexpectedly resolved.");
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, 8_192);
        Assert.Equal(bytes, document.Source.ToArray());
        Assert.Equal(new PdfName("Catalog"u8), Assert.IsType<PdfDictionary>(document.Resolve(1))[new PdfName("Type"u8)]);
        if (compatibility)
            Assert.Equal("Recoverable"u8.ToArray(), Assert.IsType<PdfString>(
                document.Resolve(new PdfIndirectReference(2, 1))).Bytes.ToArray());
        else Assert.Same(PdfNull.Instance, document.Resolve(new PdfIndirectReference(2, 1)));
    }

    [Fact]
    public void ConcurrentDistinctMissesDoNotPreventRecoveryOfAnotherGeneration()
    {
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(Fixture(0));
        Assert.Same(PdfNull.Instance, document.Resolve(new PdfIndirectReference(2, 0)));
        Parallel.For(0, 5_000, index =>
            Assert.Same(PdfNull.Instance, document.Resolve(new PdfIndirectReference(100 + index, 0))));
        Assert.Equal("Recoverable"u8.ToArray(), Assert.IsType<PdfString>(
            document.Resolve(new PdfIndirectReference(2, 1))).Bytes.ToArray());
        Assert.Same(PdfNull.Instance, document.Resolve(new PdfIndirectReference(2, 0)));
    }

    private static byte[] Fixture(int padding)
    {
        var text = new StringBuilder("%PDF-1.7\n%").Append('x', padding).Append('\n');
        int catalogOffset = text.Length;
        text.Append("1 0 obj << /Type /Catalog >> endobj\n2 1 obj (Recoverable) endobj\n");
        int xrefOffset = text.Length;
        text.Append("xref\n0 3\n0000000000 65535 f \n")
            .Append(catalogOffset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture))
            .Append(" 00000 n \n0000000000 00000 f \ntrailer << /Size 3 /Root 1 0 R >>\nstartxref\n")
            .Append(xrefOffset).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
