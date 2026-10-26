using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ResaveReferenceRepairTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ProtectRepairsOptionalReferenceAndPreservesLiveContent(bool certificateProtection, bool needsRepair)
    {
        string folder = Path.Combine(Path.GetTempPath(), "killerpdf-protect-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            byte[] healthyBytes = new PdfDocumentBuilder().AddPage(100, 100,
                new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                    .MoveText(10, 50).ShowLatin1Text("Keep").EndText()).Build();
            PdfDocument healthy = PdfDocument.Open(healthyBytes);
            PdfIndirectReference root = Assert.IsType<PdfIndirectReference>(healthy.Trailer[new PdfName("Root"u8)]);
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(healthy.Resolve(root));
            byte[] damaged = needsRepair ? new PdfIncrementalUpdateBuilder(healthy).ReplaceObject(root.ObjectNumber,
                new PdfDictionary(catalog.Append(new KeyValuePair<PdfName, PdfObject>(new PdfName("Optional"u8),
                    new PdfIndirectReference(99, 0))))).Build() : healthyBytes;
            string source = Path.Combine(folder, "source.pdf");
            string destination = Path.Combine(folder, "output.pdf");
            File.WriteAllBytes(source, damaged);

            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Synthetic repair protection", key,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using X509Certificate2 certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            IReadOnlyList<PdfReferenceRepair> repairs = certificateProtection
                ? PdfEngineIntegration.ProtectForRecipients(source, destination,
                    new PdfCertificateEncryptionOptions { Recipients = [certificate] }, false)
                : PdfEngineIntegration.ProtectWithPasswords(source, destination, new PdfPasswordEncryptionOptions
                    { UserPassword = "synthetic-open", OwnerPassword = "synthetic-owner" }, false);
            if (needsRepair) Assert.Equal(99, Assert.Single(repairs).TargetObjectNumber);
            else Assert.Empty(repairs);

            byte[] saved = File.ReadAllBytes(destination);
            PdfDocument output = certificateProtection ? PdfDocument.Open(saved, certificate)
                : PdfDocument.Open(saved, "synthetic-owner");
            Assert.True(output.IsEncrypted);
            Assert.Equal("Keep", new PdfPageContentReader(output).Read(0).Text);
            Assert.Equal(damaged, File.ReadAllBytes(source));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ResaveRepairsOptionalReferenceAndPreservesLiveContent(bool alreadyOpened, bool needsRepair)
    {
        string folder = Path.Combine(Path.GetTempPath(), "killerpdf-resave-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            byte[] healthyBytes = new PdfDocumentBuilder().AddPage(100, 100,
                new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                    .MoveText(10, 50).ShowLatin1Text("Keep").EndText()).Build();
            PdfDocument healthy = PdfDocument.Open(healthyBytes);
            var rootName = new PdfName("Root"u8);
            var optionalName = new PdfName("Optional"u8);
            PdfIndirectReference root = Assert.IsType<PdfIndirectReference>(healthy.Trailer[rootName]);
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(healthy.Resolve(root));
            byte[] damaged = needsRepair ? new PdfIncrementalUpdateBuilder(healthy).ReplaceObject(root.ObjectNumber,
                new PdfDictionary(catalog.Append(new KeyValuePair<PdfName, PdfObject>(optionalName,
                    new PdfIndirectReference(99, 0))))).Build() : healthyBytes;
            string source = Path.Combine(folder, "source.pdf");
            string destination = Path.Combine(folder, "output.pdf");
            File.WriteAllBytes(source, damaged);
            File.WriteAllBytes(destination, "existing destination"u8.ToArray());

            IReadOnlyList<PdfReferenceRepair> repairs = alreadyOpened
                ? PdfEngineIntegration.ResaveDocument(PdfDocument.Open(damaged), destination)
                : PdfEngineIntegration.ResaveDocument(source, destination);
            if (needsRepair) Assert.Equal(99, Assert.Single(repairs).TargetObjectNumber);
            else Assert.Empty(repairs);

            PdfDocument output = PdfDocument.Open(File.ReadAllBytes(destination));
            Assert.Equal("Keep", new PdfPageContentReader(output).Read(0).Text);
            PdfDictionary savedCatalog = Assert.IsType<PdfDictionary>(output.Resolve(
                Assert.IsType<PdfIndirectReference>(output.Trailer[rootName])));
            Assert.False(savedCatalog.ContainsKey(optionalName));
            Assert.Equal(damaged, File.ReadAllBytes(source));
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SanitizationReportsOptionalReferenceRepairsAndKeepsPageContent(bool removeLinks)
    {
        string folder = Path.Combine(Path.GetTempPath(), "killerpdf-sanitize-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            PdfDocument healthy = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
                new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                    .MoveText(10, 50).ShowLatin1Text("Keep").EndText())
                .AddUriLink(0, 10, 20, 50, 10, "https://example.com/").Build());
            PdfIndirectReference root = Assert.IsType<PdfIndirectReference>(healthy.Trailer[new PdfName("Root"u8)]);
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(healthy.Resolve(root));
            byte[] damaged = new PdfIncrementalUpdateBuilder(healthy).ReplaceObject(root.ObjectNumber,
                new PdfDictionary(catalog.Append(new KeyValuePair<PdfName, PdfObject>(new PdfName("Optional"u8),
                    new PdfIndirectReference(99, 0))))).Build();
            string path = Path.Combine(folder, "source.pdf");
            File.WriteAllBytes(path, damaged);

            PdfOptimizationResult result = PdfEngineIntegration.SanitizeFile(path,
                new PdfEngineIntegration.SanitizeChoices(false, false, false, false, false, removeLinks, false));

            Assert.Equal(99, Assert.Single(result.ReferenceRepairs).TargetObjectNumber);
            Assert.Equal(damaged.Length, result.OriginalSize);
            Assert.Equal("Keep", new PdfPageContentReader(PdfDocument.Open(File.ReadAllBytes(path))).Read(0).Text);
            Assert.Equal(removeLinks ? 0 : 1, PdfLinkReader.ReadPage(PdfDocument.Open(File.ReadAllBytes(path)), 0).Count);
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReductionKeepsLiveContentAndPreservesSource(bool needsRepair)
    {
        string folder = Path.Combine(Path.GetTempPath(), "killerpdf-reduction-repair-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            byte[] healthyBytes = new PdfDocumentBuilder().AddPage(100, 100,
                new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                    .MoveText(10, 50).ShowLatin1Text("Keep").EndText()).Build();
            PdfDocument healthy = PdfDocument.Open(healthyBytes);
            PdfIndirectReference root = Assert.IsType<PdfIndirectReference>(healthy.Trailer[new PdfName("Root"u8)]);
            PdfDictionary catalog = Assert.IsType<PdfDictionary>(healthy.Resolve(root));
            byte[] sourceBytes = needsRepair ? new PdfIncrementalUpdateBuilder(healthy).ReplaceObject(root.ObjectNumber,
                new PdfDictionary(catalog.Append(new KeyValuePair<PdfName, PdfObject>(new PdfName("Optional"u8),
                    new PdfIndirectReference(99, 0))))).Build() : healthyBytes;
            string source = Path.Combine(folder, "source.pdf");
            string output = Path.Combine(folder, "reduced.pdf");
            File.WriteAllBytes(source, sourceBytes);

            var result = PdfEngineIntegration.ReduceFileSize(source, output, null, CancellationToken.None);

            Assert.Equal(sourceBytes.Length, result.Before);
            if (needsRepair) Assert.Equal(99, Assert.Single(result.ReferenceRepairs).TargetObjectNumber);
            else Assert.Empty(result.ReferenceRepairs);
            Assert.Equal(new FileInfo(output).Length, result.After);
            Assert.Equal("Keep", new PdfPageContentReader(PdfDocument.Open(File.ReadAllBytes(output))).Read(0).Text);
            Assert.Equal(sourceBytes, File.ReadAllBytes(source));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
