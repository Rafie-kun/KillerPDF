using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

public sealed class PdfMetadataCompressionTests
{
    [Fact]
    public void WriteWithStreamCompressionPreservesCatalogXmpAndCompressesPageContent()
    {
        const string repeated = "metadata preservation marker ";
        string title = string.Concat(Enumerable.Repeat(repeated, 100));
        string pageText = string.Concat(Enumerable.Repeat("compress ordinary content ", 20)).TrimEnd();
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = title })
            .AddPage(200, 200, new PdfContentStreamBuilder().BeginText()
                .SetFont(PdfStandardFont.Helvetica, 12).MoveText(20, 100)
                .ShowLatin1Text(pageText).EndText())
            .Build());
        PdfDictionary sourceCatalog = Assert.IsType<PdfDictionary>(
            source.Resolve(Assert.IsType<PdfIndirectReference>(source.Trailer[Name("Root")])));
        PdfStream sourceMetadata = Assert.IsType<PdfStream>(
            source.Resolve(Assert.IsType<PdfIndirectReference>(sourceCatalog[Name("Metadata")])));

        byte[] outputBytes = PdfDocumentWriter.Write(source, new PdfDocumentWriteOptions
        {
            CompressUnfilteredStreams = true
        });
        PdfDocument output = PdfDocument.Open(outputBytes);
        PdfDictionary outputCatalog = Assert.IsType<PdfDictionary>(
            output.Resolve(Assert.IsType<PdfIndirectReference>(output.Trailer[Name("Root")])));
        PdfStream outputMetadata = Assert.IsType<PdfStream>(
            output.Resolve(Assert.IsType<PdfIndirectReference>(outputCatalog[Name("Metadata")])));
        PdfPageTreeEntry outputPage = PdfPageTree.Read(output).Pages[0];
        PdfStream outputContent = Assert.IsType<PdfStream>(output.Resolve(
            Assert.IsType<PdfIndirectReference>(outputPage.Dictionary[Name("Contents")])));

        Assert.Equal(sourceMetadata.EncodedData.ToArray(), outputMetadata.EncodedData.ToArray());
        Assert.False(outputMetadata.Dictionary.ContainsKey(Name("Filter")));
        Assert.True(outputContent.Dictionary.ContainsKey(Name("Filter")));
        Assert.Equal(pageText, new PdfPageContentReader(output).Read(0).Text);
    }

    [Fact]
    public void WriteWithStreamCompressionPreservesCatalogReferencedXmpWithoutType()
    {
        const string title = "untyped catalog metadata preservation marker ";
        const string pageText = "compress ordinary content compress ordinary content";
        PdfDocument authored = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = string.Concat(Enumerable.Repeat(title, 30)) })
            .AddPage(200, 200, new PdfContentStreamBuilder().BeginText()
                .SetFont(PdfStandardFont.Helvetica, 12).MoveText(20, 100)
                .ShowLatin1Text(string.Concat(Enumerable.Repeat(pageText, 20))).EndText())
            .Build());
        PdfDictionary authoredCatalog = Assert.IsType<PdfDictionary>(
            authored.Resolve(Assert.IsType<PdfIndirectReference>(authored.Trailer[Name("Root")])));
        PdfIndirectReference metadataReference = Assert.IsType<PdfIndirectReference>(
            authoredCatalog[Name("Metadata")]);
        PdfStream authoredMetadata = Assert.IsType<PdfStream>(authored.Resolve(metadataReference));
        PdfStream untypedMetadata = new(new PdfDictionary(authoredMetadata.Dictionary
            .Where(entry => !entry.Key.Equals(Name("Type")))), authoredMetadata.EncodedData.Span);
        PdfDocument source = PdfDocument.Open(new PdfIncrementalUpdateBuilder(authored)
            .ReplaceObject(metadataReference.ObjectNumber, untypedMetadata).Build());
        PdfDictionary sourceCatalog = Assert.IsType<PdfDictionary>(
            source.Resolve(Assert.IsType<PdfIndirectReference>(source.Trailer[Name("Root")])));
        PdfStream sourceMetadata = Assert.IsType<PdfStream>(source.Resolve(
            Assert.IsType<PdfIndirectReference>(sourceCatalog[Name("Metadata")])));

        PdfDocument output = PdfDocument.Open(PdfDocumentWriter.Write(source,
            new PdfDocumentWriteOptions { CompressUnfilteredStreams = true }));
        PdfDictionary outputCatalog = Assert.IsType<PdfDictionary>(
            output.Resolve(Assert.IsType<PdfIndirectReference>(output.Trailer[Name("Root")])));
        PdfStream outputMetadata = Assert.IsType<PdfStream>(output.Resolve(
            Assert.IsType<PdfIndirectReference>(outputCatalog[Name("Metadata")])));
        PdfPageTreeEntry outputPage = PdfPageTree.Read(output).Pages[0];
        PdfStream outputContent = Assert.IsType<PdfStream>(output.Resolve(
            Assert.IsType<PdfIndirectReference>(outputPage.Dictionary[Name("Contents")])));

        Assert.False(sourceMetadata.Dictionary.ContainsKey(Name("Type")));
        Assert.Equal(sourceMetadata.EncodedData.ToArray(), outputMetadata.EncodedData.ToArray());
        Assert.False(outputMetadata.Dictionary.ContainsKey(Name("Filter")));
        Assert.True(outputContent.Dictionary.ContainsKey(Name("Filter")));
    }

    [Fact]
    public void WriteWithStreamCompressionSkipsStreamsTypedAsMetadata()
    {
        byte[] payload = Encoding.ASCII.GetBytes(string.Concat(
            Enumerable.Repeat("typed metadata stream marker ", 100)));
        PdfDocument authored = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        var update = new PdfIncrementalUpdateBuilder(authored);
        PdfIndirectReference typedMetadataReference = update.AddObject(new PdfStream(
            new PdfDictionary([
                new(Name("Type"), Name("Metadata")),
                new(Name("Subtype"), Name("XML"))
            ]), payload));
        PdfDocument source = PdfDocument.Open(update.Build());

        PdfDocument output = PdfDocument.Open(PdfDocumentWriter.Write(source,
            new PdfDocumentWriteOptions { CompressUnfilteredStreams = true }));
        PdfStream outputMetadata = Assert.IsType<PdfStream>(
            output.Resolve(typedMetadataReference));

        Assert.Equal(payload, outputMetadata.EncodedData.ToArray());
        Assert.False(outputMetadata.Dictionary.ContainsKey(Name("Filter")));
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
