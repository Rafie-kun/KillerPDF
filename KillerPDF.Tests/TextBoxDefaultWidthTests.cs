using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// A new text box's width was a fixed 220 canvas units, while its font size was converted from points
/// to canvas units. Canvas units follow the render resolution, so on a large or high-resolution
/// screen the box came out about 85 points wide and wrapped every couple of words. The default width
/// is a point size converted the same way as the font size.
/// </summary>
public sealed class TextBoxDefaultWidthTests
{
    [Fact]
    public void TheDefaultWidthIsAPointSizeConvertedPerPage()
    {
        string viewer = Path.Combine(FindRepositoryRoot(), "Controls", "Viewer");
        string editing = File.ReadAllText(Path.Combine(viewer, "PdfViewer.TextEditing.cs"));
        string bridge = File.ReadAllText(Path.Combine(viewer, "PdfViewer.Bridge2.cs"));

        Assert.DoesNotContain("const double TextBoxDefaultWidth = 220", bridge);
        Assert.Contains("TextBoxDefaultWidthPoints", bridge + editing);
        Match helper = Regex.Match(editing, @"private double TextBoxDefaultWidth\(int \w+\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);
        Assert.True(helper.Success, "TextBoxDefaultWidth(int) was not found.");
        Assert.Contains("PointsPerCanvasUnit", helper.Groups["body"].Value);
        Assert.DoesNotMatch(@"TextBoxDefaultWidth(?![\w(])", editing);
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
