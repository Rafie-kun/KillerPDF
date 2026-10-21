using System;
using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class FileRevisionTests
    {
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
