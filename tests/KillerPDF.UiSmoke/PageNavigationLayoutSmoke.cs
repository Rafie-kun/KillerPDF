using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static DocumentStateSmoke;

internal static class PageNavigationLayoutSmoke
{
    internal static int Run(Window owner, string folder, bool verifyToolbar = false, bool verifyMenus = false)
    {
        owner.WindowState = WindowState.Normal;
        owner.Left = 16; owner.Top = 16; owner.Width = 620; owner.Height = 700;
        owner.Opacity = 1; owner.ShowActivated = true; owner.Activate();
        string path = Path.Combine(folder, new string('W', 100) + ".pdf");
        byte[] original = PageDeletionSmoke.Fixture(); File.WriteAllBytes(path, original);
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [path]);
        var previous = (Button)owner.FindName("PreviousPageBtn");
        var next = (Button)owner.FindName("NextPageBtn");
        Wait(() => ((ListBox)owner.FindName("PageList")).Items.Count == 5, "layout source");
        if (previous.IsVisible || next.IsVisible) throw new InvalidOperationException("Navigation is not off by default.");
        Capture("dark-narrow-off");
        ParagraphEditSmoke.RunSearchAction(owner, "Str_NavigationButtons_Show", "Alt+Shift+N");
        Layout("dark-narrow-on");
        Theme("ThemeLightRadio"); Layout("light-narrow-on");
        Theme("Theme98SERadio"); Layout("classic-narrow-on");
        Theme("ThemeDarkRadio"); owner.Width = 1000;
        for (int index = 0; index < 25; index++) ShortcutHintsSmoke.Key(owner, 0xBB, 0x11, 0x10);
        Wait(() => Scale() >= 1.49, "native app-scale setting reaches1.5");
        Layout("dark-large-app-scale-on");
        owner.Width = 620;
        Layout("dark-narrow-large-app-scale-on");
        var logo = (FrameworkElement)owner.FindName("LogoBar");
        for (int index = 0; index < 50; index++)
            logo.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.MouseWheelEvent });
        Wait(() => Math.Abs(Scale() - 2.5) < .001, "maximum app scale");
        owner.Width = 620;
        Layout("dark-minimum-width-maximum-app-scale-on");
        if (owner.ActualWidth > SystemParameters.WorkArea.Width)
            throw new InvalidOperationException("Footer minimum width exceeds the available screen.");
        ShortcutHintsSmoke.Key(owner, 0x79);
        var paneA = (FrameworkElement)owner.FindName("Viewer");
        var paneB = (FrameworkElement)owner.FindName("ViewerB");
        Wait(() => paneB.IsVisible && paneB.ActualWidth > 100, "footer minimum with split view");
        owner.Width = 620;
        Wait(() => paneA.ActualWidth >= 319.5 && paneB.ActualWidth >= 319.5, "footer minimum preserves both320DIP reading panes");
        Capture("dark-split-maximum-app-scale-on");
        ShortcutHintsSmoke.Key(owner, 0x79);
        Wait(() => !paneB.IsVisible, "footer split cleanup");
        for (int index = 0; index < 80 && Scale() > 1.001; index++)
            logo.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent });
        Wait(() => Math.Abs(Scale() - 1) < .001, "app-scale reset");
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(original) || Dirty(owner))
            throw new InvalidOperationException("Navigation layout changed the source or edit state.");
        Console.WriteLine("PASS navigation layout: default off/on, narrow long filename, Light/98SE/Dark and1.5app scale retain caption and filename space.");
        return 0;

        double Scale()
        {
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "data", "settings.json")));
            return settings.RootElement.TryGetProperty("AppScale", out JsonElement value)
                && double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double scale) ? scale : 1;
        }
        void Theme(string name)
        {
            Click(owner, "ThemeButton");
            var radio = ThemeMenuRadios.Find(owner, name);
            Wait(() => radio.IsVisible && radio.IsEnabled, "actual theme picker");
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(radio).GetPattern(PatternInterface.SelectionItem)!).Select();
            Wait(() => radio.IsChecked == true, "selected theme");
            ((ContextMenu)owner.FindName("ThemeMenu")).IsOpen = false;
        }
        void Layout(string name)
        {
            Wait(() => previous.IsVisible && next.IsVisible, "navigation layout controls visible");
            owner.UpdateLayout();
            Rect logo = Bounds((FrameworkElement)owner.FindName("LogoBar"));
            Rect nav = Bounds((FrameworkElement)owner.FindName("PageNavigationHost"));
            Rect file = Bounds((FrameworkElement)owner.FindName("FileNameLabel"));
            Rect captions = Bounds((FrameworkElement)((Button)owner.FindName("MaximizeBtn")).Parent);
            if (logo.Right > nav.Left + .5 || nav.Right > file.Left + .5 || file.Right > captions.Left + .5
                || file.Width < 24 || nav.Width < 50 || nav.Left < 0 || captions.Right > owner.ActualWidth + .5)
                throw new InvalidOperationException("Navigation overlaps caption content in " + name);
            Console.WriteLine($"CONTROL {name}: logo={logo}, navigation={nav}, filename={file}, captions={captions}.");
            Capture(name);
        }
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor(owner).TransformBounds(new Rect(element.RenderSize));
        void Capture(string name)
        {
            var content = (FrameworkElement)owner.FindName("RootGrid");
            Wait(() => content.IsVisible && content.Opacity > .99, "actual window content reveal before layout capture");
            owner.UpdateLayout();
            double[]? previousGeometry = null;
            int stableFrames = 0;
            Wait(() =>
            {
                owner.UpdateLayout();
                var tools = owner.FindName("FooterToolsScroll") as ScrollViewer;
                double[] geometry = [owner.ActualWidth, owner.MinWidth,
                    ((FrameworkElement)owner.FindName("FooterDocumentControls")).ActualWidth,
                    tools?.ViewportWidth ?? 0, tools?.ExtentWidth ?? 0];
                stableFrames = previousGeometry is not null && geometry.SequenceEqual(previousGeometry) ? stableFrames + 1 : 0;
                previousGeometry = geometry;
                return stableFrames >= 2 && owner.ActualWidth >= owner.MinWidth - .5;
            }, "settled footer dimensions before scroll input in " + name);
            var readingPane = (FrameworkElement)owner.FindName("Viewer");
            Wait(() => Descendants<ScrollViewer>(readingPane).Any(view => view.Name == "PagePreviewPanel"
                    && view.IsVisible && view.Opacity > .99)
                && PageNavigationModesSmoke.ColorPresent(readingPane, 255, 0, 0), "settled page viewport before layout capture in " + name);
            if (verifyToolbar)
            {
                var bar = (FrameworkElement)owner.FindName("ToolbarGrid");
                Rect Left() => Union(((Panel)owner.FindName("LeftBar")).Children.OfType<FrameworkElement>());
                Rect Right() => Union(((Panel)owner.FindName("RightBar")).Children.OfType<FrameworkElement>()
                    .Append((FrameworkElement)owner.FindName("OverflowChevron")));
                Console.WriteLine($"CONTROL toolbar {name}: allowed={Bounds(bar)}, left={Left()}, right={Right()}.");
                Wait(() => Left().Right <= Right().Left + .5 && Left().Left >= Bounds(bar).Left - .5
                    && Right().Right <= Bounds(bar).Right + .5, "settled toolbar tools do not overlap in " + name);
                if (verifyMenus) CheckToolbarMenu(name);
            }
            var version = (FrameworkElement)owner.FindName("FooterVersionCell");
            Rect footerBounds = Bounds((FrameworkElement)owner.FindName("FooterBorder"));
            Rect versionBounds = Bounds(version);
            Console.WriteLine($"CONTROL footer {name}: window={owner.ActualWidth}, footer={footerBounds}, version={versionBounds}, tools={Bounds((FrameworkElement)owner.FindName("FooterDocumentControls"))}.");
            if (versionBounds.Right > footerBounds.Right + .5)
                throw new InvalidOperationException("Footer content extends past the visible footer in " + name);
            if (owner.FindName("FooterToolsScroll") is ScrollViewer toolsScroll)
            {
                var left = (Button)owner.FindName("FooterToolsPrevious");
                var right = (Button)owner.FindName("FooterToolsNext");
                bool overflow = toolsScroll.ExtentWidth > ((FrameworkElement)owner.FindName("FooterToolsViewport")).ActualWidth + .5;
                if (left.IsVisible != overflow || right.IsVisible != overflow)
                    throw new InvalidOperationException("Footer overflow arrows do not match available width in " + name);
                if (overflow)
                {
                    for (int step = 0; right.IsEnabled && step < 20; step++)
                    {
                        double before = toolsScroll.HorizontalOffset;
                        Click(owner, "FooterToolsNext");
                        Wait(() => toolsScroll.HorizontalOffset > before, "footer overflow right arrow advances");
                    }
                    if (right.IsEnabled || !left.IsEnabled) throw new InvalidOperationException("Footer overflow cannot reach the right boundary.");
                    for (int step = 0; left.IsEnabled && step < 20; step++)
                    {
                        double before = toolsScroll.HorizontalOffset;
                        Click(owner, "FooterToolsPrevious");
                        Wait(() => toolsScroll.HorizontalOffset < before, "footer overflow left arrow advances");
                    }
                    if (left.IsEnabled || !right.IsEnabled) throw new InvalidOperationException("Footer overflow cannot return to the left boundary.");
                    Console.WriteLine("CONTROL footer arrows reach both boundaries in " + name);
                }
            }
            foreach (string tool in new[] { "PageSizeLabel", "ViewContinuousRadio", "ViewSingleRadio", "ViewTwoPageRadio", "ViewGridRadio", "FullScreenRailBtn", "DocInvertBtn", "ZoomOutBtn", "ZoomBox", "ZoomInBtn" })
            {
                var control = (FrameworkElement)owner.FindName(tool);
                control.BringIntoView();
                owner.UpdateLayout();
                Wait(() =>
                {
                    Rect bounds = Bounds(control);
                    var viewport = owner.FindName("FooterToolsScroll") as FrameworkElement;
                    Rect allowed = viewport is null ? Bounds((FrameworkElement)owner.FindName("FooterDocumentControls")) : Bounds(viewport);
                    return bounds.Left >= Math.Max(0, allowed.Left) - .5
                        && bounds.Right <= Math.Min(owner.ActualWidth, Math.Min(Bounds(version).Left, allowed.Right)) + .5;
                }, "footer tool remains reachable: " + tool + " in " + name);
            }
            Console.WriteLine("CONTROL footer tools reachable in " + name);
            var image = new RenderTargetBitmap((int)Math.Ceiling(owner.ActualWidth), (int)Math.Ceiling(owner.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(owner); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var output = File.Create(Path.Combine(folder, name + ".png")); png.Save(output);
        }
        Rect Union(IEnumerable<FrameworkElement> elements)
        {
            Rect result = Rect.Empty;
            foreach (FrameworkElement element in elements.Where(element => element.IsVisible && element.ActualWidth > 0))
                result.Union(Bounds(element));
            return result;
        }
        void CheckToolbarMenu(string name)
        {
            foreach (string kept in new[] { "GrpOpen", "GrpSave", "GrpOcr", "ToolSelectBtn" })
                if (!((FrameworkElement)owner.FindName(kept)).IsVisible)
                    throw new InvalidOperationException("A split file control or Select disappeared in " + name);
            var commands = new[] { ("FlattenBtn", "Str_Lbl_Flatten"), ("PrintBtn", "Str_Lbl_Print"),
                ("CloseFileBtn", "Str_Lbl_Close"), ("NewBlankBtn", "Str_Lbl_New"), ("ToolRotateBtn", "Str_Lbl_Rotate") };
            var hidden = commands.Where(command => !((Button)owner.FindName(command.Item1)).IsVisible).ToArray();
            if (hidden.Length == 0) return;
            var toggle = (ToggleButton)owner.FindName("OverflowChevron");
            var popup = (Popup)owner.FindName("OverflowPopup");
            var provider = (IToggleProvider)new ToggleButtonAutomationPeer(toggle).GetPattern(PatternInterface.Toggle)!;
            provider.Toggle();
            try
            {
                Wait(() => popup.IsOpen && popup.Child is FrameworkElement { IsVisible: true }, "actual More menu");
                var root = (FrameworkElement)popup.Child;
                Wait(() => PresentationSource.FromVisual(root) is not null && root.ActualWidth > 0 && root.ActualHeight > 0,
                    "More menu is connected and measured");
                foreach (var command in hidden)
                {
                    string caption = (string)owner.FindResource(command.Item2);
                    Button row = Descendants<Button>(root).Single(button => button.IsVisible
                        && Descendants<TextBlock>(button).Any(label => label.Text == caption));
                    row.BringIntoView(); root.UpdateLayout();
                    var scroll = Descendants<ScrollViewer>(root).SingleOrDefault();
                    Console.WriteLine($"CONTROL More command {command.Item1}: root={root.RenderSize}, row={row.RenderSize}, scroll={scroll is not null}.");
                    Wait(() =>
                    {
                        Rect bounds = row.TransformToAncestor(root).TransformBounds(new Rect(row.RenderSize));
                        Rect visible = scroll is null ? new Rect(root.RenderSize)
                            : scroll.TransformToAncestor(root).TransformBounds(new Rect(scroll.RenderSize));
                        Point top = row.PointToScreen(new Point(0, 0));
                        Point bottom = row.PointToScreen(new Point(0, row.ActualHeight));
                        DpiScale dpi = VisualTreeHelper.GetDpi(root);
                        return bounds.Top >= visible.Top - .5 && bounds.Bottom <= visible.Bottom + .5
                            && top.Y / dpi.DpiScaleY >= SystemParameters.WorkArea.Top - .5
                            && bottom.Y / dpi.DpiScaleY <= SystemParameters.WorkArea.Bottom + .5;
                    }, "overflow command is scroll reachable: " + command.Item1);
                    if (row.IsEnabled != ((Button)owner.FindName(command.Item1)).IsEnabled
                        || new ButtonAutomationPeer(row).GetPattern(PatternInterface.Invoke) is not IInvokeProvider)
                        throw new InvalidOperationException("Overflow command availability changed: " + command.Item1);
                }
                Console.WriteLine($"CONTROL More menu preserves {hidden.Length} fallback commands and all split file controls in {name}.");
            }
            finally
            {
                if (toggle.IsChecked == true) provider.Toggle();
            }
            Wait(() => !popup.IsOpen, "More menu closes without invoking a command");
        }
    }
}
