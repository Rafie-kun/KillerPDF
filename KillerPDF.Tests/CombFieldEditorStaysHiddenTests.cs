using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// A comb field draws one character per printed cell and keeps its real editor's text transparent.
/// Formatted values repaint a field's text black (red for a negative number), and when that reached
/// the comb editor its raw string showed on top of the cells, so every digit appeared twice.
/// </summary>
public sealed class CombFieldEditorStaysHiddenTests
{
    [Fact]
    public void FormValueTextIsColoredOnlyThroughTheCombAwareHelper()
    {
        string viewer = Path.Combine(FindRepositoryRoot(), "Controls", "Viewer");
        string forms = File.ReadAllText(Path.Combine(viewer, "PdfViewer.Forms.cs"));
        string calc = File.ReadAllText(Path.Combine(viewer, "PdfViewer.FormCalc.cs"));

        Match helper = Regex.Match(forms, @"void SetFormValueForeground\(TextBox \w+, Brush \w+\)\s*\{(?<body>.*?)\n        \}",
            RegexOptions.Singleline);
        Assert.True(helper.Success, "SetFormValueForeground was not found.");
        Assert.Contains("CombTextBox", helper.Groups["body"].Value);
        string formsOutsideHelper = forms.Remove(helper.Index, helper.Length);
        Assert.DoesNotMatch(@"\b(tb|box)\.Foreground\s*=", formsOutsideHelper);
        Assert.DoesNotMatch(@"\b(tb|box)\.Foreground\s*=", calc);
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
