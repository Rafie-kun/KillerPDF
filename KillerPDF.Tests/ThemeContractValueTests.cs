using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Values the family theme contract fixes, stated in the kit palette itself. Left to the theme
/// engine's derivation, ContentPaneBrush becomes PaneBrush and FilenameBrush becomes MutedTextBrush,
/// which darkens Malaise's document pane and puts a dark file name on 98SE's dark title bars.
/// </summary>
public sealed class ThemeContractValueTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void MalaiseDocumentPaneIsTheLighterOpaqueContentSurface()
    {
        Assert.Equal("#303638", BrushColor("Malaise.xaml", "ContentPaneBrush"));
        Assert.NotEqual(BrushColor("Malaise.xaml", "PaneBrush"), BrushColor("Malaise.xaml", "ContentPaneBrush"));
    }

    [Fact]
    public void The98SEFileNameReadsOnEveryTitleBar()
    {
        // Every 98SE title bar is a dark gradient except Yellow's pale one.
        Assert.Equal("#e8e8e8", BrushColor("98SE.xaml", "FilenameBrush"));
        Assert.Equal("#24231C", BrushColor(Path.Combine("Accents", "98SE", "Yellow.xaml"), "FilenameBrush"));
    }

    private static string BrushColor(string themeFile, string key)
    {
        var brush = XDocument.Load(Path.Combine(FindRepositoryRoot(), "Themes", themeFile)).Descendants()
            .SingleOrDefault(element => (string?)element.Attribute(Xaml + "Key") == key);
        Assert.True(brush is not null, $"{themeFile} does not declare {key}.");
        return brush!.Attribute("Color")!.Value;
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
