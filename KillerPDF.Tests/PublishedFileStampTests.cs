using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class PublishedFileStampTests
    {
        [Fact]
        public void MatchesOnlyTheBytesThatWerePublished()
        {
            string path = Path.Combine(Path.GetTempPath(), "kp-stamp-" + Path.GetRandomFileName() + ".pdf");
            try
            {
                File.WriteAllBytes(path, [1, 2, 3]);
                PublishedFileStamp stamp = PublishedFileStamp.Capture(path);
                Assert.True(stamp.Matches());

                File.WriteAllBytes(path, [1, 2, 4]);
                Assert.False(stamp.Matches());

                File.Delete(path);
                Assert.False(stamp.Matches());
            }
            finally { File.Delete(path); }
        }
    }
}
