using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Translation parity proves the twenty locale files agree with each other, not that the code's
/// keys exist. A key missing from en-US shows the raw key name on screen (Loc returns the key) or
/// leaves an accessible name empty, in every language at once.
/// </summary>
public sealed class StringKeyCoverageTests
{
    [Fact]
    public void EveryStringKeyTheAppNamesIsDefinedInEnglish()
    {
        string root = FindRepositoryRoot();
        var defined = Regex.Matches(File.ReadAllText(Path.Combine(root, "Strings", "en-US.xaml")), @"x:Key=""(Str_\w+)""")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var missing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in AppSources(root))
        {
            // Keys built from a prefix ("Str_Color_" + accent) end in an underscore and are skipped.
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"""(Str_\w*[^_\W])""|Resource (Str_\w*[^_\W])\}"))
            {
                string key = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                if (!defined.Contains(key)) missing.Add($"{key} ({Path.GetRelativePath(root, file)})");
            }
        }
        Assert.True(missing.Count == 0, "String keys used but not defined in Strings/en-US.xaml:\n" + string.Join("\n", missing));
    }

    private static IEnumerable<string> AppSources(string root)
    {
        string[] skipped = ["bin", "obj", "engine", "third_party", "tests", "tools", "KillerPDF.Tests", "Strings", "pdf-landing", "validation", "Packaging"];
        return Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .Where(file => !Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar)
                .Any(part => skipped.Contains(part, StringComparer.OrdinalIgnoreCase)));
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
