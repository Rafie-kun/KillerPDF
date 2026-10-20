#nullable enable
using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace KillerFamily.Shell
{
    /// <summary>The family tray icon, present only while the main window is hidden.</summary>
    internal sealed class CloseToTray : IDisposable
    {
        internal const string SettingName = "CloseToTray";

        private const int CallbackMessage = 0x8000 + 0x51;
        private const uint NIM_ADD = 0, NIM_DELETE = 2;
        private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
        private const int WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B,
            NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;

        private readonly Window _window;
        private readonly string _appName;
        private readonly Func<string, string> _loc;
        private readonly Func<bool> _isEnabled;
        private readonly Func<uint, bool>? _notifyIcon;
        private readonly HwndSource _source;
        private readonly uint _taskbarCreated;
        private readonly IntPtr _icon;
        private bool _iconShown, _exiting, _disposed;

        // Existing application shutdown paths must still run normal close handling.
        private static readonly PropertyInfo? s_isShuttingDown =
            typeof(Application).GetProperty("IsShuttingDown", BindingFlags.NonPublic | BindingFlags.Static);

        internal static bool CanSeeShutdown => s_isShuttingDown?.PropertyType == typeof(bool);

        private static bool AppShuttingDown()
        {
            try { return s_isShuttingDown?.GetValue(null) is true; }
            catch { return false; }
        }

        internal static CloseToTray Attach(Window window, string appName, Func<string, string> loc, Func<bool> isEnabled,
            Func<uint, bool>? notifyIcon = null)
            => new(window, appName, loc, isEnabled, notifyIcon);

        private CloseToTray(Window window, string appName, Func<string, string> loc, Func<bool> isEnabled, Func<uint, bool>? notifyIcon)
        {
            _window = window;
            _appName = appName;
            _loc = loc;
            _isEnabled = isEnabled;
            _notifyIcon = notifyIcon;
            // An unseen popup receives TaskbarCreated after Explorer restarts.
            _source = new HwndSource(new HwndSourceParameters(appName + "TrayIcon")
                { WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0 });
            _source.AddHook(WndProc);
            _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            int size = GetSystemMetrics(49);
            _icon = LoadImage(GetModuleHandle(null), new IntPtr(32512), 1, size, size, 0);
            window.Closed += WindowClosed;
            Application.Current.Exit += ApplicationExit;
        }

        /// <summary>Runs the window's normal close and save prompts, including cancellation.</summary>
        internal void Exit()
        {
            if (_disposed || _exiting) return;
            ShowWindow();
            _exiting = true;
            try { _window.Close(); }
            finally { _exiting = false; }
            // A canceled close leaves the restored window visible. A confirmed close completes
            // through the window's existing fade, whose second pass bypasses tray hiding.
        }

        internal void ShowWindow()
        {
            if (_disposed) return;
            if (!_window.IsVisible) _window.Show();
            if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
            _window.Activate();
            _window.Topmost = true;
            _window.Topmost = false;
            HideIcon();
        }

        internal bool HideInstead(CancelEventArgs e)
        {
            if (_exiting || _disposed || e.Cancel || AppShuttingDown() || !_isEnabled()) return false;
            e.Cancel = true;
            ShowIcon();
            if (_iconShown) _window.Hide();
            else ShowWindow();
            return true;
        }

        private void ShowIcon()
        {
            if (_iconShown) return;
            var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            _iconShown = _notifyIcon?.Invoke(NIM_ADD) ?? Shell_NotifyIcon(NIM_ADD, ref data);
        }

        private void HideIcon()
        {
            if (!_iconShown) return;
            var data = Data(0);
            _ = _notifyIcon?.Invoke(NIM_DELETE) ?? Shell_NotifyIcon(NIM_DELETE, ref data);
            _iconShown = false;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == CallbackMessage)
            {
                int notification = (int)(lParam.ToInt64() & 0xFFFF);
                if (notification is WM_LBUTTONUP or NIN_SELECT or NIN_KEYSELECT) ShowWindow();
                else if (notification is WM_RBUTTONUP or WM_CONTEXTMENU) ShowMenu();
                handled = true;
            }
            else if (msg == (int)_taskbarCreated && _iconShown)
            {
                _iconShown = false;
                ShowIcon();
                if (!_iconShown) ShowWindow();
            }
            return IntPtr.Zero;
        }

        private void ShowMenu()
        {
            var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
            var open = new MenuItem { Header = string.Format(_loc("Str_Tray_Open"), _appName), FontWeight = FontWeights.SemiBold };
            open.Click += (_, _) => ShowWindow();
            var exit = new MenuItem { Header = _loc("Str_Tray_Exit") };
            exit.Click += (_, _) => Exit();
            menu.Items.Add(open);
            menu.Items.Add(new Separator());
            menu.Items.Add(exit);
            SetForegroundWindow(_source.Handle);
            menu.IsOpen = true;
        }

        private NOTIFYICONDATA Data(uint flags) => new()
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _source.Handle,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
            szTip = _appName.Length > 127 ? _appName[..127] : _appName,
            szInfo = "",
            szInfoTitle = "",
        };

        private void WindowClosed(object? sender, EventArgs e) => Dispose();
        private void ApplicationExit(object sender, ExitEventArgs e) => Dispose();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            HideIcon();
            _window.Closed -= WindowClosed;
            Application.Current.Exit -= ApplicationExit;
            if (_icon != IntPtr.Zero) DestroyIcon(_icon);
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public int uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr instance, IntPtr name, uint type, int cx, int cy, uint load);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    }
}
