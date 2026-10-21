using System;
using System.IO;
using System.Runtime.InteropServices;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class RenderFailureRecoveryTests
    {
        [Fact]
        public void RecognizesTheRenderThreadFailureWhereverItIsWrapped()
        {
            var failure = new COMException("UCEERR_RENDERTHREADFAILURE", RenderFailureRecovery.RenderThreadFailureHResult);

            Assert.True(RenderFailureRecovery.IsRenderThreadFailure(failure));
            Assert.True(RenderFailureRecovery.IsRenderThreadFailure(new InvalidOperationException("outer", failure)));
            Assert.True(RenderFailureRecovery.IsRenderThreadFailure(new AggregateException(new Exception(), failure)));
            Assert.False(RenderFailureRecovery.IsRenderThreadFailure(new COMException("other", unchecked((int)0x80004005))));
            Assert.False(RenderFailureRecovery.IsRenderThreadFailure(null));
        }

        [Fact]
        public void ManifestRoundTripsAndRefusesCopiesOutsideItsFolder()
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-recovery-" + Path.GetRandomFileName());
            try
            {
                string folder = RenderFailureRecovery.CreateFolder(root);
                string copy = RenderFailureRecovery.CopyPath(folder, @"Q3 report: final?.pdf", 0);
                File.WriteAllText(copy, "%PDF-1.7");
                string outside = Path.Combine(root, "outside.pdf");
                File.WriteAllText(outside, "%PDF-1.7");

                string manifest = RenderFailureRecovery.WriteManifest(folder,
                [
                    new RecoveredDocument(@"C:\Docs\Q3 report.pdf", copy, null),
                    new RecoveredDocument(null, outside, "Combined.pdf"),
                    new RecoveredDocument(null, Path.Combine(folder, "gone.pdf"), null)
                ]);

                var read = RenderFailureRecovery.ReadManifest(manifest);
                Assert.Single(read);
                Assert.Equal(@"C:\Docs\Q3 report.pdf", read[0].OriginalPath);
                Assert.Equal(folder, Path.GetDirectoryName(copy));
                Assert.DoesNotContain(':', Path.GetFileName(copy));
                Assert.Empty(RenderFailureRecovery.ReadManifest(Path.Combine(folder, "missing.json")));
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        }
    }
}
