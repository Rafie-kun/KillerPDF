using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class ProcessTempFilesTests
    {
        [Fact]
        public void RecoveryRetentionProtectsWorkingBytesFromBothCleanupPaths()
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-retained-" + Path.GetRandomFileName());
            var temps = new ProcessTempFiles(root);
            try
            {
                string working = temps.CreatePath("working");
                File.WriteAllText(working, "available unsaved working bytes");
                File.WriteAllText(Path.Combine(temps.DirectoryPath, "recovery-retained.json"),
                    "{\"Reason\":\"A recovery snapshot failed.\"}");

                temps.CleanupOwned();
                Assert.True(File.Exists(working), "Current-process cleanup removed retained recovery bytes.");
                temps.Dispose();
                using var nextLaunch = new ProcessTempFiles(root);
                nextLaunch.CleanupStale();
                Assert.True(File.Exists(working), "Next-launch cleanup removed retained recovery bytes.");
                Assert.Equal("available unsaved working bytes", File.ReadAllText(working));
            }
            finally
            {
                temps.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void OwnsOnlyPathsUnderItsRoot()
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-temps-" + Path.GetRandomFileName());
            using var temps = new ProcessTempFiles(root);
            try
            {
                string created = temps.CreatePath("browser");

                Assert.True(temps.Owns(created));
                Assert.False(temps.Owns(Path.Combine(Path.GetTempPath(), "invoice.pdf")));
                Assert.False(temps.Owns(root + "-sibling" + Path.DirectorySeparatorChar + "x.pdf"));
                Assert.False(temps.Owns("invoice.pdf"));
            }
            finally
            {
                temps.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }
}
