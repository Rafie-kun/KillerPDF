using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// An open tab is compared through its working copy, a temporary file with a generated name. The
/// comparison details named the documents from those paths, so the first line read like
/// "killerpdf_complete_2b6de4....pdf" instead of the file the user opened. They use the titles the
/// comparison bar already shows.
/// </summary>
public sealed class ComparisonReportNamesTests
{
    [Fact]
    public void TheDetailsReportNamesDocumentsByTitleNotByWorkingPath()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "PdfComparison.cs"));
        Match report = Regex.Match(source, @"Task<string> BuildComparisonReportAsync\((?<args>[^)]*)\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline);
        Assert.True(report.Success, "BuildComparisonReportAsync was not found.");
        Assert.DoesNotContain("Path.GetFileName(leftPath)", report.Groups["body"].Value);
        Assert.DoesNotContain("Path.GetFileName(rightPath)", report.Groups["body"].Value);
        Assert.Contains("leftTitle", report.Groups["args"].Value);
        Assert.Contains("rightTitle", report.Groups["args"].Value);
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
