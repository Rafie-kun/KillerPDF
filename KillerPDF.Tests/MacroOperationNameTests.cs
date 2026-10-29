using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The macro preview, the macro editor and the watched-folder dialog name each step through
/// MacroStepEditor.TitleKey. It covered ten operations and the callers fell back to splitting the
/// enum name into English words, so steps such as OCR, Optimize and Redact showed English in every
/// language (BF-S019). Every operation now has a key, and every key exists in en-US.xaml.
/// </summary>
public sealed class MacroOperationNameTests
{
    [Fact]
    public void EveryMacroOperationHasATitleKeyThatExistsInEnglish()
    {
        string root = FindRepositoryRoot();
        string editor = File.ReadAllText(Path.Combine(root, "Controls", "MacroStepEditor.cs"));
        string production = File.ReadAllText(Path.Combine(root, "Controls", "ProductionLayoutDialog.cs"));
        string english = File.ReadAllText(Path.Combine(root, "Strings", "en-US.xaml"));

        Match title = Regex.Match(editor, @"internal static string TitleKey\(PdfMacroOperation operation\) => operation switch\s*\{(?<body>.*?)\n    \};", RegexOptions.Singleline);
        Assert.True(title.Success, "MacroStepEditor.TitleKey no longer returns a key for every operation.");
        var keys = Regex.Matches(title.Groups["body"].Value, @"PdfMacroOperation\.(\w+) => ""(Str_\w+)""")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
        // Imposition steps take their title from the production layout dialog.
        var productionNames = Regex.Matches(production, @"PdfMacroOperation\.(Impose\w+) => ""(\w+)""")
            .ToDictionary(m => m.Groups[1].Value, m => "Str_Production_" + m.Groups[2].Value, StringComparer.Ordinal);
        productionNames["ImposeNUp"] = "Str_Production_NUp";
        Assert.Contains("ProductionLayoutDialog.TitleKey(operation)", title.Groups["body"].Value);

        var missing = new List<string>();
        foreach (PdfMacroOperation operation in Enum.GetValues<PdfMacroOperation>())
        {
            string name = operation.ToString();
            string? key = keys.TryGetValue(name, out string? direct) ? direct
                : productionNames.TryGetValue(name, out string? imposed) ? imposed : null;
            if (key is null) { missing.Add($"{name}: no key"); continue; }
            if (!english.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal)) missing.Add($"{name}: {key} is not in en-US.xaml");
        }
        Assert.True(missing.Count == 0, "macro operations without a localized name: " + string.Join(" | ", missing));
    }

    [Fact]
    public void TheWatchedFolderDialogSharesTheTitleKeys()
    {
        string dialog = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Controls", "WatchedFolderDialog.cs"));
        Assert.DoesNotContain("PdfMacroOperation.Ocr => \"Str_Lbl_Ocr\"", dialog);
        Assert.Contains("MacroStepEditor.TitleKey(operation)", dialog);
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
