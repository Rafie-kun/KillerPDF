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

        // A retained session used to be kept forever; it now expires after thirty days.
        [Fact]
        public void RecoveryRetentionExpiresAfterThirtyDays()
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-retention-expiry-" + Path.GetRandomFileName());
            var temps = new ProcessTempFiles(root);
            try
            {
                string working = temps.CreatePath("working");
                File.WriteAllText(working, "unsaved working bytes");
                string directory = temps.DirectoryPath;
                temps.RetainForRecovery(null);
                temps.Dispose();

                using (var nextLaunch = new ProcessTempFiles(root)) nextLaunch.CleanupStale();
                Assert.True(File.Exists(working), "A fresh retention was cleaned up.");

                File.SetLastWriteTimeUtc(Path.Combine(directory, "recovery-retained.json"),
                    DateTime.UtcNow.AddDays(-31));
                using (var monthLater = new ProcessTempFiles(root)) monthLater.CleanupStale();
                Assert.False(Directory.Exists(directory), "An expired retention kept its session forever.");
            }
            finally
            {
                temps.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        // A full disk can refuse the retention marker as well as the recovery copies. The restarted
        // process is then told which session it replaces, keeps that folder and marks it for later
        // launches, instead of deleting the working files its recovery message points at.
        [Fact]
        public void NamedRetainedSessionSurvivesWithoutItsMarker()
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-retained-named-" + Path.GetRandomFileName());
            var temps = new ProcessTempFiles(root);
            try
            {
                string working = temps.CreatePath("working");
                File.WriteAllText(working, "unsaved working bytes");
                string directory = temps.DirectoryPath;
                temps.RetainForRecovery(null);
                File.Delete(Path.Combine(directory, "recovery-retained.json"));
                Assert.Equal(directory, temps.RetainedDirectory);
                temps.Dispose();

                using (var restarted = new ProcessTempFiles(root)) restarted.CleanupStale(directory);
                Assert.True(File.Exists(working), "The restart deleted the session it was told to keep.");
                using (var nextLaunch = new ProcessTempFiles(root)) nextLaunch.CleanupStale();
                Assert.True(File.Exists(working), "The kept session was not marked for later launches.");
            }
            finally
            {
                temps.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }

        [Fact]
        public void UnretainedSessionNamesNoDirectory()
        {
            string root = Path.Combine(Path.GetTempPath(), "kp-unretained-" + Path.GetRandomFileName());
            using var temps = new ProcessTempFiles(root);
            try
            {
                _ = temps.CreatePath("working");
                Assert.Null(temps.RetainedDirectory);
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
