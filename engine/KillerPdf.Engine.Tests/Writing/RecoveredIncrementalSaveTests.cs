using System.Text;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

public sealed class RecoveredIncrementalSaveTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    public void SavedRevisionStrictlyResolvesChangedAndUntouchedObjects(int recovery, bool stream)
    {
        byte[] source = Source(recovery != 0, recovery == 1, recovery == 3);
        PdfDocument original = recovery != 0
            ? PdfDocument.OpenWithCompatibilityRecovery(source) : PdfDocument.Open(source);
        if (recovery == 1)
            Assert.Equal(original.Header.Offset, original.CrossReferences.StartXref.Offset);
        var update = new PdfIncrementalUpdateBuilder(original);
        update.ReplaceObject(5, new PdfInteger(42));
        PdfIndirectReference added = update.AddObject(new PdfInteger(99));
        byte[] saved = update.Build(new PdfIncrementalUpdateWriteOptions
        {
            CrossReferenceFormat = stream ? PdfCrossReferenceFormat.Stream : PdfCrossReferenceFormat.Table
        });

        PdfDocument reopened = PdfDocument.Open(saved);
        Assert.Equal(source, saved[..source.Length]);
        Assert.Equal(42, Assert.IsType<PdfInteger>(reopened.Resolve(new PdfIndirectReference(5, 2))).Value);
        Assert.Equal(99, Assert.IsType<PdfInteger>(reopened.Resolve(added)).Value);
        var page = Assert.IsType<PdfDictionary>(reopened.Resolve(3));
        var content = Assert.IsType<PdfStream>(reopened.Resolve(
            Assert.IsType<PdfIndirectReference>(page[new PdfName("Contents"u8)])));
        Assert.Equal("q Q", Encoding.ASCII.GetString(content.EncodedData.Span));
        Assert.Equal(recovery != 0 ? 1 : 2, reopened.CrossReferences.Sections.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RecoveredCompressedSourceRequiresStreamAndPreservesUntouchedMembers(
        bool packChanges, bool useDefaults)
    {
        var seed = new PdfIncrementalUpdateBuilder(PdfDocument.OpenWithCompatibilityRecovery(Source(true)));
        var pages = new PdfDictionary([
            new(new PdfName("Type"u8), new PdfName("Pages"u8)),
            new(new PdfName("Kids"u8), new PdfArray([new PdfIndirectReference(3, 0)])),
            new(new PdfName("Count"u8), new PdfInteger(1))
        ]);
        seed.ReplaceObject(2, pages);
        byte[] bytes = seed.Build(new PdfIncrementalUpdateWriteOptions
        {
            CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
            UseObjectStreams = true,
            CompressObjectStreams = true
        });
        byte[] damaged = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(bytes)
            .Replace("/Type /XRef", "/Type /Gone", StringComparison.Ordinal));
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(damaged);
        Assert.Equal(recovered.Header.Offset, recovered.CrossReferences.StartXref.Offset);
        Assert.Equal(PdfCrossReferenceEntryType.Compressed, recovered.CrossReferences[2].Type);
        var update = new PdfIncrementalUpdateBuilder(recovered);
        update.ReplaceObject(5, new PdfInteger(42));
        update.FreeObject(4);
        PdfIndirectReference added = update.AddObject(new PdfInteger(99));
        NotSupportedException error = Assert.Throws<NotSupportedException>(() => update.Build(
            new PdfIncrementalUpdateWriteOptions { CrossReferenceFormat = PdfCrossReferenceFormat.Table }));
        Assert.Contains("requires cross-reference stream output", error.Message);

        byte[] saved = useDefaults ? update.Build() : update.Build(new PdfIncrementalUpdateWriteOptions
        {
            CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
            CompressCrossReferenceStream = true,
            UseObjectStreams = packChanges,
            CompressObjectStreams = packChanges
        });
        PdfDocument reopened = PdfDocument.Open(saved);
        Assert.Equal(damaged, saved[..damaged.Length]);
        Assert.Equal(42, Assert.IsType<PdfInteger>(reopened.Resolve(new PdfIndirectReference(5, 2))).Value);
        Assert.Equal(99, Assert.IsType<PdfInteger>(reopened.Resolve(added)).Value);
        Assert.Equal(1, Assert.IsType<PdfInteger>(Assert.IsType<PdfDictionary>(
            reopened.Resolve(2))[new PdfName("Count"u8)]).Value);
        Assert.Equal(recovered.CrossReferences[2], reopened.CrossReferences[2]);
        Assert.Equal(PdfCrossReferenceEntryType.Free, reopened.CrossReferences[4].Type);
        Assert.Equal(1, reopened.CrossReferences[4].Field2);
        Assert.IsType<PdfNull>(reopened.Resolve(4));
        Assert.Single(reopened.CrossReferences.Sections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveredEncryptedSourceKeepsProtectionAndUntouchedContent(bool stream)
    {
        byte[] encrypted = PdfDocumentEncryptionWriter.EncryptWithPasswords(
            PdfDocument.Open(Source(false)), new PdfPasswordEncryptionOptions
            {
                UserPassword = "open",
                OwnerPassword = "owner"
            });
        string text = Encoding.Latin1.GetString(encrypted);
        int marker = text.LastIndexOf("startxref", StringComparison.Ordinal);
        byte[] damaged = Encoding.Latin1.GetBytes(text[..marker] + "startxref\n1\n%%EOF\n");
        PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(damaged, "owner");
        var update = new PdfIncrementalUpdateBuilder(recovered);
        PdfIndirectReference added = update.AddObject(
            new PdfString("new secret"u8, PdfStringForm.Literal));
        byte[] saved = update.Build(new PdfIncrementalUpdateWriteOptions
        {
            CrossReferenceFormat = stream ? PdfCrossReferenceFormat.Stream : PdfCrossReferenceFormat.Table
        });

        PdfDocument reopened = PdfDocument.Open(saved, "open");
        Assert.True(reopened.IsEncrypted);
        Assert.True(reopened.IsDecrypted);
        Assert.Equal("new secret", Encoding.ASCII.GetString(
            Assert.IsType<PdfString>(reopened.Resolve(added)).Bytes.Span));
        Assert.Equal(-1, saved.AsSpan().IndexOf("new secret"u8));
        var page = PdfPageTree.Read(reopened).Pages[0];
        var content = Assert.IsType<PdfStream>(reopened.Resolve(
            Assert.IsType<PdfIndirectReference>(page.Dictionary[new PdfName("Contents"u8)])));
        Assert.Equal("q Q", Encoding.ASCII.GetString(content.EncodedData.Span));
        Assert.Equal(damaged, saved[..damaged.Length]);
        Assert.Single(reopened.CrossReferences.Sections);
    }

    private static byte[] Source(bool broken, bool damagedTable = true, bool truncatedChain = false)
    {
        var source = new StringBuilder("%PDF-2.0\n");
        string[] objects =
        [
            "1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n",
            "2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n",
            "3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >> endobj\n",
            "4 0 obj << /Length 3 >> stream\nq Q\nendstream endobj\n",
            "5 2 obj 7 endobj\n"
        ];
        var offsets = new List<int>();
        foreach (string value in objects)
        {
            offsets.Add(source.Length);
            source.Append(value);
        }
        int xref = source.Length;
        source.Append(broken && damagedTable ? "damaged\n" : "xref\n");
        source.Append("0 6\n0000000000 65535 f \n");
        for (int index = 0; index < offsets.Count; index++)
            source.Append($"{offsets[index]:0000000000} {(index == 4 ? 2 : 0):00000} n \n");
        source.Append("trailer << /Size 6 /Root 1 0 R");
        if (truncatedChain) source.Append(" /Prev 99999999");
        source.Append($" >>\nstartxref\n{(broken && !truncatedChain ? 1 : xref)}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(source.ToString());
    }
}
