using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

// The About settings sit three rows a side: update check, Close to System Tray and menu shortcuts
// on the left; the two privacy settings and Clear all Data on the right.
public sealed class AboutSettingsColumnsTests
{
    [Fact]
    public void TheLeftColumnHoldsThreeCheckboxesAndTheRightTwoAboveClearAllData()
    {
        string window = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "MainWindow.xaml"));
        int start = window.IndexOf("x:Name=\"AboutStartupUpdateCheck\"", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int split = window.IndexOf("<StackPanel Grid.Column=\"2\">", start, StringComparison.Ordinal);
        int end = window.IndexOf("Click=\"AboutClearData_Click\"", split, StringComparison.Ordinal);
        Assert.True(split > start && end > split);
        string left = window[start..split];
        // The slice starts inside the update checkbox's own tag, so it counts the rows after it.
        Assert.Equal(3, Regex.Matches(left, "<CheckBox ").Count + 1);
        Assert.Contains("AboutCloseToTray", left, StringComparison.Ordinal);
        Assert.Contains("Str_About_ShowShortcutsInMenus", left, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(window[split..end], "<CheckBox ").Count);
    }

    private static string FindRepositoryRoot([System.Runtime.CompilerServices.CallerFilePath] string source = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the KillerPDF repository root.");
    }
}
