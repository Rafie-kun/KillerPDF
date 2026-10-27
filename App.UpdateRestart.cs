using KillerPDF.Shell;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using KillerPDF.Features;
using KillerPDF.Services;

namespace KillerPDF;

public partial class App
{
    private static UpdateRestartSnapshot? _updateRestart;
    private static bool _updateRestartReadFailed;
    private static UpdateRestartHandoff? _updateHandoff;
    internal static bool UpdateRestartPrepared => _updateHandoff is not null;

    private static UpdateRestartStore CreateUpdateRestartStore()
    {
        string identity = GetPortableLauncherPath()
            ?? Path.Combine(Path.GetDirectoryName(typeof(App).Assembly.Location)!, ExeName);
        using var account = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        return new UpdateRestartStore(Path.Combine(AppDataPaths.LocalRoot, "UpdateRestart"),
            Path.GetFullPath(identity).ToUpperInvariant(),
            account.User?.Value ?? throw new InvalidOperationException("The current account is unavailable."),
            process.SessionId);
    }

    internal static void LoadPendingUpdateRestart()
    {
        try { _updateRestart = CreateUpdateRestartStore().TryClaim(); }
        catch { _updateRestartReadFailed = true; }
    }

    internal static UpdateRestartSnapshot? TakeUpdateRestart() => Interlocked.Exchange(ref _updateRestart, null);
    internal static bool TakeUpdateRestartReadFailure()
    {
        bool failed = _updateRestartReadFailed;
        _updateRestartReadFailed = false;
        return failed;
    }

    internal static IUpdateRestartHandoff? PrepareUpdateRestart(MainWindow owner)
    {
        if (_updateHandoff is not null) return null;
        MainWindow[] windows = Current.Windows.OfType<MainWindow>().ToArray();
        var captures = new List<(MainWindow Window, long Revision, string[] Files, bool Enabled)>();
        foreach (MainWindow window in windows)
        {
            if (!window.TryCaptureUpdateRestart(out long revision, out string[] files)) return null;
            captures.Add((window, revision, files, window.UpdateRestartEditingEnabled));
        }
        UpdateRestartStore store = CreateUpdateRestartStore();
        Guid token = store.Stage(captures.SelectMany(capture => capture.Files), owner.UpdateRestartActiveFile);
        var handoff = new UpdateRestartHandoff(store, token, captures);
        _updateHandoff = handoff;
        try
        {
            foreach (var capture in captures)
            {
                capture.Window.UpdateRestartEditingEnabled = false;
                capture.Window.SaveSessionEndingSettings();
            }
            handoff.Validate();
            return handoff;
        }
        catch { handoff.Dispose(); throw; }
    }

    internal static bool TryCloseForUpdate(MainWindow window, System.ComponentModel.CancelEventArgs args)
    {
        if (_updateHandoff is not { } handoff) return false;
        try
        {
            handoff.Validate();
            if (!args.Cancel) handoff.ApproveClosing(window);
        }
        catch { args.Cancel = true; }
        return true;
    }

    // An incoming file invalidates a captured session before it can change any tabs.
    internal static void InvalidateUpdateRestart() => _updateHandoff?.Invalidate();
    internal static void RetainUpdateRestartForExit() => _updateHandoff?.RetainForExit();

    private sealed class UpdateRestartHandoff(UpdateRestartStore store, Guid token,
        List<(MainWindow Window, long Revision, string[] Files, bool Enabled)> captures) : IUpdateRestartHandoff
    {
        private readonly HashSet<MainWindow> _closing = [];
        private bool _started;
        private bool _committed;
        private bool _invalid;
        private bool _disposed;

        internal void Validate()
        {
            if (_disposed || _invalid) throw new InvalidOperationException("The update session changed.");
            MainWindow[] live = Current.Windows.OfType<MainWindow>().ToArray();
            if (live.Any(window => !captures.Any(capture => ReferenceEquals(capture.Window, window)))
                || captures.Any(capture => !live.Contains(capture.Window) && !_closing.Contains(capture.Window)))
                throw new InvalidOperationException("The open windows changed during update preparation.");
            foreach (var capture in captures.Where(capture => live.Contains(capture.Window)))
                if (!capture.Window.TryCaptureUpdateRestart(out long revision, out string[] files)
                    || revision != capture.Revision
                    || !files.SequenceEqual(capture.Files, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException("An open document changed during update preparation.");
        }

        internal void ApproveClosing(MainWindow window)
        {
            if (!captures.Any(capture => ReferenceEquals(capture.Window, window)))
                throw new InvalidOperationException("The window is not part of the update session.");
            _closing.Add(window);
        }

        public void Started() { Validate(); _started = true; }
        public void Commit() { Validate(); _committed = true; }
        internal void RetainForExit() { _started = true; _committed = true; }
        internal void Invalidate() { _invalid = true; _committed = false; Dispose(); }

        public void Dispose()
        {
            // Shutdown is dispatched by WPF. The application owns a committed handoff until exit.
            if (_disposed || _committed) return;
            _disposed = true;
            if (ReferenceEquals(_updateHandoff, this)) _updateHandoff = null;
            foreach (var capture in captures)
                if (Current.Windows.OfType<MainWindow>().Contains(capture.Window))
                    capture.Window.UpdateRestartEditingEnabled = capture.Enabled;
            if (!_started || _invalid) store.Cancel(token);
        }
    }
}
