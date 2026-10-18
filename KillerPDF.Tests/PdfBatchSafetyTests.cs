using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfBatchSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MergeImageFailureIsIsolatedAndCancellationPreservesExistingOutputs(bool cancel)
    {
        string folder = Path.Combine(Path.GetTempPath(), "killerpdf-batch-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var cancellation = new CancellationTokenSource();
        try
        {
            byte[] template = new PdfDocumentBuilder().AddBlankPage()
                .AddTextField(0, "Name", 20, 20, 120, 24).Build();
            var profile = new PdfDataMergeProfile("Images", [new("Name", "Name")], "{{Name}}.pdf",
                imageMappings: [new("Image", 0, 20, 80, 20, 20)]);
            var plan = PdfDataMergeWorkflow.CreatePlan(PdfDocument.Open(template),
                [new Dictionary<string, string?> { ["Name"] = "Ada", ["Image"] = "missing" },
                 new Dictionary<string, string?> { ["Name"] = "Grace", ["Image"] = "valid" }], "letter.pdf", profile);
            string existing = Path.Combine(folder, "Ada.pdf");
            byte[] original = [1, 2, 3, 4];
            File.WriteAllBytes(existing, original);
            int imageCalls = 0;

            var report = PdfDataMergeWorkflow.RunToDirectory(template, plan, folder,
                PdfDataMergeOutputMode.Editable, cancellation.Token, value =>
                {
                    imageCalls++;
                    if (value == "missing")
                    {
                        if (cancel) { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); }
                        throw new FileNotFoundException("The requested image is missing.");
                    }
                    return PdfImage.FromRgba(1, 1, new byte[] { 255, 0, 0, 255 });
                }, new HashSet<string> { existing });

            Assert.Equal(original, File.ReadAllBytes(existing));
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
            if (cancel)
            {
                Assert.Equal(1, imageCalls);
                Assert.Equal(0, report.SucceededRecords);
                Assert.False(File.Exists(Path.Combine(folder, "Grace.pdf")));
            }
            else
            {
                Assert.Equal(1, report.FailedRecords);
                Assert.Equal(1, report.SucceededRecords);
                var output = PdfDocument.Open(File.ReadAllBytes(Path.Combine(folder, "Grace.pdf")));
                Assert.Equal("Grace", Assert.Single(PdfFormDataExporter.Export(output).Fields).Values[0]);
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MergePreservesBlockedOutputAndContinuesWithTheNextRecord(bool locked)
    {
        string folder = Path.Combine(Path.GetTempPath(), "killerpdf-batch-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            byte[] template = new PdfDocumentBuilder().AddBlankPage()
                .AddTextField(0, "Name", 20, 20, 120, 24).Build();
            var plan = PdfDataMergeWorkflow.CreatePlan(PdfDocument.Open(template),
                [new Dictionary<string, string?> { ["Name"] = "Ada" },
                 new Dictionary<string, string?> { ["Name"] = "Grace" }], "letter.pdf");
            string existing = Path.Combine(folder, "letter.pdf");
            byte[] original = [1, 2, 3, 4];
            File.WriteAllBytes(existing, original);

            using var heldFile = locked ? File.Open(existing, FileMode.Open, FileAccess.Read, FileShare.None) : null;
            var report = PdfDataMergeWorkflow.RunToDirectory(template, plan, folder,
                PdfDataMergeOutputMode.Editable, CancellationToken.None,
                approvedOverwrites: locked ? new HashSet<string> { existing } : new HashSet<string>());
            heldFile?.Dispose();

            Assert.Equal(1, report.FailedRecords);
            Assert.Equal(1, report.SucceededRecords);
            Assert.Equal(original, File.ReadAllBytes(existing));
            var result = PdfDocument.Open(File.ReadAllBytes(Path.Combine(folder, "letter-2.pdf")));
            Assert.Equal("Grace", Assert.Single(PdfFormDataExporter.Export(result).Fields).Values[0]);
            Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }
}
