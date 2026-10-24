using System;
using System.IO;
using System.Text.Json;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class FileRevisionTests
    {
        [Fact]
        public void AnOpenWriterDoesNotMakeAReplaceableDestinationAppearMissing()
        {
            string path = Path.Combine(Path.GetTempPath(), "kp-revision-" + Path.GetRandomFileName() + ".pdf");
            try
            {
                File.WriteAllBytes(path, [1, 2, 3]);
                using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete);
                FileRevision? existing = FileRevision.Capture(path);

                AtomicFileOutput.Write(path, new byte[] { 3, 2, 1 });
                Assert.Equal(new byte[] { 3, 2, 1 }, File.ReadAllBytes(path));
                Assert.NotNull(existing);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void TwoUnreadableSnapshotsStillRequireConfirmation()
        {
            string path = Path.Combine(Path.GetTempPath(), "kp-revision-" + Path.GetRandomFileName() + ".pdf");
            try
            {
                File.WriteAllBytes(path, [1, 2, 3]);
                using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete);
                FileRevision? approved = FileRevision.Capture(path);

                Assert.NotNull(approved);
                Assert.Null(approved.Value.Sha256);
                Assert.Equal(approved, FileRevision.Capture(path));
                Assert.True(FileRevision.ConflictsWith(approved, path));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void LegacyRecoveryRevisionRequiresConfirmationForAnExistingFile()
        {
            string path = Path.Combine(Path.GetTempPath(), "kp-revision-" + Path.GetRandomFileName() + ".pdf");
            try
            {
                File.WriteAllBytes(path, [1, 2, 3]);
                FileRevision loaded = Assert.IsType<FileRevision>(FileRevision.Capture(path));
                string legacyJson = JsonSerializer.Serialize(new { loaded.Length, loaded.LastWriteUtc });
                FileRevision legacy = JsonSerializer.Deserialize<FileRevision>(legacyJson);

                Assert.Equal(loaded.Length, legacy.Length);
                Assert.Equal(loaded.LastWriteUtc, legacy.LastWriteUtc);
                Assert.Null(legacy.Sha256);
                Assert.True(FileRevision.ConflictsWith(legacy, path));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void RequiresConfirmationWhenAnExistingRevisionCannotBeRead()
        {
            string path = Path.Combine(Path.GetTempPath(), "kp-revision-" + Path.GetRandomFileName() + ".pdf");
            try
            {
                File.WriteAllBytes(path, [1, 2, 3]);
                FileRevision loaded = Assert.IsType<FileRevision>(FileRevision.Capture(path));
                using var busy = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

                FileRevision unreadable = Assert.IsType<FileRevision>(FileRevision.Capture(path));
                Assert.Null(unreadable.Sha256);
                Assert.True(FileRevision.ConflictsWith(loaded, path));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void DetectsChangedContentWhenLengthAndTimestampArePreserved()
        {
            string path = Path.Combine(Path.GetTempPath(), "kp-revision-" + Path.GetRandomFileName() + ".pdf");
            try
            {
                File.WriteAllBytes(path, [1, 2, 3]);
                FileRevision loaded = Assert.IsType<FileRevision>(FileRevision.Capture(path));

                File.WriteAllBytes(path, [3, 2, 1]);
                File.SetLastWriteTimeUtc(path, loaded.LastWriteUtc);
                Assert.Equal(loaded.Length, new FileInfo(path).Length);
                Assert.Equal(loaded.LastWriteUtc, File.GetLastWriteTimeUtc(path));

                Assert.True(FileRevision.ConflictsWith(loaded, path));
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void DetectsAReplacementTheDocumentNeverSaw()
        {
            string path = Path.Combine(Path.GetTempPath(), "kp-revision-" + Path.GetRandomFileName() + ".pdf");
            try
            {
                Assert.False(FileRevision.ConflictsWith(null, path));   // nothing there yet

                File.WriteAllBytes(path, [1, 2, 3]);
                FileRevision? loaded = FileRevision.Capture(path);
                Assert.False(FileRevision.ConflictsWith(loaded, path));
                Assert.True(FileRevision.ConflictsWith(null, path));

                // Another copy of the document publishes over the file.
                File.WriteAllBytes(path, [1, 2, 3, 4]);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
                Assert.True(FileRevision.ConflictsWith(loaded, path));
            }
            finally { File.Delete(path); }
        }
    }
}
