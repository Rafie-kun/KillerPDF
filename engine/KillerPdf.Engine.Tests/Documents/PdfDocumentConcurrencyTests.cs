using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfDocumentConcurrencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentReadersResolveOneUncachedObjectWithoutReportingACycle(bool recovery)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(16, 16, Array.Empty<byte>()).Build());
        var update = new PdfIncrementalUpdateBuilder(source);
        var reference = update.AddObject(new PdfArray(Enumerable.Range(0, 20_000)
            .Select(number => (PdfObject)new PdfInteger(number))));
        byte[] bytes = update.Build();
        var document = recovery ? PdfDocument.OpenWithCompatibilityRecovery(bytes) : PdfDocument.Open(bytes);
        using var start = new Barrier(8);
        var readers = Enumerable.Range(0, 8).Select(index => Task.Factory.StartNew(() =>
        {
            if (!start.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Readers did not start.");
            return index % 2 == 0 ? document.Resolve(reference) : document.Resolve(reference.ObjectNumber);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        PdfObject[] results = await Task.WhenAll(readers);
        var array = Assert.IsType<PdfArray>(results[0]);
        Assert.Equal(20_000, array.Count);
        Assert.Equal(19_999, Assert.IsType<PdfInteger>(array[^1]).Value);
        Assert.All(results, result => Assert.Same(array, result));
        Assert.Same(array, document.Resolve(reference));
    }
}
