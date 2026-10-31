using System.IO;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps.Packaging;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PrintRasterDocumentTests
{
    [Fact]
    public void SerializedPrintKeepsDistinctPageImagesAndCopies()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), "killerpdf-print-" + Guid.NewGuid().ToString("N") + ".xps");
            try
            {
                using (var document = new PrintRasterDocument(copies: 2) { PageSize = new Size(100, 100) })
                {
                    foreach (byte[] pixel in new byte[][] { [0, 0, 255, 255], [0, 255, 0, 255], [255, 0, 0, 255] })
                    {
                        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixel, 4);
                        bitmap.Freeze();
                        document.Add(bitmap);
                    }
                    Assert.Equal(6, document.PageCount);
                    using var xps = new XpsDocument(path, FileAccess.Write);
                    XpsDocument.CreateXpsDocumentWriter(xps).Write(document);
                }
                using var archive = ZipFile.OpenRead(path);
                Assert.Equal(6, archive.Entries.Count(entry => entry.FullName.EndsWith(".fpage", StringComparison.Ordinal)));
                Assert.True(archive.Entries.Count(entry => entry.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) == 3,
                    string.Join("\n", archive.Entries.Select(entry => entry.FullName)));
            }
            catch (Exception exception) { error = exception; }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Print serialization timed out.");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }
}
