using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static DocumentStateSmoke;

internal static class PageNavigationModesSmoke
{
    internal static int Run(Window owner, string folder, string? focusArm = null)
    {
        owner.WindowState = WindowState.Normal;
        owner.Left = 16; owner.Top = 16; owner.Width = 950; owner.Height = 700;
        owner.Opacity = 1; owner.ShowActivated = true; owner.Activate();
        var pages = (ListBox)owner.FindName("PageList");
        var jump = (TextBox)owner.FindName("PageJumpBox");
        var previous = (Button)owner.FindName("PreviousPageBtn");
        var next = (Button)owner.FindName("NextPageBtn");
        var focusedPane = (FrameworkElement)owner.FindName("Viewer");
        var sources = new Dictionary<string, byte[]>();
        string two = Source("two-pages.pdf", 2), five = Source(new string('W', 100) + ".pdf", 5);
        ParagraphEditSmoke.RunSearchAction(owner, "Str_NavigationButtons_Show", "Alt+Shift+N");
        Open(two, 2);
        Wait(() => previous.IsVisible && next.IsVisible, "two-page navigation visibility");
        foreach ((byte key, string radio) in new[] { ((byte)0x75, "ViewSingleRadio"), ((byte)0x74, "ViewContinuousRadio"), ((byte)0x77, "ViewGridRadio") })
        {
            Mode(key, radio); SelectPage(0);
            Step("NextPageBtn", 1); Boundary(previous: true, next: false);
            Step("PreviousPageBtn", 0); Boundary(previous: false, next: true);
            Console.WriteLine("CONTROL two-page document navigation: " + radio);
        }
        Mode(0x76, "ViewTwoPageRadio"); SelectPage(0);
        Boundary(previous: false, next: false);
        Book(true);
        Wait(() => next.IsEnabled, "book cover can advance to its next spread");
        Step("NextPageBtn", 1); Boundary(previous: true, next: false);
        Step("PreviousPageBtn", 0);
        Book(false);
        Wait(() => !next.IsEnabled, "ordinary two-page spread boundary restored");

        Open(five, 5);
        foreach ((byte key, string radio) in new[] { ((byte)0x75, "ViewSingleRadio"), ((byte)0x74, "ViewContinuousRadio"), ((byte)0x77, "ViewGridRadio") })
        {
            Mode(key, radio); SelectPage(0);
            for (int index = 1; index < 5; index++) Step("NextPageBtn", index);
            Boundary(previous: true, next: false);
            for (int index = 3; index >= 0; index--) Step("PreviousPageBtn", index);
            Boundary(previous: false, next: true);
            Console.WriteLine("CONTROL five-page document navigation: " + radio);
        }
        Mode(0x76, "ViewTwoPageRadio"); SelectPage(0);
        Step("NextPageBtn", 2); Step("NextPageBtn", 4); Boundary(previous: true, next: false);
        Step("PreviousPageBtn", 2); Step("PreviousPageBtn", 0);
        Book(true);
        Step("NextPageBtn", 1); Step("NextPageBtn", 3); Boundary(previous: true, next: false);
        Step("PreviousPageBtn", 1); Step("PreviousPageBtn", 0);
        Book(false);
        Mode(0x75, "ViewSingleRadio"); SelectPage(0);
        Wait(() => ColorPresent((FrameworkElement)owner.FindName("Viewer"), 255, 0, 0)
            && !ColorPresent((FrameworkElement)owner.FindName("Viewer"), 255, 0, 255), "first-page viewport color presence and absence controls");
        Step("NextPageBtn", 1);
        Wait(() => ColorPresent((FrameworkElement)owner.FindName("Viewer"), 0, 255, 0)
            && !ColorPresent((FrameworkElement)owner.FindName("Viewer"), 255, 0, 0), "Next renders the new page rather than only changing sidebar selection");
        SelectPage(0);

        ShortcutHintsSmoke.Key(owner, 0x79);
        var paneA = (FrameworkElement)owner.FindName("Viewer");
        var paneB = (FrameworkElement)owner.FindName("ViewerB");
        Wait(() => paneB.IsVisible && paneB.ActualWidth > 100, "second document pane");
        if (focusArm is not null)
            return PageNavigationFocusProbe.Run(owner, paneA, paneB, five, Source("focus-single.pdf", 1), sources, focusArm);
        Pane(paneB); Open(Source("pane-b-single.pdf", 1), 1);
        Wait(() => !previous.IsVisible && !next.IsVisible, "one-page focused pane hides arrows");
        Pane(paneA); Wait(() => pages.Items.Count == 5 && jump.Text == "1" && next.IsVisible, "pane A restores its navigation state");
        Pane(paneB); Open(Source("pane-b-five.pdf", 5), 5); Mode(0x75, "ViewSingleRadio");
        Step("NextPageBtn", 1); Step("NextPageBtn", 2);
        Wait(() => ColorPresent(paneB, 0, 0, 255) && !ColorPresent(paneB, 255, 0, 0), "pane B actually renders its third blue page");
        Pane(paneA); Wait(() => jump.Text == "1", "pane B navigation did not move pane A");
        Wait(() => ColorPresent(paneA, 255, 0, 0) && !ColorPresent(paneA, 0, 0, 255), "pane A still renders its first red page");
        Step("NextPageBtn", 1);
        Wait(() => ColorPresent(paneA, 0, 255, 0) && !ColorPresent(paneA, 255, 0, 0), "pane A actually renders its second green page");
        Pane(paneB); Wait(() => jump.Text == "3", "pane A navigation did not move pane B");
        Wait(() => ColorPresent(paneB, 0, 0, 255) && !ColorPresent(paneB, 0, 255, 0), "pane B retains its independently rendered blue page");
        Pane(paneA); ShortcutHintsSmoke.Key(owner, 0x79);
        Wait(() => !paneB.IsVisible, "split pane cleanup");
        Open(five, 5); SelectPage(0);
        owner.Width = 620;
        Layout("dark-narrow-on");
        ParagraphEditSmoke.RunSearchAction(owner, "Str_NavigationButtons_Show", "Alt+Shift+N");
        Wait(() => !previous.IsVisible && !next.IsVisible, "narrow title bar option off");
        Capture("dark-narrow-off");
        ParagraphEditSmoke.RunSearchAction(owner, "Str_NavigationButtons_Show", "Alt+Shift+N");
        Theme("ThemeLightRadio"); Layout("light-narrow-on");
        Theme("Theme98SERadio"); Layout("classic-narrow-on");
        Theme("ThemeDarkRadio");
        owner.Width = 1000;
        for (int index = 0; index < 25; index++) ShortcutHintsSmoke.Key(owner, 0xBB, 0x11, 0x10);
        Wait(() => double.TryParse(Setting("AppScale"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double scale)
            && scale >= 1.49, "native app-scale input applied");
        Layout("dark-large-app-scale-on");
        var logo = (FrameworkElement)owner.FindName("LogoBar");
        for (int index = 0; index < 25; index++)
            logo.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent });
        Wait(() => Setting("AppScale") == "1", "native app-scale reset applied");
        foreach (var source in sources)
            if (!File.ReadAllBytes(source.Key).AsSpan().SequenceEqual(source.Value)) throw new InvalidOperationException("Navigation changed synthetic source bytes.");
        if (Dirty(owner)) throw new InvalidOperationException("Navigation changed document edit state.");
        Console.WriteLine("PASS navigation modes: two/five pages, all view modes, book spreads, both focused panes, narrow/classic/light title bars and large app scale preserve source ownership.");
        return 0;

        string Source(string name, int count)
        {
            string path = Path.Combine(folder, name); byte[] data = PageDeletionSmoke.Fixture(count);
            File.WriteAllBytes(path, data); sources.Add(path, data); return path;
        }
        void Open(string path, int count)
        {
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [path]);
            Wait(() => pages.Items.Count == count && ((RadioButton)owner.FindName("ViewSingleRadio")).IsEnabled
                && ((TextBlock)owner.FindName("FileNameLabel")).Text == Path.GetFileName(path), "installed navigation mode source");
        }
        void Mode(byte key, string name)
        {
            Wait(() => ((RadioButton)owner.FindName(name)).IsVisible && ((RadioButton)owner.FindName(name)).IsEnabled,
                "view mode is available before native input");
            ShortcutHintsSmoke.Key(owner, key);
            string expected = name[4..^5];
            bool Applied()
            {
                string path = Path.Combine(folder, "data", "settings.json");
                if (!File.Exists(path)) return false;
                using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(path));
                return settings.RootElement.TryGetProperty("ViewMode", out JsonElement mode) && mode.GetString() == expected;
            }
            Wait(() => ((RadioButton)owner.FindName(name)).IsChecked == true && Applied()
                && Descendants<ScrollViewer>(focusedPane).Any(view => view.Name == "PagePreviewPanel" && view.IsVisible && view.Opacity > .99),
                "applied view mode and completed viewport fade " + expected);
            Console.WriteLine("CONTROL applied view mode with settled viewport: " + expected);
        }
        string? Setting(string key)
        {
            string path = Path.Combine(folder, "data", "settings.json");
            if (!File.Exists(path)) return null;
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(path));
            return settings.RootElement.TryGetProperty(key, out JsonElement value) ? value.GetString() : null;
        }
        void Book(bool enabled)
        {
            ShortcutHintsSmoke.Key(owner, 0x42);
            Wait(() => (Setting("TwoPageBook") == "1") == enabled, "native book-layout toggle applied");
            Console.WriteLine("CONTROL native book-layout setting applied: " + enabled);
        }
        void SelectPage(int index)
        {
            pages.SelectedIndex = index;
            Wait(() => jump.Text == (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "selected navigation page");
        }
        void Step(string name, int expected)
        {
            Click(owner, name);
            Wait(() => pages.SelectedIndex == expected && jump.Text == (expected + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "navigation step " + expected);
        }
        void Boundary(bool previous, bool next)
        {
            Wait(() => ((Button)owner.FindName("PreviousPageBtn")).IsEnabled == previous
                && ((Button)owner.FindName("NextPageBtn")).IsEnabled == next, "navigation boundary availability");
        }
        void Pane(FrameworkElement pane)
        {
            Console.WriteLine("BEGIN owned pane focus: " + pane.Name);
            Point point = PageNavigationFocusProbe.NeutralPaneFocusPoint(owner, pane);
            ShortcutHintsSmoke.ClickAt(owner, point, expectedPane: pane);
            focusedPane = pane;
            Console.WriteLine("CONTROL pane click completed: " + pane.Name + ", pages=" + pages.Items.Count + ", page=" + jump.Text);
        }
        void Theme(string name)
        {
            Click(owner, "RailThemeBtn");
            var radio = (RadioButton)owner.FindName(name);
            Wait(() => radio.IsVisible && radio.IsEnabled, "actual theme picker");
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(radio).GetPattern(PatternInterface.SelectionItem)!).Select();
            Wait(() => radio.IsChecked == true, "selected theme");
            ((ContextMenu)owner.FindName("ThemeFlyout")).IsOpen = false;
        }
        void Layout(string name)
        {
            Wait(() => previous.IsVisible && next.IsVisible, "navigation layout visible");
            owner.UpdateLayout();
            Rect logo = Bounds((FrameworkElement)owner.FindName("LogoBar"));
            Rect nav = Bounds((FrameworkElement)owner.FindName("PageNavigationHost"));
            Rect file = Bounds((FrameworkElement)owner.FindName("FileNameLabel"));
            Rect captions = Bounds((FrameworkElement)((Button)owner.FindName("MaximizeBtn")).Parent);
            if (logo.Right > nav.Left + .5 || nav.Right > file.Left + .5 || file.Right > captions.Left + .5
                || file.Width < 24 || nav.Width < 50 || nav.Left < 0 || captions.Right > owner.ActualWidth + .5)
                throw new InvalidOperationException("Navigation title-bar overlap or unavailable filename space in " + name);
            Console.WriteLine($"CONTROL {name}: logo={logo}, navigation={nav}, filename={file}, captions={captions}.");
            Capture(name);
        }
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(owner).TransformBounds(new Rect(element.RenderSize));
        void Capture(string name)
        {
            var root = (FrameworkElement)owner.FindName("RootGrid");
            Wait(() => root.IsVisible && root.Opacity > .99 && owner.ActualWidth >= owner.MinWidth - .5
                && ColorPresent(paneA, 255, 0, 0), "visible first page before navigation layout capture " + name);
            owner.UpdateLayout();
            var image = new RenderTargetBitmap((int)Math.Ceiling(owner.ActualWidth), (int)Math.Ceiling(owner.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(owner); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(Path.Combine(folder, name + ".png")); encoder.Save(output);
        }
    }

    internal static bool ColorPresent(FrameworkElement pane, byte red, byte green, byte blue)
    {
        ScrollViewer? viewport = Descendants<ScrollViewer>(pane).SingleOrDefault(view => view.Name == "PagePreviewPanel" && view.IsVisible);
        if (viewport is null) return false;
        Window? owner = Window.GetWindow(pane);
        if (owner is null || owner.ActualWidth < 1 || owner.ActualHeight < 1) return false;
        owner.UpdateLayout();
        var rendered = new RenderTargetBitmap((int)Math.Ceiling(owner.ActualWidth), (int)Math.Ceiling(owner.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        rendered.Render(owner);
        Rect bounds = viewport.TransformToAncestor(owner).TransformBounds(new Rect(viewport.RenderSize));
        bounds.Intersect(new Rect(0, 0, rendered.PixelWidth, rendered.PixelHeight));
        if (bounds.IsEmpty || bounds.Width < 20 || bounds.Height < 20) return false;
        int x = (int)Math.Ceiling(bounds.Left), y = (int)Math.Ceiling(bounds.Top);
        int width = (int)Math.Floor(bounds.Right) - x, height = (int)Math.Floor(bounds.Bottom) - y;
        byte[] pixels = new byte[width * height * 4];
        rendered.CopyPixels(new Int32Rect(x, y, width, height), pixels, width * 4, 0);
        int matches = 0;
        for (int row = 0; row < height; row += 8)
            for (int column = 0; column < width; column += 8)
            {
                int offset = (row * width + column) * 4;
                if (Math.Abs(pixels[offset + 2] - red) < 8 && Math.Abs(pixels[offset + 1] - green) < 8
                    && Math.Abs(pixels[offset] - blue) < 8 && ++matches >= 16) return true;
            }
        return false;
    }
}
