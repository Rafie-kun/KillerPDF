using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The Compare list offers the other open tabs, and pane B reopens the chosen one from its path.
/// A document with no saved home (a new blank document, a browser download) has no path to
/// reopen, so listing it ended in the repair prompt (BF-S002). The diff and the right pane also
/// have to read the same file: pane B opens the tab's on-disk file, so the diff compares that
/// file too rather than the tab's working copy with its unsaved page edits (BF-S021).
/// </summary>
public sealed class ComparisonChoiceTests
{
    [Fact]
    public void ATabWithoutASavedHomeIsNotOfferedForComparison()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Controls", "Viewer", "PdfViewer.TabsApi.cs"));
        Match method = Regex.Match(source,
            @"private static ComparisonDocument\? ComparisonDocumentFromSession\(DocumentSession\? session\)\s*\{(?<body>.*?)\n        \}",
            RegexOptions.Singleline);
        Assert.True(method.Success, "ComparisonDocumentFromSession was not found.");
        string body = method.Groups["body"].Value;
        // The old shape fell back to the working copy as the "original", so a new document was listed.
        Assert.DoesNotContain("?? working;", body);
        Assert.Contains("!System.IO.File.Exists(original)", body);
    }

    [Fact]
    public void TheDiffReadsTheFileTheRightPaneOpens()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "PdfComparison.cs"));
        Match begin = Regex.Match(source, @"private void BeginComparison\(.*?\n    \}", RegexOptions.Singleline);
        Assert.True(begin.Success, "BeginComparison was not found.");
        Match diff = Regex.Match(begin.Value, @"_comparisonRightPath = comparison\.(\w+);");
        Match opened = Regex.Match(begin.Value, @"ViewerB\.OpenInNewTabExt\(comparison\.(\w+)\);");
        Assert.True(diff.Success && opened.Success, "The right path assignment or the pane B open was not found.");
        Assert.Equal(opened.Groups[1].Value, diff.Groups[1].Value);
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
