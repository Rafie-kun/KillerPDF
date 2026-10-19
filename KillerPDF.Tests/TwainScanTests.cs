using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using KillerPDF.Services;
using NTwain;
using NTwain.Data;
using Xunit;

namespace KillerPDF.Tests;

public sealed class TwainScanTests
{
    [Fact]
    public async Task LaterTransferFailureMustNotBecomeOrdinarySuccess()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scan-test-" + Guid.NewGuid().ToString("N"));
        ITwainSession session = DispatchProxy.Create<ITwainSession, ScannerProxy>();
        IDataSource source = DispatchProxy.Create<IDataSource, ScannerProxy>();
        var events = (ScannerProxy)session;
        using var scan = new TwainScan(session, IntPtr.Zero);
        try
        {
            var task = scan.ScanAsync(source, false, folder);
            Transfer(events);
            events.Raise("TransferError", new TransferErrorEventArgs(new IOException("Feeder failed")));
            Transfer(events);
            events.Raise("SourceDisabled", EventArgs.Empty);
            Assert.Equal(2, Directory.GetFiles(folder).Length);
            TwainScanResult result = await task;
            Assert.False(result.Completed);
            Assert.False(result.Canceled);
            Assert.Equal("Feeder failed", Assert.Single(result.Errors).Message);
            Assert.Equal(2, result.Pages.Count);
            Assert.All(result.Pages, path => Assert.True(File.Exists(path)));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("success", 2, 0, false)]
    [InlineData("stop", 1, 0, true)]
    [InlineData("decode", 2, 1, false)]
    [InlineData("write", 2, 1, false)]
    [InlineData("empty", 0, 0, false)]
    [InlineData("failed", 0, 1, false)]
    public async Task ScanOutcomePreservesPagesAndDistinguishesFailuresAndStops(
        string scenario, int pageCount, int errorCount, bool canceled)
    {
        string folder = Path.Combine(Path.GetTempPath(), "scan-test-" + Guid.NewGuid().ToString("N"));
        ITwainSession session = DispatchProxy.Create<ITwainSession, ScannerProxy>();
        IDataSource source = DispatchProxy.Create<IDataSource, ScannerProxy>();
        var events = (ScannerProxy)session;
        using var scan = new TwainScan(session, IntPtr.Zero);
        try
        {
            var task = scan.ScanAsync(source, false, folder);
            if (scenario is not ("empty" or "failed")) Transfer(events);
            if (scenario == "failed") events.Raise("TransferError", new TransferErrorEventArgs(new IOException("No transfer")));
            if (scenario == "stop") events.DisableReason = Message.CloseDSReq;
            if (scenario == "decode") Transfer(events, invalid: true);
            if (scenario == "write")
            {
                using var locked = new FileStream(Path.Combine(folder, "scan-002.jpg"), FileMode.CreateNew,
                    FileAccess.ReadWrite, FileShare.None);
                Transfer(events);
            }
            if (scenario is "success" or "decode" or "write") Transfer(events);
            events.Raise("SourceDisabled", EventArgs.Empty);
            TwainScanResult result = await task;
            Assert.Equal(pageCount, result.Pages.Count);
            Assert.Equal(errorCount, result.Errors.Count);
            Assert.Equal(canceled, result.Canceled);
            Assert.Equal(errorCount == 0 && !canceled, result.Completed);
            Assert.All(result.Pages, path => Assert.True(new FileInfo(path).Length > 0));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    private static void Transfer(ScannerProxy events, bool invalid = false)
    {
        // One 24-bit BGR pixel with a four-byte aligned scanline.
        byte[] dib = new byte[44];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(1).CopyTo(dib, 4);
        BitConverter.GetBytes(1).CopyTo(dib, 8);
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)24).CopyTo(dib, 14);
        BitConverter.GetBytes(4).CopyTo(dib, 20);
        if (invalid) BitConverter.GetBytes(99).CopyTo(dib, 16);
        dib[42] = 255;
        IntPtr data = Marshal.AllocHGlobal(dib.Length);
        try
        {
            Marshal.Copy(dib, 0, data, dib.Length);
            events.Raise("DataTransferred", new DataTransferredEventArgs(null!, data));
        }
        finally { Marshal.FreeHGlobal(data); }
    }

    public class ScannerProxy : DispatchProxy
    {
        private readonly Dictionary<string, Delegate?> _events = [];
        public Message DisableReason { get; set; }
        public void Raise(string name, EventArgs args) => _events[name]?.DynamicInvoke(this, args);

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
        {
            string name = method!.Name;
            if (name.StartsWith("add_", StringComparison.Ordinal))
            {
                string key = name[4..];
                _events.TryGetValue(key, out Delegate? existing);
                _events[key] = Delegate.Combine(existing, (Delegate)arguments![0]!);
                return null;
            }
            if (name.StartsWith("remove_", StringComparison.Ordinal))
            {
                string key = name[7..];
                _events[key] = Delegate.Remove(_events[key], (Delegate)arguments![0]!);
                return null;
            }
            if (name == "get_DisableReason") return DisableReason;
            if (name is "Open" or "Close" or "Enable") return ReturnCode.Success;
            throw new NotSupportedException(name);
        }
    }
}
