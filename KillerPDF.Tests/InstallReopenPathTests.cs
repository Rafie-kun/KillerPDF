using System;
using System.IO;
using System.Text.RegularExpressions;
using KillerPDF.Shell;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The footer's Install button replaces this process with the installed copy and hands it the
/// active document to reopen. A browser download has no saved home: its tab's only file is a
/// session temp file that cleanup deletes on exit, before the installed copy can open it. The
/// installed copy therefore gets the user's real file or nothing, never a cleanup-owned working
/// copy (BF-A005).
/// </summary>
public sealed class InstallReopenPathTests
{
    [Fact]
    public void TheRealFileIsHandedOver()
    {
        Assert.Equal(@"C:\Docs\report.pdf", PortableInstallPlan.ReopenPath(@"C:\Docs\report.pdf", @"C:\Temp\work.pdf", _ => true));
        Assert.Equal(@"C:\Docs\report.pdf", PortableInstallPlan.ReopenPath(@"C:\Docs\report.pdf", null, _ => false));
    }

    [Fact]
    public void AWorkingCopyIsHandedOverOnlyWhenCleanupLeavesItAlone()
    {
        Assert.Equal(@"C:\Docs\opened.pdf", PortableInstallPlan.ReopenPath(null, @"C:\Docs\opened.pdf", _ => false));
        Assert.Null(PortableInstallPlan.ReopenPath(null, @"C:\Temp\browser-download.pdf", _ => true));
        Assert.Null(PortableInstallPlan.ReopenPath(null, null, _ => false));
    }

    [Fact]
    public void TheInstallBadgeUsesTheHelper()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "Install.Extras.cs"));
        Assert.DoesNotContain("fileToOpen: _originalFile ?? _currentFile", source);
        Assert.Matches(@"fileToOpen: PortableInstallPlan\.ReopenPath\(_originalFile, _currentFile, App\.IsCleanupOwnedPath\)", source);
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
