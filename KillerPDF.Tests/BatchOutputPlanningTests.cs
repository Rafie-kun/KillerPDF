using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class BatchOutputPlanningTests
{
    [Fact]
    public void BatchOutputsAreUniqueAndCannotReplaceAnySource()
    {
        string[] sources = [@"C:\one\same.pdf", @"C:\two\same.pdf"];
        var outputs = PdfEngineIntegration.PlanBatchOutputs(sources, @"C:\out\result.pdf");
        Assert.Equal(new[] { @"C:\out\result.pdf", @"C:\out\result-2.pdf" }, outputs);
        Assert.Throws<ArgumentException>(() => PdfEngineIntegration.PlanBatchOutputs(
            [@"C:\out\result-2.pdf", @"C:\other.pdf"], @"C:\out\result.pdf"));
    }
}
