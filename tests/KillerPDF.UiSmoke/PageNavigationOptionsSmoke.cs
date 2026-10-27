using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using static DocumentStateSmoke;

internal static class PageNavigationOptionsSmoke
{
    internal static int Run(Window owner, string folder, string dataRoot, bool restored)
    {
        Show(owner);
        var previous = (Button)owner.FindName("PreviousPageBtn");
        var next = (Button)owner.FindName("NextPageBtn");
        string single = Path.Combine(folder, "option-single.pdf"), multiple = Path.Combine(folder, "option-multiple.pdf");
        byte[] one = TableExportSmoke.Fixture(), five = PageDeletionSmoke.Fixture();
        File.WriteAllBytes(single, one); File.WriteAllBytes(multiple, five);
        Stored(restored);
        if (restored)
        {
            var restoredPages = (ListBox)owner.FindName("PageList");
            Wait(() => restoredPages.Items.Count > 0, "remembered synthetic document restored in the new process");
            Wait(() => previous.IsVisible == (restoredPages.Items.Count > 1) && next.IsVisible == (restoredPages.Items.Count > 1),
                "saved navigation preference agrees with the restored document");
            Console.WriteLine("CONTROL new process restored " + restoredPages.Items.Count + " pages with the enabled navigation preference.");
            Open(owner, multiple, 5);
            Wait(() => previous.IsVisible && next.IsVisible, "saved navigation preference in a new process");
            Console.WriteLine("PASS navigation restored: new process reads the enabled saved preference for remembered and newly opened synthetic documents.");
            return 0;
        }

        Hidden(owner);
        Menu(owner, expected: false, invoke: true); Stored(true); Hidden(owner);
        Toggle(owner); Stored(false); Hidden(owner);
        Open(owner, multiple, 5); Toggle(owner); Stored(true);
        Wait(() => previous.IsVisible && next.IsVisible, "native shortcut enables arrows");
        Hints(true); Hover(true);
        Hints(false); Hover(false);
        Hints(true); Hover(true);
        Open(owner, single, 1); Hidden(owner);
        Toggle(owner); Stored(false); Hidden(owner);
        Toggle(owner); Stored(true); Hidden(owner);
        Open(owner, multiple, 5);
        Wait(() => previous.IsVisible && next.IsVisible, "navigation follows the selected multi-page document");

        Window other = (Window)Activator.CreateInstance(owner.GetType())!;
        try
        {
            Show(other);
            Hidden(other);
            string second = Path.Combine(folder, "other-window.pdf");
            File.WriteAllBytes(second, five);
            Open(other, second, 5);
            Wait(() => ((Button)other.FindName("NextPageBtn")).IsVisible, "new window honors saved preference");
            Toggle(other); Stored(false);
            Hidden(other); Hidden(owner);
            Toggle(owner); Stored(true);
            Wait(() => next.IsVisible && ((Button)other.FindName("NextPageBtn")).IsVisible, "toggle updates both windows");
            if (!File.ReadAllBytes(second).AsSpan().SequenceEqual(five)) throw new InvalidOperationException("Navigation changed the other window's source.");
        }
        finally { other.Close(); }
        Menu(owner, expected: true, invoke: false);
        if (!File.ReadAllBytes(single).AsSpan().SequenceEqual(one) || !File.ReadAllBytes(multiple).AsSpan().SequenceEqual(five))
            throw new InvalidOperationException("Navigation options changed source bytes.");
        Console.WriteLine("PASS navigation options: native shortcut, system-menu toggle, saved off/on, one-page hiding, cross-window synchronization and rendered shortcut hints.");
        return 0;

        void Stored(bool enabled)
        {
            string settings = Path.Combine(dataRoot, "settings.json");
            bool actual = false;
            if (File.Exists(settings))
            {
                using JsonDocument json = JsonDocument.Parse(File.ReadAllText(settings));
                actual = json.RootElement.TryGetProperty("ShowPageNavigationButtons", out JsonElement value) && value.GetString() == "1";
            }
            if (actual != enabled) throw new InvalidOperationException("The saved navigation preference differs from its expected value.");
            Console.WriteLine("CONTROL saved navigation preference: " + enabled);
        }

        void Hints(bool enabled)
        {
            ShortcutHintsSmoke.Key(owner, 0x7B);
            var about = (FrameworkElement)owner.FindName("AboutOverlay");
            Wait(() => about.IsVisible, "actual About preference");
            string label = (string)owner.FindResource("Str_About_ShowShortcutsInMenus");
            CheckBox checkbox = Descendants<CheckBox>(about).Single(box => box.Content is TextBlock text && text.Text == label);
            if (checkbox.IsChecked != enabled)
            {
                var toggle = (IToggleProvider)new ToggleButtonAutomationPeer(checkbox).GetPattern(PatternInterface.Toggle)!;
                toggle.Toggle();
            }
            Wait(() => checkbox.IsChecked == enabled, "shortcut hint preference");
            ShortcutHintsSmoke.Key(owner, 0x7B);
            Wait(() => !about.IsVisible, "About closes after preference change");
        }

        void Hover(bool hints)
        {
            Point point = next.TransformToAncestor(owner).Transform(new Point(next.ActualWidth / 2, next.ActualHeight / 2));
            ShortcutHintsSmoke.MovePointer(owner, point);
            Wait(() => Popups<ToolTip>().Any(tip => tip.IsOpen && tip.IsVisible && ReferenceEquals(tip.PlacementTarget, next)), "actual Next page hover");
            ToolTip tip = Popups<ToolTip>().Single(tip => tip.IsOpen && tip.IsVisible && ReferenceEquals(tip.PlacementTarget, next));
            string visible = string.Join(" ", Descendants<TextBlock>(tip).Where(text => text.IsVisible).Select(text => text.Text));
            string label = (string)owner.FindResource("Str_Kb_NextPage"), key = (string)owner.FindResource("Str_Key_PgDn");
            if (!visible.Contains(label, StringComparison.Ordinal) || visible.Contains(key, StringComparison.Ordinal) != hints
                || new ButtonAutomationPeer(next).GetName() != label)
                throw new InvalidOperationException("Navigation hover or accessible name does not follow the shortcut-hint preference: " + visible);
            Menu(owner, expected: true, invoke: false, hints: hints);
            var pane = (FrameworkElement)owner.FindName("Viewer");
            Point away = pane.TransformToAncestor(owner).Transform(new Point(pane.ActualWidth / 2, pane.ActualHeight / 2));
            ShortcutHintsSmoke.MovePointer(owner, away);
            Wait(() => !tip.IsOpen, "navigation hover cleanup");
            Console.WriteLine("CONTROL rendered navigation hint " + hints + ": " + visible);
        }
    }

    private static void Toggle(Window owner)
    {
        ShortcutHintsSmoke.Key(owner, 0x4E, 0x12, 0x10);
        Pump();
    }

    private static void Menu(Window owner, bool expected, bool invoke, bool hints = true)
    {
        ShortcutHintsSmoke.Key(owner, 0x20, 0x12);
        Wait(() => Popups<ContextMenu>().Any(menu => menu.IsOpen), "native Alt+Space menu");
        ContextMenu menu = Popups<ContextMenu>().Single(menu => menu.IsOpen);
        try
        {
            string label = (string)owner.FindResource("Str_NavigationButtons_Show");
            MenuItem option = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, label));
            if (!option.IsEnabled || !option.IsCheckable || option.IsChecked != expected || option.InputGestureText != "Alt+Shift+N")
                throw new InvalidOperationException("The system-menu navigation option does not match its saved state or shortcut.");
            option.ApplyTemplate();
            var gesture = (TextBlock)option.Template.FindName("Gesture", option);
            Wait(() => gesture.Visibility == (hints ? Visibility.Visible : Visibility.Collapsed), "navigation menu hint preference");
            if (!new MenuItemAutomationPeer(option).GetName().Contains(label, StringComparison.Ordinal))
                throw new InvalidOperationException("The navigation menu lost its accessible name.");
            if (invoke)
            {
                var provider = (IInvokeProvider)new MenuItemAutomationPeer(option).GetPattern(PatternInterface.Invoke)!;
                provider.Invoke(); Pump();
                Wait(() => option.IsChecked != expected, "actual system-menu navigation toggle");
            }
        }
        finally { menu.IsOpen = false; }
    }

    private static void Show(Window owner)
    {
        owner.WindowState = WindowState.Normal;
        owner.Left = 32; owner.Top = 32; owner.Width = 950; owner.Height = 700;
        owner.Opacity = 1; owner.ShowActivated = true; owner.Show(); owner.Activate();
        Pump();
    }

    private static void Open(Window owner, string path, int count)
    {
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [path]);
        Wait(() => ((ListBox)owner.FindName("PageList")).Items.Count == count, "navigation option synthetic document");
    }

    private static void Hidden(Window owner)
    {
        Pump();
        if (((Button)owner.FindName("PreviousPageBtn")).Visibility != Visibility.Collapsed
            || ((Button)owner.FindName("NextPageBtn")).Visibility != Visibility.Collapsed)
            throw new InvalidOperationException("Navigation arrows appeared when their document or preference requires hiding them.");
    }

    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);

    private static IEnumerable<T> Popups<T>() where T : DependencyObject => PresentationSource.CurrentSources.Cast<PresentationSource>()
        .Select(source => source.RootVisual).OfType<DependencyObject>()
        .SelectMany(root => (root is T value ? new[] { value } : Array.Empty<T>()).Concat(Descendants<T>(root))).Distinct();
}
