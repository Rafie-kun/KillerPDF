using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

// A dialog's title bar shows the card's own face, grain included, so it reads as one surface with
// the body. Only 98SE asks for a classic caption band.
public sealed class DialogCaptionBlendsTests
{
    [Fact]
    public void OnlyClassicPalettesGiveDialogsTheirOwnCaptionBand()
    {
        string themes = Path.Combine(FindRepositoryRoot(), "Themes");
        string[] banded = Directory.EnumerateFiles(themes, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).StartsWith("98SE", StringComparison.Ordinal)
                && !path.Contains(Path.DirectorySeparatorChar + "98SE" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(path => File.ReadAllLines(path).Any(line =>
                line.Contains("x:Key=\"DialogTitleBarBrush\"", StringComparison.Ordinal)
                && !line.Contains("Color=\"Transparent\"", StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(themes, path))
            .ToArray();
        Assert.Empty(banded);
    }

    [Theory]
    [InlineData("ConfirmDialog.xaml")]
    [InlineData("FileDialog.xaml")]
    public void CaptionDoesNotPaintASecondGrainOverTheCard(string file)
    {
        var document = XDocument.Load(Path.Combine(FindRepositoryRoot(), "Controls", file));
        XElement caption = document.Descendants().Single(element =>
            element.Name.LocalName == "Border" &&
            (string?)element.Attribute("Background") == "{DynamicResource DialogTitleBarBrush}");
        Assert.DoesNotContain(caption.Descendants(), element =>
            ((string?)element.Attribute("Background"))?.Contains("GrainTileBrush", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void GeneratedDialogCaptionDoesNotPaintASecondGrain()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Controls", "DialogFrame.cs"));
        int start = source.IndexOf("public static Border BuildTitleBar", StringComparison.Ordinal);
        int end = source.IndexOf("bar.Child =", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        Assert.DoesNotContain("GrainTileBrush", source[start..end], StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the KillerPDF repository root.");
    }
}
