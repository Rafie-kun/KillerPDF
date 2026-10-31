using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

public sealed class PdfDanglingReferenceRepairTests
{
    [Fact]
    public void OptInRewritePreservesLiveContentAndReportsNullReferences()
    {
        PdfDocument source = Create();
        Assert.Throws<InvalidOperationException>(() => PdfDocumentWriter.Write(source));

        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(source,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true });
        PdfDocument output = PdfDocument.Open(result.Data);
        Assert.Equal("Keep", new PdfPageContentReader(output).Read(0).Text);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(output.Resolve(
            Assert.IsType<PdfIndirectReference>(output.Trailer[N("Root")])));
        Assert.False(catalog.ContainsKey(N("Optional")));
        PdfArray privateData = Assert.IsType<PdfArray>(output.Trailer[N("Private")]);
        Assert.IsType<PdfNull>(privateData[0]);
        PdfString kept = Assert.IsType<PdfString>(output.Resolve(
            Assert.IsType<PdfIndirectReference>(privateData[1])));
        Assert.Equal("keep trailer", Encoding.Latin1.GetString(kept.Bytes.Span));
        Assert.Equal(2, result.ReferenceRepairs.Count);
        Assert.Contains(result.ReferenceRepairs, repair => repair.OwnerObjectNumber == 1
            && repair.Path == "/Optional" && repair.TargetObjectNumber == 99
            && repair.TargetGeneration == 0 && repair.Reason == PdfReferenceRepairReason.Missing
            && repair.Action == PdfReferenceRepairAction.RemoveDictionaryEntry);
        Assert.Contains(result.ReferenceRepairs, repair => repair.OwnerObjectNumber is null
            && repair.Path == "/Private[0]" && repair.TargetObjectNumber == 98
            && repair.Reason == PdfReferenceRepairReason.Free
            && repair.Action == PdfReferenceRepairAction.ReplaceWithNull);
    }

    [Fact]
    public void MetadataRemovalDoesNotRestoreOriginalDanglingCatalogEntries()
    {
        PdfDocument source = Create(metadata: true);
        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(source,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true,
                MetadataPolicy = PdfMetadataPolicy.RemoveDocumentInformationAndXmp });
        PdfDocument output = PdfDocument.Open(result.Data);

        Assert.Equal("Keep", new PdfPageContentReader(output).Read(0).Text);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(output.Resolve(
            Assert.IsType<PdfIndirectReference>(output.Trailer[N("Root")])));
        Assert.False(catalog.ContainsKey(N("Optional")));
        Assert.False(catalog.ContainsKey(N("Metadata")));
    }

    [Fact]
    public void OptInDoesNotEraseReferencesToLiveOmittedStructuralObjects()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        PdfIndirectReference root = Assert.IsType<PdfIndirectReference>(source.Trailer[N("Root")]);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(source.Resolve(root));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference structural = update.AddObject(new PdfStream(
            new PdfDictionary([new(N("Type"), N("XRef"))]), []));
        PdfDocument input = PdfDocument.Open(update.ReplaceObject(root.ObjectNumber,
            new PdfDictionary(catalog.Append(new KeyValuePair<PdfName, PdfObject>(N("Private"), structural))))
            .Build());

        Assert.Throws<InvalidOperationException>(() => PdfDocumentWriter.WriteWithReport(input,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothTrailerFormatsRetainRepairsAndLiveRootsWithPruning(bool streams)
    {
        PdfDocument source = Create();
        PdfDocumentWriteResult first = PdfDocumentWriter.WriteWithReport(source,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true,
                PruneUnreachableObjects = true, UseObjectStreams = streams,
                CompressStructuralStreams = streams,
                CrossReferenceFormat = streams ? PdfCrossReferenceFormat.Stream : PdfCrossReferenceFormat.Table });
        PdfDocument output = PdfDocument.Open(first.Data);
        Assert.Equal("Keep", new PdfPageContentReader(output).Read(0).Text);
        PdfArray data = Assert.IsType<PdfArray>(output.Trailer[N("Private")]);
        Assert.IsType<PdfNull>(data[0]);
        Assert.IsType<PdfString>(output.Resolve(Assert.IsType<PdfIndirectReference>(data[1])));
        Assert.Equal(2, first.ReferenceRepairs.Count);
    }

    // Recovery finds an object the cross-reference table does not list, and the viewer shows what it
    // holds. The rewrite gives that content a number of its own instead of erasing it or refusing.
    [Fact]
    public void MeaningfulCompatibilityRecoveredBytesAreKeptUnderANewObjectNumber()
    {
        PdfDocument source = Create(recovery: true);
        Assert.IsType<PdfString>(source.Resolve(new PdfIndirectReference(99, 0)));

        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(source,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true });

        PdfDocument output = PdfDocument.Open(result.Data);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(output.Resolve(
            Assert.IsType<PdfIndirectReference>(output.Trailer[N("Root")])));
        PdfIndirectReference optional = Assert.IsType<PdfIndirectReference>(catalog[N("Optional")]);
        Assert.Equal("recovered content", Encoding.Latin1.GetString(
            Assert.IsType<PdfString>(output.Resolve(optional)).Bytes.Span));
        Assert.Equal("Keep", new PdfPageContentReader(output).Read(0).Text);
        Assert.DoesNotContain(result.ReferenceRepairs, repair => repair.Path == "/Optional");
    }

    // A reference with the wrong generation reads as the live object in recovery, and the rewrite
    // points it at that object instead of copying it.
    [Fact]
    public void AStaleGenerationThatRecoveryReadsAsTheLiveObjectIsPointedAtIt()
    {
        PdfDocument source = Create(optionalReference: "6 1 R", recovery: true);
        Assert.IsType<PdfDictionary>(source.Resolve(new PdfIndirectReference(6, 1)));

        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(source,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true });

        PdfDocument output = PdfDocument.Open(result.Data);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(output.Resolve(
            Assert.IsType<PdfIndirectReference>(output.Trailer[N("Root")])));
        PdfIndirectReference optional = Assert.IsType<PdfIndirectReference>(catalog[N("Optional")]);
        Assert.Equal(6, optional.ObjectNumber);
        Assert.Equal("Helvetica", Assert.IsType<PdfName>(
            Assert.IsType<PdfDictionary>(output.Resolve(optional))[N("BaseFont")]).ValueAsLatin1());
    }

    // The catalog the viewer opens may be one the table never listed, and the output opens from it.
    [Fact]
    public void ACatalogTheTableDoesNotListStaysTheRoot()
    {
        const string content = "BT /F1 12 Tf 10 50 Td (Keep) Tj ET";
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 1\n0000000000 65535 f \n2 4\n");
        for (int index = 1; index < offsets.Count; index++) pdf.Append($"{offsets[index]:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        PdfDocument source = PdfDocument.OpenWithCompatibilityRecovery(Encoding.ASCII.GetBytes(pdf.ToString()));
        Assert.Equal("Keep", new PdfPageContentReader(source).Read(0).Text);

        PdfDocument output = PdfDocument.Open(PdfDocumentWriter.WriteWithReport(source,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true }).Data);

        Assert.Equal("Keep", new PdfPageContentReader(output).Read(0).Text);
    }

    // Recovered content can point at more recovered content, or back at itself.
    [Fact]
    public void RecoveredContentThatReferencesItselfIsKeptOnce()
    {
        PdfDocument source = Create(recovery: true, optionalReference: "97 0 R",
            recoveredBody: "<< /Self 97 0 R /Other 99 0 R >>");

        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(source,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true });

        PdfDocument output = PdfDocument.Open(result.Data);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(output.Resolve(
            Assert.IsType<PdfIndirectReference>(output.Trailer[N("Root")])));
        PdfIndirectReference optional = Assert.IsType<PdfIndirectReference>(catalog[N("Optional")]);
        PdfDictionary recovered = Assert.IsType<PdfDictionary>(output.Resolve(optional));
        Assert.Equal(optional.ObjectNumber, Assert.IsType<PdfIndirectReference>(recovered[N("Self")]).ObjectNumber);
        Assert.IsType<PdfString>(output.Resolve(Assert.IsType<PdfIndirectReference>(recovered[N("Other")])));
    }

    [Fact]
    public void IndirectCatalogAliasesRemainValidDuringNormalization()
    {
        PdfDocument source = Create(indirectRoot: true);
        Assert.Equal("Keep", new PdfPageContentReader(source).Read(0).Text);

        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(source,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true });

        Assert.Equal("Keep", new PdfPageContentReader(PdfDocument.Open(result.Data)).Read(0).Text);
    }

    [Theory]
    [InlineData("99 0 R", 0, PdfReferenceRepairReason.Missing)]
    [InlineData("98 0 R", 0, PdfReferenceRepairReason.Free)]
    [InlineData("6 1 R", 0, PdfReferenceRepairReason.StaleGeneration)]
    [InlineData("6 2 R", 3, PdfReferenceRepairReason.StaleGeneration)]
    public void ReferenceClassificationPreservesTheActiveFontIdentity(
        string reference, int fontGeneration, PdfReferenceRepairReason reason)
    {
        PdfDocument source = Create(optionalReference: reference, fontGeneration: fontGeneration);
        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(source,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true });
        PdfDocument output = PdfDocument.Open(result.Data);

        Assert.Equal("Keep", new PdfPageContentReader(output).Read(0).Text);
        Assert.IsType<PdfDictionary>(output.Resolve(new PdfIndirectReference(6, fontGeneration)));
        Assert.Contains(result.ReferenceRepairs, repair => repair.OwnerObjectNumber == 1
            && repair.Path == "/Optional" && repair.Reason == reason);
    }

    [Theory]
    [InlineData("99 0 R", "")]
    [InlineData("[99 0 R (kept)]", "[0]")]
    [InlineData("<< /Private 99 0 R /Length 1 >>\nstream\nx\nendstream", "/Private")]
    public void UnreachableBodiesArraysAndStreamDictionariesAreNormalized(
        string extraBody, string path)
    {
        PdfDocument source = Create(extraBody: extraBody);
        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(source,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true });
        PdfDocument output = PdfDocument.Open(result.Data);
        PdfObject body = output.Resolve(7);
        if (path.Length == 0) Assert.IsType<PdfNull>(body);
        else if (path == "[0]")
        {
            PdfArray array = Assert.IsType<PdfArray>(body);
            Assert.IsType<PdfNull>(array[0]);
            Assert.Equal("kept", Encoding.Latin1.GetString(Assert.IsType<PdfString>(array[1]).Bytes.Span));
        }
        else
        {
            PdfStream stream = Assert.IsType<PdfStream>(body);
            Assert.False(stream.Dictionary.ContainsKey(N("Private")));
            Assert.Equal("x", Encoding.Latin1.GetString(stream.EncodedData.Span));
        }
        Assert.Contains(result.ReferenceRepairs, repair => repair.OwnerObjectNumber == 7
            && repair.Path == path && repair.TargetObjectNumber == 99);
    }

    [Fact]
    public void RepeatedOutputIsDeterministicAndLeavesTheInputUnrepaired()
    {
        byte[] input = CreateData();
        byte[] original = input.ToArray();
        PdfDocument document = PdfDocument.Open(input);
        var options = new PdfDocumentWriteOptions { ReplaceDanglingReferences = true };

        PdfDocumentWriteResult first = PdfDocumentWriter.WriteWithReport(document, options);
        PdfDocumentWriteResult second = PdfDocumentWriter.WriteWithReport(document, options);

        Assert.Equal(first.Data.ToArray(), second.Data.ToArray());
        Assert.Equal(first.ReferenceRepairs, second.ReferenceRepairs);
        Assert.Equal(original, input);
        Assert.Throws<InvalidOperationException>(() => PdfDocumentWriter.Write(document));
        Assert.Throws<InvalidOperationException>(() => PdfDocumentWriter.Write(PdfDocument.Open(input)));
    }

    [Fact]
    public void DeduplicationUsesNormalizedResourceBodies()
    {
        PdfDocument source = Create(duplicateFonts: true);
        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(source,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true,
                DeduplicatePageResources = true });
        PdfDocument output = PdfDocument.Open(result.Data);

        Assert.Equal("Keep", new PdfPageContentReader(output).Read(0).Text);
        Assert.IsType<PdfNull>(output.Resolve(7));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthorizedInvalidationRepairsDanglingTrailerReferencesWithoutLosingFields(bool certified)
    {
        byte[] signed = PdfDetachedSignatureWriter.Sign(PdfDocument.Open(new PdfDocumentBuilder()
                .AddPage(100, 100, new PdfContentStreamBuilder().BeginText()
                    .SetFont(PdfStandardFont.Helvetica, 12).MoveText(10, 50).ShowLatin1Text("Keep").EndText())
                .AddTextField(0, "customer", 10, 10, 80, 20, "ALPHA").Build()),
            _ => [1], new PdfSignatureOptions { FieldName = "approval", ReservedSignatureSize = 16,
                SigningTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                CertificationPermission = certified
                    ? PdfSignatureCertificationPermission.FormFillingAndSignatures : null });
        string text = Encoding.Latin1.GetString(signed);
        int startXref = text.LastIndexOf("\nstartxref\n", StringComparison.Ordinal);
        int trailer = text.LastIndexOf("\ntrailer\n", startXref, StringComparison.Ordinal);
        int trailerEnd = text.LastIndexOf(">>", startXref, StringComparison.Ordinal);
        Assert.True(trailer >= 0 && trailerEnd > trailer);
        // The new entry follows the xref, leaving every stored object offset intact.
        byte[] damaged = Encoding.Latin1.GetBytes(text.Insert(trailerEnd, "\n/Private 999 0 R\n"));
        PdfDocument source = PdfDocument.Open(damaged);
        Assert.True(Assert.Single(PdfSignatureReader.Read(source)).IsSigned);
        Assert.Equal("Keep", new PdfPageContentReader(source).Read(0).Text);
        Assert.Throws<InvalidOperationException>(() => PdfDocumentWriter.WriteWithReport(source,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true }));

        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(source,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true, AllowSignatureInvalidation = true });
        PdfDocument output = PdfDocument.Open(result.Data);

        Assert.False(Assert.Single(PdfSignatureReader.Read(output)).IsSigned);
        Assert.Null(PdfSignatureReader.ReadCertificationPermission(output));
        Assert.Equal("Keep", new PdfPageContentReader(output).Read(0).Text);
        Assert.Equal("ALPHA", Assert.Single(PdfFormWidgetReader.ReadPage(output, 0),
            field => field.FieldName == "customer").Value);
        Assert.Contains(PdfFormWidgetReader.ReadPage(output, 0), field => field.FieldName == "approval");
        Assert.False(output.Trailer.ContainsKey(N("Private")));
        Assert.Contains(result.ReferenceRepairs, repair => repair.OwnerObjectNumber is null
            && repair.Path == "/Private" && repair.TargetObjectNumber == 999);
    }

    [Fact]
    public void InformationRemovalUsesNormalizedTrailerAndIdentityChains()
    {
        PdfDocument source = Create(extraBody: "99 0 R", extraTrailer: "/Info 7 0 R /Other 99 0 R");
        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(source,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true,
                MetadataPolicy = PdfMetadataPolicy.RemoveDocumentInformation });
        PdfDocument output = PdfDocument.Open(result.Data);

        Assert.Equal("Keep", new PdfPageContentReader(output).Read(0).Text);
        Assert.False(output.Trailer.ContainsKey(N("Info")));
        Assert.False(output.Trailer.ContainsKey(N("Other")));
        Assert.Contains(result.ReferenceRepairs, repair => repair.OwnerObjectNumber == 7
            && repair.Path.Length == 0 && repair.TargetObjectNumber == 99);
    }

    [Fact]
    public void RepairPathsDistinguishEscapedNamesFromNestedDictionaries()
    {
        PdfDocument source = Create(extraBody: "<< /A#2FB 99 0 R /A << /B 99 0 R >> >>");
        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(source,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true });

        PdfReferenceRepair[] repairs = result.ReferenceRepairs.Where(repair => repair.OwnerObjectNumber == 7).ToArray();
        Assert.Equal(2, repairs.Length);
        Assert.Contains(repairs, repair => repair.Path == "/A#2FB");
        Assert.Contains(repairs, repair => repair.Path == "/A/B");
        Assert.Equal(2, repairs.Select(repair => repair.Path).Distinct().Count());
    }

    [Fact]
    public void OmittedStructuralBodiesDoNotProduceOutputRepairRecords()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfIndirectReference omitted = update.AddObject(new PdfStream(
            new PdfDictionary([new(N("Type"), N("XRef")),
                new(N("Private"), new PdfIndirectReference(99, 0))]), []));
        PdfDocument input = PdfDocument.Open(update.Build());
        _ = PdfDocument.Open(PdfDocumentWriter.Write(input));

        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(input,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true });

        Assert.Empty(result.ReferenceRepairs);
        Assert.IsType<PdfNull>(PdfDocument.Open(result.Data).Resolve(omitted));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompressionPreservesUntypedMetadataAfterResourceDeduplication(bool normalize)
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        PdfIndirectReference root = Assert.IsType<PdfIndirectReference>(original.Trailer[N("Root")]);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(original.Resolve(root));
        PdfDictionary pages = Assert.IsType<PdfDictionary>(original.Resolve(
            Assert.IsType<PdfIndirectReference>(catalog[N("Pages")])));
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[N("Kids")])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(original.Resolve(pageReference));
        byte[] xml = Encoding.ASCII.GetBytes("<xmp>" + new string('x', 512) + "</xmp>");
        var update = new PdfIncrementalUpdateBuilder(original);
        PdfIndirectReference first = update.AddObject(new PdfStream(new PdfDictionary([]), xml));
        PdfIndirectReference second = update.AddObject(new PdfStream(new PdfDictionary([]), xml));
        update.ReplaceObject(root.ObjectNumber, new PdfDictionary(catalog.Append(
            new KeyValuePair<PdfName, PdfObject>(N("Metadata"), second))));
        var resources = new PdfDictionary([new(N("XObject"),
            new PdfDictionary([new(N("A"), first), new(N("B"), second)]))]);
        update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(
            page.Where(entry => !entry.Key.Equals(N("Resources")))
                .Append(new KeyValuePair<PdfName, PdfObject>(N("Resources"), resources))));

        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(PdfDocument.Open(update.Build()),
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = normalize,
                DeduplicatePageResources = true, CompressUnfilteredStreams = true });
        PdfDocument output = PdfDocument.Open(result.Data);
        PdfDictionary outputCatalog = Assert.IsType<PdfDictionary>(output.Resolve(
            Assert.IsType<PdfIndirectReference>(output.Trailer[N("Root")])));
        PdfStream metadata = Assert.IsType<PdfStream>(output.Resolve(
            Assert.IsType<PdfIndirectReference>(outputCatalog[N("Metadata")])));

        Assert.False(metadata.Dictionary.ContainsKey(N("Filter")));
        Assert.Equal(xml, metadata.EncodedData.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthorizedInvalidationPreservesTheOwnerEncryptionChoice(bool removeEncryption)
    {
        byte[] encrypted = new PdfDocumentBuilder().SetPasswordEncryption(new PdfPasswordEncryptionOptions
            { UserPassword = "user", OwnerPassword = "owner" })
            .AddPage(100, 100, new PdfContentStreamBuilder().BeginText()
                .SetFont(PdfStandardFont.Helvetica, 12).MoveText(10, 50).ShowLatin1Text("Keep").EndText()).Build();
        byte[] signed = PdfDetachedSignatureWriter.Sign(PdfDocument.Open(encrypted, "owner"),
            _ => [1], new PdfSignatureOptions { FieldName = "approval", ReservedSignatureSize = 16 });
        PdfDocument source = PdfDocument.Open(signed, "owner");
        Assert.True(source.IsEncrypted);
        Assert.True(Assert.Single(PdfSignatureReader.Read(source)).IsSigned);

        PdfDocumentWriteResult result = PdfDocumentWriter.WriteWithReport(source,
            new PdfDocumentWriteOptions { ReplaceDanglingReferences = true,
                AllowSignatureInvalidation = true, RemoveEncryption = removeEncryption });
        PdfDocument output = removeEncryption ? PdfDocument.Open(result.Data)
            : PdfDocument.Open(result.Data, "owner");

        Assert.Equal(!removeEncryption, output.IsEncrypted);
        Assert.Equal("Keep", new PdfPageContentReader(output).Read(0).Text);
        Assert.False(Assert.Single(PdfSignatureReader.Read(output)).IsSigned);
        Assert.Empty(result.ReferenceRepairs);
    }

    [Fact]
    public void EncryptionRemovalRejectsAnInfoReferenceToTheOmittedBootstrap()
    {
        byte[] encrypted = new PdfDocumentBuilder().SetPasswordEncryption(new PdfPasswordEncryptionOptions
            { UserPassword = "user", OwnerPassword = "owner" }).AddBlankPage().Build();
        PdfDocument original = PdfDocument.Open(encrypted, "owner");
        Assert.False(original.Trailer.ContainsKey(N("Info")));
        PdfIndirectReference encryption = Assert.IsType<PdfIndirectReference>(original.Trailer[N("Encrypt")]);
        string text = Encoding.Latin1.GetString(encrypted);
        int startXref = text.LastIndexOf("\nstartxref\n", StringComparison.Ordinal);
        int trailerEnd = text.LastIndexOf(">>", startXref, StringComparison.Ordinal);
        byte[] input = Encoding.Latin1.GetBytes(text.Insert(trailerEnd,
            $"\n/Info {encryption.ObjectNumber} {encryption.Generation} R\n"));
        PdfDocument source = PdfDocument.Open(input, "owner");
        Assert.True(source.IsDecrypted);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfDocumentWriter.WriteWithReport(source, new PdfDocumentWriteOptions
                { ReplaceDanglingReferences = true, RemoveEncryption = true }));

        Assert.Contains("omitted", error.Message, StringComparison.Ordinal);
    }

    private static PdfName N(string name) => new(Encoding.ASCII.GetBytes(name));

    private static PdfDocument Create(bool metadata = false, bool recovery = false, bool indirectRoot = false,
        string optionalReference = "99 0 R", int fontGeneration = 0, string? extraBody = null,
        bool duplicateFonts = false, string extraTrailer = "", string? recoveredBody = null)
    {
        byte[] data = CreateData(metadata, recovery, indirectRoot, optionalReference, fontGeneration, extraBody, duplicateFonts, extraTrailer, recoveredBody);
        return recovery ? PdfDocument.OpenWithCompatibilityRecovery(data) : PdfDocument.Open(data);
    }

    private static byte[] CreateData(bool metadata = false, bool recovery = false, bool indirectRoot = false,
        string optionalReference = "99 0 R", int fontGeneration = 0, string? extraBody = null,
        bool duplicateFonts = false, string extraTrailer = "", string? recoveredBody = null)
    {
        const string content = "BT /F1 12 Tf 10 50 Td (Keep) Tj ET";
        string[] objects = [
            $"<< /Type /Catalog /Pages 2 0 R /Optional {optionalReference} {(metadata ? "/Metadata 7 0 R" : "")} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 6 {fontGeneration} R {(duplicateFonts ? "/F2 7 0 R" : "")} >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "(keep trailer)",
            $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica {(duplicateFonts ? "/Private 99 0 R" : "")} >>"];
        if (metadata) objects = [.. objects,
            "<< /Type /Metadata /Subtype /XML /Length 4 >>\nstream\n<x/>\nendstream"];
        else if (indirectRoot) objects = [.. objects, "1 0 R"];
        else if (extraBody is not null) objects = [.. objects, extraBody];
        else if (duplicateFonts) objects = [.. objects, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} {(index == 5 ? fontGeneration : 0)} obj\n{objects[index]}\nendobj\n");
        }
        if (recovery) pdf.Append("99 0 obj\n(recovered content)\nendobj\n");
        if (recovery && recoveredBody is not null) pdf.Append($"97 0 obj\n{recoveredBody}\nendobj\n");
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        for (int index = 0; index < offsets.Count; index++)
            pdf.Append($"{offsets[index]:0000000000} {(index == 5 ? fontGeneration : 0):00000} n \n");
        pdf.Append($"98 1\n0000000000 00001 f \ntrailer << /Size 100 /Root {(indirectRoot ? 7 : 1)} 0 R /Private [98 0 R 5 0 R] {extraTrailer} >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
