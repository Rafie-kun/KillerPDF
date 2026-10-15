using System;
using System.IO;
using KillerPDF.Services;
using Microsoft.Win32;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// #410: the preview handler registration runs against a scratch key under HKCU, the same way
/// as ProtocolRegistrarStaleTests, and never touches the real .pdf preview.
/// </summary>
public sealed class PreviewHandlerRegistrationTests : IDisposable
{
    private const string PreviewPath = @"Software\Classes\.pdf\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}";
    private const string ServerPath = @"Software\Classes\CLSID\" + PreviewHandlerRegistration.ClassId + @"\InprocServer32";

    private readonly string _rootPath = @"Software\KillerPDF.Tests-" + Guid.NewGuid().ToString("N");
    private readonly RegistryKey _root;
    private readonly string _directory;

    public PreviewHandlerRegistrationTests()
    {
        _root = Registry.CurrentUser.CreateSubKey(_rootPath)
            ?? throw new InvalidOperationException("The scratch registry root could not be created.");
        _directory = Path.Combine(Path.GetTempPath(), "kpdf-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, PreviewHandlerRegistration.HostFileName), []);
    }

    public void Dispose()
    {
        _root.Dispose();
        try { Registry.CurrentUser.DeleteSubKeyTree(_rootPath, throwOnMissingSubKey: false); } catch { }
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private string? Read(string path, string name = "")
    {
        using RegistryKey? key = _root.OpenSubKey(path);
        return key?.GetValue(name) as string;
    }

    [Fact]
    public void Register_PointsPdfPreviewsAtTheHandlerInItsOwnSurrogate()
    {
        PreviewHandlerRegistration.Register(_root, _directory);

        Assert.True(PreviewHandlerRegistration.IsRegistered(_root));
        Assert.Equal(Path.Combine(_directory, PreviewHandlerRegistration.HostFileName), Read(ServerPath));
        Assert.Equal("Apartment", Read(ServerPath, "ThreadingModel"));
        Assert.Equal(PreviewHandlerRegistration.ClassId, Read(PreviewPath));
        Assert.Equal(PreviewHandlerRegistration.AppId,
            Read(@"Software\Classes\CLSID\" + PreviewHandlerRegistration.ClassId, "AppID"));
        using RegistryKey? appId = _root.OpenSubKey(@"Software\Classes\AppID\" + PreviewHandlerRegistration.AppId);
        Assert.Equal(@"%SystemRoot%\system32\prevhost.exe",
            appId?.GetValue("DllSurrogate", null, RegistryValueOptions.DoNotExpandEnvironmentNames));
    }

    [Fact]
    public void Unregister_PutsBackThePreviewItReplaced()
    {
        using (RegistryKey key = _root.CreateSubKey(PreviewPath)) key.SetValue("", "{11111111-2222-3333-4444-555555555555}");

        PreviewHandlerRegistration.Register(_root, _directory);
        PreviewHandlerRegistration.Register(_root, _directory);
        PreviewHandlerRegistration.Unregister(_root);

        Assert.False(PreviewHandlerRegistration.IsRegistered(_root));
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", Read(PreviewPath));
        Assert.Null(_root.OpenSubKey(@"Software\Classes\CLSID\" + PreviewHandlerRegistration.ClassId));
    }

    [Fact]
    public void Unregister_RemovesThePreviewWhenNothingWasThereBefore()
    {
        PreviewHandlerRegistration.Register(_root, _directory);
        PreviewHandlerRegistration.Unregister(_root);

        Assert.Null(_root.OpenSubKey(PreviewPath));
        using RegistryKey? handlers = _root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PreviewHandlers");
        Assert.Null(handlers?.GetValue(PreviewHandlerRegistration.ClassId));
    }

    [Fact]
    public void Unregister_LeavesAnotherHandlersPreviewAlone()
    {
        using (RegistryKey key = _root.CreateSubKey(PreviewPath)) key.SetValue("", "{AAAAAAAA-2222-3333-4444-555555555555}");

        PreviewHandlerRegistration.Unregister(_root);

        Assert.Equal("{AAAAAAAA-2222-3333-4444-555555555555}", Read(PreviewPath));
    }

    [Fact]
    public void Register_RefusesAnInstallationWithoutTheHandler()
    {
        File.Delete(Path.Combine(_directory, PreviewHandlerRegistration.HostFileName));
        Assert.Throws<FileNotFoundException>(() => PreviewHandlerRegistration.Register(_root, _directory));
        Assert.False(PreviewHandlerRegistration.IsRegistered(_root));
    }
}
