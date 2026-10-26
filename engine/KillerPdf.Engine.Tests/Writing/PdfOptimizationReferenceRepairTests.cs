using System.Text.Json;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

public sealed class PdfOptimizationReferenceRepairTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlanRepairsBeforeReachabilityAndPreservesOriginalAccounting(bool removeMetadata)
    {
        byte[] healthy = new PdfDocumentBuilder().SetMetadata(new PdfDocumentMetadata { Title = "Synthetic title" })
            .AddPage(100, 100,
            new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(10, 50).ShowLatin1Text("Keep").EndText()).Build();
        PdfDocument original = PdfDocument.Open(healthy);
        PdfIndirectReference root = Assert.IsType<PdfIndirectReference>(original.Trailer[new PdfName("Root"u8)]);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(original.Resolve(root));
        byte[] damaged = new PdfIncrementalUpdateBuilder(original).ReplaceObject(root.ObjectNumber,
            new PdfDictionary(catalog.Append(new KeyValuePair<PdfName, PdfObject>(new PdfName("Optional"u8),
                new PdfIndirectReference(99, 0))))).Build();
        PdfDocument source = PdfDocument.Open(damaged);
        PdfOptimizationOptions options = PdfOptimizationOptions.ForDocument(source) with
            { ReplaceDanglingReferences = true, RemoveMetadata = removeMetadata };

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(source, options);
        PdfOptimizationResult result = plan.Apply();

        Assert.Equal("Keep", new PdfPageContentReader(PdfDocument.Open(result.Data)).Read(0).Text);
        Assert.Equal(damaged.Length, plan.OriginalSize);
        Assert.Equal(damaged.Length, result.OriginalSize);
        Assert.Equal(source.CrossReferences.Values.Count(entry => entry.Type is PdfCrossReferenceEntryType.InUse
            or PdfCrossReferenceEntryType.Compressed), result.OriginalObjectCount);
        Assert.Equal(99, Assert.Single(plan.ReferenceRepairs).TargetObjectNumber);
        Assert.Equal(plan.ReferenceRepairs, result.ReferenceRepairs);
        using JsonDocument preview = JsonDocument.Parse(plan.ToJson());
        using JsonDocument report = JsonDocument.Parse(result.ToJson());
        Assert.Equal(99, preview.RootElement.GetProperty("referenceRepairs")[0].GetProperty("targetObjectNumber").GetInt32());
        Assert.Equal(99, report.RootElement.GetProperty("referenceRepairs")[0].GetProperty("targetObjectNumber").GetInt32());
        Assert.Contains("/Optional", plan.ToText(), StringComparison.Ordinal);
        Assert.Contains("/Optional", result.ToText(), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => PdfOptimizer.CreatePlan(PdfDocument.Open(damaged),
            options with { ReplaceDanglingReferences = false }).Apply());
    }

    [Fact]
    public void RepairPreflightRequiresSignatureConsentAndRetainsSourceSignature()
    {
        byte[] signed = PdfDetachedSignatureWriter.Sign(PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(100, 100).Build()), _ => [1],
            new PdfSignatureOptions { FieldName = "SyntheticSignature", ReservedSignatureSize = 256 });
        PdfDocument original = PdfDocument.Open(signed);
        PdfIndirectReference root = Assert.IsType<PdfIndirectReference>(original.Trailer[new PdfName("Root"u8)]);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(original.Resolve(root));
        PdfDocument source = PdfDocument.Open(new PdfIncrementalUpdateBuilder(original).ReplaceObject(root.ObjectNumber,
            new PdfDictionary(catalog.Append(new KeyValuePair<PdfName, PdfObject>(new PdfName("Optional"u8),
                new PdfIndirectReference(99, 0))))).Build());
        PdfOptimizationOptions options = PdfOptimizationOptions.ForDocument(source) with
            { ReplaceDanglingReferences = true };

        Assert.Throws<InvalidOperationException>(() => PdfOptimizer.CreatePlan(source, options));
        PdfOptimizationResult result = PdfOptimizer.CreatePlan(source,
            options with { AllowSignatureInvalidation = true }).Apply();

        Assert.Single(result.ReferenceRepairs);
        Assert.Contains(PdfSignatureReader.Read(source), signature => signature.IsSigned);
        Assert.DoesNotContain(PdfSignatureReader.Read(PdfDocument.Open(result.Data)), signature => signature.IsSigned);
    }

    [Fact]
    public void RepairPreflightDoesNotSilentlyRemoveAuthenticatedEncryption()
    {
        byte[] encrypted = new PdfDocumentBuilder().AddBlankPage().SetPasswordEncryption(
            new PdfPasswordEncryptionOptions { UserPassword = "synthetic-open", OwnerPassword = "synthetic-owner" })
            .Build();
        PdfDocument original = PdfDocument.Open(encrypted, "synthetic-owner");
        PdfIndirectReference root = Assert.IsType<PdfIndirectReference>(original.Trailer[new PdfName("Root"u8)]);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(original.Resolve(root));
        byte[] damaged = new PdfIncrementalUpdateBuilder(original).ReplaceObject(root.ObjectNumber,
            new PdfDictionary(catalog.Append(new KeyValuePair<PdfName, PdfObject>(new PdfName("Optional"u8),
                new PdfIndirectReference(99, 0))))).Build();
        PdfDocument source = PdfDocument.Open(damaged, "synthetic-owner");

        NotSupportedException error = Assert.Throws<NotSupportedException>(() => PdfOptimizer.CreatePlan(source,
            PdfOptimizationOptions.ForDocument(source) with { ReplaceDanglingReferences = true }));

        Assert.Contains("decrypted working copy", error.Message, StringComparison.Ordinal);
        Assert.True(source.IsEncrypted);
        Assert.Single(PdfPageTree.Read(PdfDocument.Open(damaged, "synthetic-owner")).Pages);
    }

    [Fact]
    public void RepairPreflightAllowsRemovalOfMalformedInheritedInformation()
    {
        PdfDocument healthy = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .SetMetadata(new PdfDocumentMetadata { Title = "Long synthetic metadata title for a malformed-field fixture" }).Build());
        PdfIndirectReference root = Assert.IsType<PdfIndirectReference>(healthy.Trailer[new PdfName("Root"u8)]);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(healthy.Resolve(root));
        byte[] damaged = new PdfIncrementalUpdateBuilder(healthy).ReplaceObject(root.ObjectNumber,
            new PdfDictionary(catalog.Append(new KeyValuePair<PdfName, PdfObject>(new PdfName("Optional"u8),
                new PdfIndirectReference(99, 0))))).Build();
        PdfIndirectReference info = Assert.IsType<PdfIndirectReference>(healthy.Trailer[new PdfName("Info"u8)]);
        byte[] originalInformation = PdfObjectWriter.Write(healthy.Resolve(info));
        int offset = damaged.AsSpan().IndexOf(originalInformation);
        Assert.True(offset > 0);
        byte[] invalidInformation = Encoding.ASCII.GetBytes("<< /Title 42 >>");
        Assert.True(invalidInformation.Length < originalInformation.Length);
        damaged.AsSpan(offset, originalInformation.Length).Fill((byte)' ');
        invalidInformation.CopyTo(damaged.AsSpan(offset));
        PdfDocument source = PdfDocument.Open(damaged);
        Assert.Equal(42, Assert.IsType<PdfInteger>(Assert.IsType<PdfDictionary>(source.Resolve(info))
            [new PdfName("Title"u8)]).Value);

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(source,
            PdfOptimizationOptions.ForDocument(source) with { ReplaceDanglingReferences = true, RemoveMetadata = true });
        PdfOptimizationResult result = plan.Apply();

        Assert.Contains(PdfOptimizationChangeKind.RemoveMetadata, plan.Changes);
        Assert.Contains(PdfOptimizationChangeKind.RemoveMetadata, result.VerifiedRemovals);
        Assert.False(PdfDocument.Open(result.Data).Trailer.ContainsKey(new PdfName("Info"u8)));
        Assert.Equal(99, Assert.Single(result.ReferenceRepairs).TargetObjectNumber);
        Assert.Equal(damaged.Length, result.OriginalSize);
    }
}
