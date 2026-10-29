using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Full screen hides the sidebar and zeroes its column. F9 and Shift+F9 still reached the sidebar
/// toggle and the side switch, which opened a blank column over the page and flipped the collapsed
/// state behind the full-screen view, so leaving full screen restored an inconsistent layout until
/// F9 was pressed again (BF-S007). Both keys are ignored while full screen is on.
/// </summary>
public sealed class SidebarFullScreenShortcutTests
{
    [Theory]
    [InlineData("ModifierKeys.Shift")]
    [InlineData("ModifierKeys.None")]
    public void TheSidebarKeysAreIgnoredInFullScreen(string modifiers)
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "KeyboardShortcuts.cs"));
        Match branch = Regex.Match(source, @"else if \(key == Key\.F9 && mods == " + Regex.Escape(modifiers) + @"([^)]*)\)");
        Assert.True(branch.Success, $"The F9 branch for {modifiers} was not found.");
        Assert.Contains("&& !_fullScreen", branch.Groups[1].Value);
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
