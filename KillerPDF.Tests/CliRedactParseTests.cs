using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The command-line Redact searches form field values and annotation text in one engine document
/// and, with --marked, reads the saved redaction marks from the same one. Each full parse of a
/// large file costs batch scripts real time, so the two reads share a single parse (BF-A009).
/// </summary>
public sealed class CliRedactParseTests
{
    [Fact]
    public void TheAnnotationSearchAndTheSavedMarksShareOneParse()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Features", "Cli", "CliRunner.Redact.cs"));
        int start = source.IndexOf("var areas = new List<", StringComparison.Ordinal);
        int end = source.IndexOf("if (options.ContainsKey(\"--dry-run\"))", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "The area collection of CliRedact was not found.");
        string collection = source.Substring(start, end - start);

        Assert.Single(Regex.Matches(collection, @"OpenForReading\(File\.ReadAllBytes\(source\)\)"));
        Assert.DoesNotMatch(@"ReadRedactionMarks\(\s*PdfEngineIntegration\.OpenForReading", collection);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("KillerPDF.csproj was not found above the test output.");
    }
}
