using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using static DocumentStateSmoke;

internal static class ShortcutHintsSmoke
{
    internal static int Run(Window owner, string folder)
    {
        var about = (FrameworkElement)owner.FindName("AboutOverlay");
        var open = (Button)owner.FindName("OpenFileBtn");
        if (((ListBox)owner.FindName("PageList")).Items.Count != 0)
            throw new InvalidOperationException("Shortcut hint acceptance requires an empty window.");

        IEnumerable<DependencyObject> Roots() => PresentationSource.CurrentSources.Cast<PresentationSource>()
            .Select(source => source.RootVisual).OfType<DependencyObject>().ToArray();
        IEnumerable<T> Popups<T>() where T : DependencyObject => Roots()
            .SelectMany(root => (root is T value ? new[] { value } : System.Array.Empty<T>()).Concat(Descendants<T>(root))).Distinct();

        void Preference(bool enabled)
        {
            Key(owner, 0x7B);
            Wait(() => about.IsVisible, "About opened by F12");
            string label = (string)owner.FindResource("Str_About_ShowShortcutsInMenus");
            CheckBox checkbox = Descendants<CheckBox>(about).Single(box =>
                box.Content is TextBlock text && text.Text == label);
            if (checkbox.IsChecked != enabled)
            {
                var peer = new ToggleButtonAutomationPeer(checkbox);
                var toggle = peer.GetPattern(PatternInterface.Toggle) as IToggleProvider
                    ?? throw new InvalidOperationException("The About preference has no public toggle provider.");
                toggle.Toggle();
            }
            Wait(() => checkbox.IsChecked == enabled, "actual About shortcut hint toggle");
            using (JsonDocument settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "data", "settings.json"))))
            {
                bool stored = !settings.RootElement.TryGetProperty("ShowShortcutsInMenus", out JsonElement saved)
                    || saved.GetString() != "0";
                if (stored != enabled) throw new InvalidOperationException("The About preference did not persist its displayed value.");
            }
            Key(Window.GetWindow(about) ?? owner, 0x7B);
            Wait(() => !about.IsVisible, "F12 stays active after changing shortcut hints");
        }

        void Menu(bool enabled)
        {
            Key(owner, 0x20, 0x12);
            Wait(() => Popups<ContextMenu>().Any(menu => menu.IsOpen), "Alt+Space title-bar menu");
            ContextMenu menu = Popups<ContextMenu>().Single(candidate => candidate.IsOpen);
            try
            {
                MenuItem close = menu.Items.OfType<MenuItem>().Single(item =>
                    Equals(item.Header, owner.FindResource("Str_Sys_Close")));
                close.ApplyTemplate();
                var gesture = (TextBlock)close.Template.FindName("Gesture", close);
                var grid = (Grid)gesture.Parent;
                Wait(() => enabled
                    ? gesture.Visibility == Visibility.Visible && grid.ColumnDefinitions[Grid.GetColumn(gesture)].ActualWidth > 1
                    : gesture.Visibility == Visibility.Collapsed && grid.ColumnDefinitions[Grid.GetColumn(gesture)].ActualWidth < 0.5,
                    "menu gesture visibility and released column space");
                if (enabled && gesture.Text != "Alt+F4")
                    throw new InvalidOperationException("Title-bar Close hint is missing its actual key.");
                string accessible = new MenuItemAutomationPeer(close).GetName();
                if (!accessible.Contains((string)owner.FindResource("Str_Sys_Close"), StringComparison.Ordinal))
                    throw new InvalidOperationException("Hiding menu gestures removed the meaningful accessible name.");
                Console.WriteLine("CONTROL shortcut hints " + enabled + ": menu gesture " + gesture.Visibility
                    + ", column width " + grid.ColumnDefinitions[Grid.GetColumn(gesture)].ActualWidth.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + ", accessible name " + accessible);
            }
            finally { menu.IsOpen = false; }
        }

        void Hover(bool enabled)
        {
            Focus(owner);
            Point position = open.PointToScreen(new Point(open.ActualWidth / 2, open.ActualHeight / 2));
            if (!SetCursorPos((int)position.X, (int)position.Y))
                throw new InvalidOperationException("Could not position the pointer over the owned Open button.");
            Wait(() => Popups<ToolTip>().Any(tip => tip.IsOpen && tip.IsVisible), "actual Open hover tooltip");
            ToolTip tip = Popups<ToolTip>().Single(candidate => candidate.IsOpen && candidate.IsVisible);
            string text = string.Join(" ", Descendants<TextBlock>(tip).Select(block => block.Text));
            if (!text.Contains((string)owner.FindResource("Str_KS_Open"), StringComparison.Ordinal)
                || text.Contains("Ctrl+O", StringComparison.Ordinal) != enabled)
                throw new InvalidOperationException("Open hover help did not preserve its description and follow the shortcut hint preference: " + text);
            Console.WriteLine("CONTROL shortcut hints " + enabled + ": rendered Open tooltip " + text.Replace('\n', '|'));
            Point away = owner.PointToScreen(new Point(8, Math.Max(8, owner.ActualHeight - 8)));
            if (!SetCursorPos((int)away.X, (int)away.Y)) throw new InvalidOperationException("Could not clear the owned hover target.");
            Wait(() => !tip.IsOpen, "hover tooltip cleanup");
        }

        Preference(true);
        Menu(true);
        Hover(true);
        Preference(false);
        Menu(false);
        Hover(false);
        Preference(true);
        Menu(true);
        Hover(true);
        string result = "PASS verify-shortcut-hints: actual About off/on updates rendered menu hints and column space, preserves hover descriptions and accessible names, and leaves F12 active.";
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        Console.WriteLine(result);
        return 0;
    }

    private static void Focus(Window window)
    {
        window.Activate();
        window.Focus();
        IntPtr handle = new WindowInteropHelper(window).Handle;
        Console.WriteLine("READINESS owned window " + window.GetType().Name + ": active=" + window.IsActive
            + ", visible=" + window.IsVisible + ", opacity=" + window.Opacity
            + ", target=" + handle.ToInt64() + ", foreground=" + GetForegroundWindow().ToInt64());
        if (GetForegroundWindow() != handle && window.GetType().Name == "MainWindow")
        {
            Point caption = window.PointToScreen(new Point(Math.Min(220, window.ActualWidth / 2), 12));
            var position = new NativePoint { X = (int)caption.X, Y = (int)caption.Y };
            if (!window.IsVisible || window.Opacity < 0.99 || WindowFromPoint(position) != handle)
                throw new InvalidOperationException("The owned visible caption is not available for safe activation; no pointer input was sent.");
            if (!SetCursorPos(position.X, position.Y)) throw new InvalidOperationException("Could not position the pointer on the owned caption.");
            if (WindowFromPoint(position) != handle) throw new InvalidOperationException("The owned caption activation target changed.");
            try { MouseEvent(0x0002, 0, 0, 0, UIntPtr.Zero); }
            finally { MouseEvent(0x0004, 0, 0, 0, UIntPtr.Zero); }
        }
        Wait(() => GetForegroundWindow() == handle, "owned native keyboard target");
    }

    private static void Key(Window window, byte key, byte modifier = 0)
    {
        Focus(window);
        try
        {
            if (modifier != 0) KeyEvent(modifier, 0, 0, UIntPtr.Zero);
            KeyEvent(key, 0, 0, UIntPtr.Zero);
        }
        finally
        {
            KeyEvent(key, 0, 2, UIntPtr.Zero);
            if (modifier != 0) KeyEvent(modifier, 0, 2, UIntPtr.Zero);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeyEvent(byte key, byte scan, uint flags, UIntPtr extra);
}
