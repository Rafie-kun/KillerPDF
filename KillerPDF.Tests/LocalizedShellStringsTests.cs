using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// English that the shell showed directly instead of through a string key: the read-only and
/// repair prompts on open (BF-S012 to BF-S014), the document and image export filters (BF-S015,
/// BF-S016), the "Untitled" tab title (BF-S017), a US-only date pattern in the recent files list
/// (BF-S018), the macro output name error (BF-S019) and seven "PDF|*.pdf" dialog filters where
/// Open already used Str_Filter_Pdf (BF-S020).
/// </summary>
public sealed class LocalizedShellStringsTests
{
    [Fact]
    public void TheOpenPromptsComeFromStringKeys()
    {
        string open = Method("OpenFile", "(string path)");
        Assert.DoesNotContain("has a non-standard structure and was opened read-only", open);
        Assert.DoesNotContain("This PDF couldn't be opened", open);
        Assert.Contains("string.Format(Loc(\"Str_Dlg_OpenedReadOnlyXRef\"), System.IO.Path.GetFileName(path))", open);
        // Both repair offers on open say "opened"; Str_Dlg_RepairAsk ("added") stays with the import paths.
        Assert.Equal(2, Regex.Matches(open, @"Loc\(""Str_Dlg_RepairAskOpen""\)").Count);
        Assert.DoesNotContain("\"Str_Dlg_RepairAsk\"", open);
    }

    [Fact]
    public void TheDocumentExportFilterComesFromStringKeys()
    {
        string export = Method("ExportDocument_Click", "(object sender, RoutedEventArgs e)", "async void");
        foreach (string literal in new[] { "\"Word document\"", "\"Excel workbook\"", "\"PowerPoint presentation\"",
                     "\"HTML document\"", "\"Markdown document\"", "\"Text document\"", "\"JSON document\"", "\"Document\"" })
            Assert.DoesNotContain(literal, export);
        foreach (string key in new[] { "Str_Filter_Word", "Str_Filter_Excel", "Str_Filter_PowerPoint",
                     "Str_Filter_Html", "Str_Filter_Markdown", "Str_Filter_Json", "Str_Filter_Text" })
            Assert.Contains($"Loc(\"{key}\")", export);
    }

    [Fact]
    public void TheImageExportFilterReusesTheImagesKey()
    {
        string export = Method("ExportImagesFlow", "(string presetRange)", "async Task");
        Assert.DoesNotContain("\"JPEG image|*.jpg\"", export);
        Assert.DoesNotContain("\"PNG image|*.png\"", export);
        Assert.Contains("Loc(\"Str_Filter_Images\")", export);
    }

    [Fact]
    public void EveryPdfDialogFilterUsesThePdfFilterKey()
    {
        string root = FindRepositoryRoot();
        var offenders = new[] { "Shell", "Controls" }
            .SelectMany(folder => Directory.EnumerateFiles(Path.Combine(root, folder), "*.cs", SearchOption.AllDirectories))
            .Where(file => File.ReadAllText(file).Contains("Filter = \"PDF|", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file))
            .ToList();
        Assert.True(offenders.Count == 0, "dialogs with a hard-coded PDF filter: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheUntitledTabTitleComesFromAStringKey()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Controls", "Viewer", "PdfViewer.Tabs.cs"));
        Assert.DoesNotMatch(@"(?<!\?)\? ""Untitled""", source);
        Assert.DoesNotContain("= \"Untitled\";", source);
        Assert.DoesNotContain("OriginalFile ?? \"Untitled\"", source);
        // The English word stays only as the stand-in where no application is running.
        Assert.Contains("TryFindResource(\"Str_Untitled\") as string ?? \"Untitled\"", source);
    }

    [Fact]
    public void TheRecentFilesDateFollowsTheCulture()
    {
        var written = new DateTime(2026, 10, 29, 9, 30, 0);
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("29.10.2026", RecentFilesList.DateLabel(written));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("10/29/2026", RecentFilesList.DateLabel(written));
        }
        finally { CultureInfo.CurrentCulture = previous; }

        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "FileOperations.cs"));
        Assert.DoesNotContain(":MMM d, yyyy}", source);
        Assert.Contains("Services.RecentFilesList.DateLabel(System.IO.File.GetLastWriteTime(path))", source);
    }

    [Fact]
    public void TheMacroOutputNameErrorComesFromAStringKey()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "Macros.cs"));
        Assert.DoesNotContain("\"The macro output name differs from its approved plan.\"", source);
        Assert.Contains("throw new InvalidOperationException(Loc(\"Str_Macro_OutputNameDrift\"));", source);
    }

    [Fact]
    public void TheNewKeysAreDefinedInEnglish()
    {
        string english = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Strings", "en-US.xaml"));
        foreach (string key in new[] { "Str_Dlg_OpenedReadOnlyXRef", "Str_Dlg_RepairAskOpen", "Str_Filter_Word",
                     "Str_Filter_Excel", "Str_Filter_PowerPoint", "Str_Filter_Html", "Str_Filter_Markdown",
                     "Str_Filter_Json", "Str_Untitled", "Str_Macro_OutputNameDrift" })
            Assert.True(english.Contains($"x:Key=\"{key}\"", StringComparison.Ordinal), $"en-US.xaml has no {key}.");
    }

    private static string Method(string name, string parameters, string returnType = "void")
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "FileOperations.cs"));
        Match method = Regex.Match(source,
            Regex.Escape(returnType) + " " + name + Regex.Escape(parameters) + @"\s*\{(?<body>.*?)\n        \}",
            RegexOptions.Singleline);
        Assert.True(method.Success, $"{name} was not found.");
        return method.Groups["body"].Value;
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
