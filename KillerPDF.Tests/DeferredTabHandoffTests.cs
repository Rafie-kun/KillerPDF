using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// A restored tab that was never clicked is deferred: it has a path and no document. Only
/// SwitchToTab materialized it, and SwitchToTab returns early for the tab that is already active.
/// Tearing such a tab out to a new window (BF-S003) or dragging it into the other pane (BF-S004)
/// made it the lone active tab of its pane without loading it, so the pane showed the empty state
/// and clicking the tab did nothing. Both handoffs now materialize a deferred tab the way the
/// update restart does. The quit prompt's Save for a dirty tab in the collapsed pane B also has to
/// open the split first, so the document being saved is on screen and a cancelled Save As leaves
/// the toolbar on a visible pane (BF-S005).
/// </summary>
public sealed class DeferredTabHandoffTests
{
    [Fact]
    public void ATornOutDeferredTabIsMaterializedInItsNewWindow()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "MainWindow.xaml.cs"));
        Match branch = Regex.Match(source, @"if \(_startupTearOutSession is \{ \} tornOut\)\s*\{(?<body>.*?)\n                \}", RegexOptions.Singleline);
        Assert.True(branch.Success, "The tear-out startup branch was not found.");
        string body = branch.Groups["body"].Value;
        Assert.Matches(@"if \(tornOut\.Doc == null && tornOut\.DeferredPath != null\) MaterializeDeferred\(tornOut\);\s*else RenderActiveSession\(\);", body);
    }

    [Fact]
    public void ADeferredTabDraggedIntoTheOtherPaneIsMaterializedThere()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "PaneDrag.cs"));
        Match method = Regex.Match(source, @"internal void MoveTabToPane\(.*?\n        \}", RegexOptions.Singleline);
        Assert.True(method.Success, "MoveTabToPane was not found.");
        Assert.Matches(@"if \(s\.Doc == null && s\.DeferredPath != null\) target\.MaterializeDeferredExt\(s\);\s*else target\.RenderActiveSessionExt\(\);", method.Value);
    }

    [Fact]
    public void SavingADirtyTabInTheCollapsedPaneOpensTheSplitFirst()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "WindowChrome.cs"));
        Match method = Regex.Match(source, @"private bool SaveAllDirtyDocuments\(\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);
        Assert.True(method.Success, "SaveAllDirtyDocuments was not found.");
        string body = method.Groups["body"].Value;
        int open = body.IndexOf("if (ReferenceEquals(pane, ViewerB) && !_isSplit) OpenSplit();", StringComparison.Ordinal);
        int save = body.IndexOf("pane.SaveSessionForCloseExt(session)", StringComparison.Ordinal);
        Assert.True(open >= 0 && save > open, "Pane B's dirty tabs are saved while the split is still collapsed.");
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
