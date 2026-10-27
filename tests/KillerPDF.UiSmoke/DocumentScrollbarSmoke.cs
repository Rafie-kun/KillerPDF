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

internal static class DocumentScrollbarSmoke
{
    internal static int RunVariants(Window owner, string folder)
    {
        Run(owner, folder, "ThemeLightRadio");
        Run(owner, folder, "Theme98SERadio");
        Run(owner, folder, "ThemeDarkRadio", maximumScale: true);
        Console.WriteLine("PASS document scrollbar variants: Light and classic controls plus maximum-scale Dark preserve theme-specific hover behavior, distant dragging, and direct page entry without edits.");
        return 0;
    }

    internal static int Run(Window owner, string folder, string? themeRadio = null, bool maximumScale = false)
    {
        Rect work = SystemParameters.WorkArea;
        owner.WindowState = WindowState.Normal;
        owner.Left = work.Left + 8; owner.Top = work.Top + 8;
        owner.Width = Math.Min(950, work.Width - 16); owner.Height = Math.Min(700, work.Height - 16);
        owner.Opacity = 1; owner.Activate();
        if (themeRadio is not null)
        {
            Click(owner, "ThemeButton");
            var radio = ThemeMenuRadios.Find(owner, themeRadio);
            Wait(() => radio.IsVisible && radio.IsEnabled, "actual theme picker for document scrollbar");
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(radio).GetPattern(PatternInterface.SelectionItem)!).Select();
            Wait(() => radio.IsChecked == true, "selected document scrollbar theme");
            ((ContextMenu)owner.FindName("ThemeMenu")).IsOpen = false;
        }
        if (maximumScale)
        {
            var logo = (FrameworkElement)owner.FindName("LogoBar");
            for (int step = 0; step < 80 && Setting("AppScale") != "2.5"; step++)
                logo.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.MouseWheelEvent });
            Wait(() => Setting("AppScale") == "2.5", "maximum isolated app scale for document scrollbar");
        }
        string source = Path.Combine(folder, "document-navigation-eighty-pages.pdf");
        byte[] original = ThumbnailBoxGutterSmoke.LongFixture();
        if (!File.Exists(source)) File.WriteAllBytes(source, original);
        else if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original)) throw new InvalidOperationException("An earlier scrollbar variant changed the source.");
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [source]);
        var pages = (ListBox)owner.FindName("PageList");
        var pane = (FrameworkElement)owner.FindName("Viewer");
        var jump = (TextBox)owner.FindName("PageJumpBox");
        Wait(() => pages.Items.Count == 80 && Descendants<ScrollViewer>(pane).Any(view => view.Name == "PagePreviewPanel"
            && view.IsVisible && view.ScrollableHeight > view.ViewportHeight), "long-document reading viewport");
        var viewport = Descendants<ScrollViewer>(pane).Single(view => view.Name == "PagePreviewPanel" && view.IsVisible);
        jump.Focus(); jump.SelectAll(); jump.Text = "1";
        ShortcutHintsSmoke.Key(owner, 0x0D);
        Wait(() => pages.SelectedIndex == 0 && jump.Text == "1" && viewport.VerticalOffset < 1, "first page before themed scrollbar control");
        var bar = Descendants<ScrollBar>(viewport).Single(element => element.IsVisible && element.Orientation == Orientation.Vertical);
        var thumb = Descendants<Thumb>(bar).Single(element => element.IsVisible);
        double[]? previousGeometry = null;
        long stableSince = Environment.TickCount64;
        Wait(() =>
        {
            owner.UpdateLayout();
            double[] geometry = [owner.ActualWidth, owner.ActualHeight, thumb.ActualWidth, thumb.ActualHeight,
                viewport.ViewportHeight, viewport.ExtentHeight];
            if (previousGeometry is null || !geometry.SequenceEqual(previousGeometry))
            { previousGeometry = geometry; stableSince = Environment.TickCount64; }
            return work.Contains(new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight))
                && thumb.ActualWidth > 5 && thumb.ActualHeight > 20 && Environment.TickCount64 - stableSince >= 150;
        }, "stable document scrollbar and owned window");
        ShortcutHintsSmoke.MovePointer(owner, new Point(8, 8));
        Wait(() => !thumb.IsMouseOver, "resting document scrollbar");
        Rect bounds = thumb.TransformToAncestor(owner).TransformBounds(new Rect(thumb.RenderSize));
        Point center = new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        if (!HitsThumb(center) || HitsThumb(new Point(bounds.Left - 20, center.Y)))
            throw new InvalidOperationException("The document-thumb hit detector failed its center or outside control.");
        Point left = owner.PointToScreen(bounds.TopLeft), right = owner.PointToScreen(bounds.TopRight);
        double y = Math.Round(owner.PointToScreen(center).Y);
        int firstPixel = (int)Math.Ceiling(left.X) + 1, lastPixel = (int)Math.Floor(right.X) - 1;
        var missed = new List<int>();
        for (int x = firstPixel; x <= lastPixel; x++)
            if (!HitsThumb(owner.PointFromScreen(new Point(x, y)))) missed.Add(x - (int)Math.Floor(left.X));
        var fill = (FrameworkElement)thumb.Template.FindName("thumbFill", thumb);
        double restingWidth = fill.ActualWidth;
        Capture("document-scrollbar-rest.png");
        Console.WriteLine($"CONTROL document scrollbar: barWidth={bar.ActualWidth}, thumbBounds={bounds}, paintedWidth={fill.ActualWidth}, interiorPixels={lastPixel - firstPixel + 1}, missedThumbOffsets={string.Join(',', missed)}; center/outside controls passed.");
        ShortcutHintsSmoke.MovePointer(owner, center);
        bool classic = themeRadio == "Theme98SERadio";
        Wait(() => thumb.IsMouseOver && (classic ? fill.ActualWidth >= 12 : fill.ActualWidth > restingWidth),
            "document thumb retains its theme-specific hover affordance");
        Rect hoverBounds = fill.TransformToAncestor(owner).TransformBounds(new Rect(fill.RenderSize));
        Point hoverLeft = owner.PointToScreen(hoverBounds.TopLeft), hoverRight = owner.PointToScreen(hoverBounds.TopRight);
        var hoverMisses = new List<int>();
        for (int x = (int)Math.Ceiling(hoverLeft.X) + 1; x <= (int)Math.Floor(hoverRight.X) - 1; x++)
            if (!HitsThumb(owner.PointFromScreen(new Point(x, y)))) hoverMisses.Add(x);
        Capture("document-scrollbar-hover.png");
        Console.WriteLine($"CONTROL document scrollbar hover: theme={themeRadio ?? "default"}, scale={Setting("AppScale")}, restingWidth={restingWidth}, hoverWidth={fill.ActualWidth}, paintedBounds={hoverBounds}, missedPaintedPixels={string.Join(',', hoverMisses)}.");
        if (hoverMisses.Count != 0) throw new InvalidOperationException("The widened painted thumb contains inactive grab positions.");
        Point start = new(hoverBounds.Left + 1, center.Y);
        double distance = Math.Min(100, viewport.ActualHeight - thumb.ActualHeight - 8);
        if (distance < 40 || !HitsThumb(start)) throw new InvalidOperationException("The off-center native drag has no verified owned thumb target or travel.");
        double before = viewport.VerticalOffset;
        ThumbnailBoxSelectionSmoke.Drag(owner, start, start + new Vector(0, distance));
        Wait(() => viewport.VerticalOffset > before && int.TryParse(jump.Text, out int page) && page >= 10,
            "off-center document-thumb drag reaches a distant page");
        jump.Focus(); jump.SelectAll(); jump.Text = "77";
        ShortcutHintsSmoke.Key(owner, 0x0D);
        Wait(() => pages.SelectedIndex == 76 && jump.Text == "77", "direct page entry reaches page77");
        if (Dirty(owner) || Mouse.Captured is not null || !File.ReadAllBytes(source).AsSpan().SequenceEqual(original)
            || owner.FindName("PageSelectionBox") is FrameworkElement { IsVisible: true }
            || owner.FindName("PageDropIndicator") is FrameworkElement { IsVisible: true })
            throw new InvalidOperationException("Document navigation changed source, edit state, or sidebar gesture state.");
        Capture("document-scrollbar-after.png");
        Console.WriteLine("PASS document scrollbar: the theme-specific hovered thumb accepts off-center native dragging to a distant page, and direct entry reaches page77 without edits.");
        return 0;

        bool HitsThumb(Point point)
        {
            for (DependencyObject? hit = owner.InputHitTest(point) as DependencyObject; hit is not null; hit = VisualTreeHelper.GetParent(hit))
                if (ReferenceEquals(hit, thumb)) return true;
            return false;
        }
        void Capture(string name)
        {
            owner.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(owner.ActualWidth), (int)Math.Ceiling(owner.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(owner);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, themeRadio is null ? name : themeRadio + "-" + name)); encoder.Save(output);
        }
        string? Setting(string key)
        {
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "data", "settings.json")));
            return settings.RootElement.TryGetProperty(key, out JsonElement value) ? value.GetString() : null;
        }
    }
}
