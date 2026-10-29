using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Highlighting a page with no text layer suggests the Shapes tool and names its key. Tools moved from
/// the number row to letters in 1.9 and Shapes is O, but the hint still said 4 in every language.
/// </summary>
public sealed class NoTextHintKeyTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void TheNoTextHintNamesTheShapesKeyInEveryLanguage()
    {
        string table = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Services", "ShortcutTable.cs"));
        Assert.Matches(new System.Text.RegularExpressions.Regex(@"B\(""O"",\s*""Str_Lbl_Shape"""), table);
        foreach (string file in Directory.GetFiles(Path.Combine(FindRepositoryRoot(), "Strings"), "*.xaml"))
        {
            string? hint = XDocument.Load(file).Descendants()
                .FirstOrDefault(e => (string?)e.Attribute(Xaml + "Key") == "Str_St_NoTextUseShapes")?.Value;
            Assert.True(hint is not null, $"{Path.GetFileName(file)} has no Str_St_NoTextUseShapes.");
            Assert.DoesNotContain("4", hint);
            Assert.DoesNotContain("৪", hint);
            Assert.Contains("O", hint);
        }
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
