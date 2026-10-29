using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Starting a comparison opens the second document in the right pane. That open queues its
/// continuous setup on the page it opened at, and the setup leaves a pending target that scrolls
/// back to that page when its slot renders. A comparison started on page 5 therefore showed page 5
/// on the left and the cover on the right. An explicit jump now replaces a pending target, and the
/// comparison jumps the right pane again once the open work has run.
/// </summary>
public sealed class ComparisonStartAlignmentTests
{
    [Fact]
    public void BeginComparisonJumpsTheRightPaneAgainAfterItsOpenSettles()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "PdfComparison.cs"));
        Match begin = Regex.Match(source, @"private void BeginComparison\(.*?\n    \}", RegexOptions.Singleline);
        Assert.True(begin.Success, "BeginComparison was not found.");
        Assert.Matches(@"DispatcherPriority\.ApplicationIdle[^;]*AlignComparisonPanes\(page\)", begin.Value);

        Match align = Regex.Match(source, @"private void AlignComparisonPanes\(int \w+\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline);
        Assert.True(align.Success, "AlignComparisonPanes was not found.");
        Assert.Contains("_comparisonActive", align.Groups["body"].Value);
        Assert.Contains("ViewerB.NavigateToPageExt", align.Groups["body"].Value);
    }

    [Fact]
    public void AContinuousJumpReplacesAPendingRenderTarget()
    {
        string viewport = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Controls", "Viewer", "PdfViewer.Viewport.cs"));
        Match navigate = Regex.Match(viewport, @"private void NavigateContinuousToPage\(int pageIndex\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);
        Assert.True(navigate.Success, "NavigateContinuousToPage was not found.");
        Assert.Matches(@"if \(_continuousScrollTarget >= 0\) _continuousScrollTarget = pageIndex;", navigate.Groups["body"].Value);
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
