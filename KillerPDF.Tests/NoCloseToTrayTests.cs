using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

// KillerPDF does not offer Close to System Tray (Steve, 2026-10-29): a hidden window did not come
// back with its documents when KillerPDF was opened again. About shows two settings per column.
public sealed class NoCloseToTrayTests
{
    [Fact]
    public void AboutOffersNoTrayOptionAndClosingAlwaysCloses()
    {
        string root = FindRepositoryRoot();
        string window = File.ReadAllText(Path.Combine(root, "Shell", "MainWindow.xaml"));
        Assert.DoesNotContain("AboutCloseToTray", window, StringComparison.Ordinal);
        Assert.DoesNotContain("Str_About_CloseToTray", window, StringComparison.Ordinal);

        string code = File.ReadAllText(Path.Combine(root, "Shell", "MainWindow.xaml.cs"));
        Assert.Contains("CloseToTray.Attach(this, \"KillerPDF\", Loc, () => false);", code, StringComparison.Ordinal);
        string about = File.ReadAllText(Path.Combine(root, "Shell", "About.Extras.cs"));
        Assert.DoesNotContain("IAboutTray", about, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAboutSettingsColumnsHoldTwoCheckboxesEach()
    {
        string window = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "MainWindow.xaml"));
        int start = window.IndexOf("x:Name=\"AboutStartupUpdateCheck\"", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int split = window.IndexOf("<StackPanel Grid.Column=\"2\">", start, StringComparison.Ordinal);
        int end = window.IndexOf("Click=\"AboutClearData_Click\"", split, StringComparison.Ordinal);
        Assert.True(split > start && end > split);
        // The slice starts inside the update checkbox's own tag, so it counts the rows after it.
        Assert.Equal(2, Regex.Matches(window[start..split], "<CheckBox ").Count + 1);
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
