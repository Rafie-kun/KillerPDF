using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// A form dropdown sits on the page, so it wears the page's field colors like the text fields do.
/// The kit's implicit ComboBox style paints ComboFieldBrush, which is near black in the dark themes,
/// and the overlay forces black text, so without its own fill the chosen value vanished.
/// </summary>
public sealed class FormDropdownColorsTests
{
    [Fact]
    public void TheFormDropdownSetsItsOwnFillAndBorder()
    {
        string forms = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Controls", "Viewer", "PdfViewer.Forms.cs"));
        Match combo = Regex.Match(forms, @"var combo = new ComboBox\s*\{(?<body>.*?)\};", RegexOptions.Singleline);
        Assert.True(combo.Success, "The form dropdown overlay was not found.");
        string body = combo.Groups["body"].Value;
        Assert.Matches(@"\bBackground\s*=", body);
        Assert.Matches(@"\bBorderBrush\s*=", body);
        Assert.Contains("Foreground = Brushes.Black", body);
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
