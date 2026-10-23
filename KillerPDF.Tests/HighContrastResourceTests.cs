using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

public sealed class HighContrastResourceTests
{
    // Hovered menu and tool-search text drew a theme accent on the system window color in High
    // Contrast; it pairs with the overlay's WindowBrush hover fill as window text instead.
    [Fact]
    public void HoveredMenuTextUsesTheSystemWindowTextColor()
    {
        string source = File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(Path.GetDirectoryName(SourcePath()))!, "Controls", "HighContrastResources.cs"));
        string Group(string brush) => string.Concat(Regex.Matches(source,
            @"Brushes\(SystemColors\." + brush + @",([^;]*)\);").Select(match => match.Groups[1].Value));

        Assert.Contains("\"MenuHoverBrush\"", Group("WindowBrush"));
        Assert.Contains("\"MenuHoverTextBrush\"", Group("WindowTextBrush"));
    }

    private static string SourcePath([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
