using System;
using System.IO;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Startup switches to OnLastWindowClose before it can show the duplicate-installation prompt. When
/// that prompt came before the main window it was the app's only window, so answering it, Yes or No,
/// shut KillerPDF down instead of opening it. The prompt waits until the main window is showing.
/// </summary>
public sealed class DuplicateInstallPromptOrderTests
{
    [Fact]
    public void TheDuplicateInstallPromptFollowsTheMainWindow()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "App.xaml.cs"));
        int lastWindowClose = source.IndexOf("ShutdownMode = ShutdownMode.OnLastWindowClose;", StringComparison.Ordinal);
        int show = source.IndexOf("mainWindow.Show();", lastWindowClose, StringComparison.Ordinal);
        int prompt = source.IndexOf("OfferInstallConflictRepair();", lastWindowClose, StringComparison.Ordinal);
        Assert.True(lastWindowClose >= 0 && show > lastWindowClose, "Startup no longer shows the main window after setting OnLastWindowClose.");
        Assert.True(prompt > show, "The duplicate-installation prompt runs before the main window is showing.");
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
