using System;
using System.IO;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public class InstalledShortcutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesktopShortcutUsesTheInstalledScope(bool machine)
    {
        string expected = Path.Combine(Environment.GetFolderPath(machine
            ? Environment.SpecialFolder.CommonDesktopDirectory
            : Environment.SpecialFolder.DesktopDirectory), "KillerPDF.lnk");
        Assert.Equal(expected, InstalledShortcuts.DesktopPath(machine));
    }

    [Fact]
    public void ShortcutWriteFailureIsReported()
    {
        string folder = Path.Combine(Path.GetTempPath(), "kp-shortcut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string target = Path.Combine(folder, "KillerPDF.App.exe");
            File.WriteAllText(target, "test target");
            string link = Path.Combine(folder, "KillerPDF.lnk");
            Directory.CreateDirectory(link);
            Assert.ThrowsAny<Exception>(() => InstalledShortcuts.Create(link, target));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void NativeShortcutHasTargetWorkingDirectoryAndIconAndCanBeUpdated()
    {
        string folder = Path.Combine(Path.GetTempPath(), "kp-shortcut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string link = Path.Combine(folder, "redirected desktop", "KillerPDF.lnk");
            foreach (string name in new[] { "KillerPDF old.exe", "KillerPDF é App.exe" })
            {
                string target = Path.Combine(folder, name);
                File.WriteAllText(target, "test target");
                InstalledShortcuts.Create(link, target);
                var saved = InstalledShortcuts.Read(link);
                Assert.Equal(target, saved.Target, ignoreCase: true);
                Assert.Equal(folder, saved.WorkingDirectory, ignoreCase: true);
                Assert.Equal(target, saved.Icon, ignoreCase: true);
                Assert.Equal(0, saved.IconIndex);
                byte[] bytes = File.ReadAllBytes(link);
                Assert.Equal(76, BitConverter.ToInt32(bytes, 0));
                Assert.Equal(new Guid("00021401-0000-0000-C000-000000000046"), new Guid(bytes.AsSpan(4, 16)));
                Assert.Single(Directory.GetFiles(Path.GetDirectoryName(link)!));
            }
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void FailedReplacementPreservesTheExistingShortcut()
    {
        string folder = Path.Combine(Path.GetTempPath(), "kp-shortcut-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string target = Path.Combine(folder, "KillerPDF.App.exe");
            File.WriteAllText(target, "test target");
            string link = Path.Combine(folder, "KillerPDF.lnk");
            InstalledShortcuts.Create(link, target);
            byte[] original = File.ReadAllBytes(link);
            using (File.Open(link, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Exception? failure = Record.Exception(() => InstalledShortcuts.Create(link, target));
                Assert.True(failure is IOException or UnauthorizedAccessException, failure?.ToString());
            }
            Assert.Equal(original, File.ReadAllBytes(link));
            Assert.Equal(2, Directory.GetFiles(folder).Length);
            Assert.Throws<FileNotFoundException>(() => InstalledShortcuts.Create(link, Path.Combine(folder, "missing.exe")));
            Assert.Equal(original, File.ReadAllBytes(link));
        }
        finally { Directory.Delete(folder, true); }
    }
}
