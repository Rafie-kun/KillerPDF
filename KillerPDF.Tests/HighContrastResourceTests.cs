using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

public sealed class HighContrastResourceTests
{
    // A hovered or keyboard-highlighted menu item, and the selected tool-search row, use the
    // system highlight pair in High Contrast; window colors made them look like every other item.
    [Fact]
    public void HighlightedMenuItemsUseTheSystemHighlightColors()
    {
        string source = File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!, "Controls", "HighContrastResources.cs"));
        // Refresh takes the system pair as parameters named after SystemColors (highlightBrush for
        // SystemColors.HighlightBrush), so match either spelling.
        string Group(string brush) => string.Concat(Regex.Matches(source,
            @"Brushes\((?:SystemColors\." + brush + "|" + char.ToLowerInvariant(brush[0]) + brush[1..]
            + @"),([^;]*)\);").Select(match => match.Groups[1].Value));

        Assert.Contains("\"MenuHoverBrush\"", Group("HighlightBrush"));
        Assert.Contains("\"MenuHoverTextBrush\"", Group("HighlightTextBrush"));
        Assert.DoesNotContain("\"MenuHoverBrush\"", Group("WindowBrush"));
        Assert.DoesNotContain("\"MenuHoverTextBrush\"", Group("WindowTextBrush"));
    }

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
