using System;
using System.IO;
using System.Text.RegularExpressions;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// A new blank document has no saved home. It used to be opened with the display name
/// "Untitled.pdf", so Ctrl+S, the Save button and the quit prompt wrote Untitled.pdf into the
/// process working directory without asking (BF-S001). The blank document now goes through the
/// temp-backed import path a browser download uses: the temp file is the display path and the
/// suggested name is remembered, so FinishOpenFile leaves the document without an original file
/// and every save lands in Save As with the suggested name filled in.
/// </summary>
public sealed class NewDocumentSaveHomeTests
{
    [Fact]
    public void ATempPathIsOwnedByCleanupSoItNeverBecomesASavedHome()
    {
        string root = Path.Combine(Path.GetTempPath(), $"killerpdf-newdoc-{Guid.NewGuid():N}");
        using var temps = new ProcessTempFiles(root);
        try
        {
            string path = temps.CreatePath("new");
            Assert.True(temps.Owns(path));
            Assert.False(temps.Owns(Path.Combine(Path.GetTempPath(), "Untitled.pdf")));
            Assert.False(temps.Owns("Untitled.pdf"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void TheNewDocumentOpensAsATempBackedImportWithASuggestedName()
    {
        string body = Method("NewDocument");
        Assert.DoesNotContain("FinishOpenFile(\"Untitled.pdf\"", body);
        Assert.Contains("App.RememberSuggestedName(tempPath, Loc(\"Str_Untitled\") + \".pdf\");", body);
        Assert.Contains("FinishOpenFile(tempPath, tempPath);", body);
        int remember = body.IndexOf("App.RememberSuggestedName(", StringComparison.Ordinal);
        int finish = body.IndexOf("FinishOpenFile(tempPath, tempPath);", StringComparison.Ordinal);
        Assert.True(remember >= 0 && finish > remember, "The suggested name must be remembered before FinishOpenFile takes it.");
    }

    private static string Method(string name)
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "FileOperations.cs"));
        Match method = Regex.Match(source, @"private void " + name + @"\(\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);
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
