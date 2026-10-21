using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class ProcessTempFilesTests
    {
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
