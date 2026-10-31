using System;
using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// A signed file or one with Reader usage rights is refused by default, because nobody should lose
// them by accident. The command line has no dialog to ask in, so --optimize takes the same
// --invalidate-signatures flag --protect does, and the refusal names it.
public sealed class CliOptimizeSignatureConsentTests
{
    [Fact]
    public void OptimizeTakesTheFlagIntoTheEnginesAllowOption()
    {
        string optimize = OptimizeSource();

        Assert.Contains("AllowSignatureInvalidation = options.ContainsKey(\"--invalidate-signatures\")", optimize, StringComparison.Ordinal);
    }

    [Fact]
    public void OptimizeRefusalNamesTheFlagAndWritesNothing()
    {
        string optimize = OptimizeSource();
        int refusal = optimize.IndexOf("PdfRewriteRefusal.Matches(ex)", StringComparison.Ordinal);
        int publish = optimize.IndexOf("CliPublishOutput(", StringComparison.Ordinal);

        Assert.True(refusal > 0, "--optimize does not recognize the writer's refusal.");
        Assert.True(publish > refusal, "The refusal must be handled before any output is written.");
        Assert.Contains("Add --invalidate-signatures to go ahead.", optimize[refusal..publish], StringComparison.Ordinal);
        Assert.Contains("return 1;", optimize[refusal..publish], StringComparison.Ordinal);
    }

    [Fact]
    public void TheUsageLineAndTheHelpTextListTheFlag()
    {
        string runner = File.ReadAllText(Path.Combine(Root(), "Features", "Cli", "CliRunner.cs"));
        string optimize = OptimizeSource();

        Assert.Contains("[--jpeg-quality <n>] [--invalidate-signatures] [--report]", optimize, StringComparison.Ordinal);
        int help = runner.IndexOf("\"  killerpdf optimize <in.pdf>", StringComparison.Ordinal);
        int next = runner.IndexOf("\"  killerpdf number-pages", StringComparison.Ordinal);
        Assert.True(help > 0 && next > help);
        Assert.Contains("[--invalidate-signatures]", runner[help..next], StringComparison.Ordinal);
    }

    private static string OptimizeSource()
    {
        string runner = File.ReadAllText(Path.Combine(Root(), "Features", "Cli", "CliRunner.cs"));
        int start = runner.IndexOf("private static int CliOptimize(", StringComparison.Ordinal);
        int end = runner.IndexOf("private static int CliImpose(", StringComparison.Ordinal);
        Assert.True(start > 0 && end > start);
        return runner[start..end];
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Strings", "en-US.xaml")))
            dir = dir.Parent;
        Assert.True(dir != null, "could not locate the repo root from the test output folder");
        return dir!.FullName;
    }
}
