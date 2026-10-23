using KillerPDF.Features;
using Xunit;

namespace KillerPDF.Tests;

// batch-render's summary and exit code are the release gates' pass signal. A page kept by
// --allow-incomplete is still an OK page; it is named and counted, never turned into a failure.
public sealed class BatchRenderTallyTests
{
    [Fact]
    public void CompletePagesAreOkAndTheRunSucceeds()
    {
        var tally = new BatchRenderTally();
        tally.Add("OK", incomplete: false);
        tally.Add("OK", incomplete: false);

        Assert.Equal((2, 0, 0, 0), (tally.Ok, tally.Skipped, tally.Failed, tally.Incomplete));
        Assert.Equal(0, tally.ExitCode(files: 1));
        Assert.Equal("OK", BatchRenderTally.ConsoleStatus("OK", incomplete: false));
    }

    [Fact]
    public void KeptIncompletePageIsOkAndCountedSeparately()
    {
        var tally = new BatchRenderTally();
        tally.Add("OK", incomplete: false);
        tally.Add("OK", incomplete: true);

        Assert.Equal((2, 0, 0, 1), (tally.Ok, tally.Skipped, tally.Failed, tally.Incomplete));
        Assert.Equal(0, tally.ExitCode(files: 1));
        Assert.Equal("INCOMPLETE", BatchRenderTally.ConsoleStatus("OK", incomplete: true));
        Assert.Equal("Done. 1 files, 2 pages OK (1 incomplete, kept by --allow-incomplete), 0 skipped, 0 failed, "
            + "40 ms summed page rendering, 90 ms elapsed.", tally.Summary(files: 1, 40, 90));
    }

    // A page whose diagnostics were classified before its PNG write failed is a failure, not a
    // kept page, and the console must not call it INCOMPLETE.
    [Fact]
    public void FailedWriteIsAFailureWhateverItsDiagnostics()
    {
        var tally = new BatchRenderTally();
        tally.Add("FAIL", incomplete: true);

        Assert.Equal((0, 0, 1, 0), (tally.Ok, tally.Skipped, tally.Failed, tally.Incomplete));
        Assert.Equal(1, tally.ExitCode(files: 1));
        Assert.Equal("FAIL", BatchRenderTally.ConsoleStatus("FAIL", incomplete: true));
        Assert.Equal("Done. 1 files, 0 pages OK, 0 skipped, 1 failed, 0 ms summed page rendering, 5 ms elapsed.",
            tally.Summary(files: 1, 0, 5));
    }

    [Fact]
    public void SkippedFileOrEmptyInputFailsTheRun()
    {
        var tally = new BatchRenderTally();
        tally.Add("OK", incomplete: false);
        tally.Add("SKIP", incomplete: false);

        Assert.Equal((1, 1, 0, 0), (tally.Ok, tally.Skipped, tally.Failed, tally.Incomplete));
        Assert.Equal(1, tally.ExitCode(files: 2));
        Assert.Equal(1, new BatchRenderTally().ExitCode(files: 0));
    }
}
