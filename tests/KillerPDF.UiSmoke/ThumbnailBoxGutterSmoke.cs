using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class ThumbnailBoxGutterSmoke
{
    internal static int Run(Window owner, string folder, bool scroll = false, bool strip = false, bool maximumScale = false, bool actions = false, bool switchDocument = false)
    {
        Rect work = SystemParameters.WorkArea;
        owner.WindowState = WindowState.Normal;
        owner.Left = work.Left + 8; owner.Top = work.Top + 8;
        owner.Width = Math.Min(950, work.Width - 16); owner.Height = Math.Min(700, work.Height - 16);
        owner.Opacity = 1; owner.Activate();
        string path = Path.Combine(folder, "gutter-eighty-pages.pdf");
        byte[] original = LongFixture();
        File.WriteAllBytes(path, original);
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [path]);
        var pages = (ListBox)owner.FindName("PageList");
        Wait(() => pages.Items.Count == 80 && pages.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem { ActualHeight: > 50 },
            "virtualized long-document first thumbnail");
        if (strip)
        {
            ShortcutHintsSmoke.Key(owner, 0x78, 0x10);
            Wait(() => Setting("SidebarSide") == "Right", "native sidebar switch to right");
            ShortcutHintsSmoke.Key(owner, 0x78, 0x10);
            Wait(() => Setting("SidebarSide") == "Top", "native sidebar switch to top strip");
        }
        if (strip || maximumScale)
        {
            var logo = (FrameworkElement)owner.FindName("LogoBar");
            for (int step = 0; step < 80 && Setting("AppScale") != "2.5"; step++)
                logo.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.MouseWheelEvent });
            Wait(() => Setting("AppScale") == "2.5"
                && Descendants<VirtualizingStackPanel>(pages).Any(panel => panel.Orientation == (strip ? Orientation.Horizontal : Orientation.Vertical)),
                "maximum app scale and expected virtualized orientation");
        }
        Wait(() => pages.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem { ActualHeight: > 50 },
            "realized first thumbnail after layout changes");
        var first = (ListBoxItem)pages.ItemContainerGenerator.ContainerFromIndex(0);
        Size previous = new(); long stable = Environment.TickCount64;
        Wait(() =>
        {
            if (first.RenderSize != previous) { previous = first.RenderSize; stable = Environment.TickCount64; }
            return work.Contains(new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight))
                && Environment.TickCount64 - stable >= 150;
        }, "stable long-document bounds");
        int realized = Descendants<ListBoxItem>(pages).Count();
        if (realized >= 40) throw new InvalidOperationException("The long-document fixture did not establish thumbnail virtualization.");
        Point start = first.TranslatePoint(strip ? new Point(first.ActualWidth * .75, 2) : new Point(2, first.ActualHeight * .75), owner);
        Point end = first.TranslatePoint(new Point(first.ActualWidth - 20, 12), owner);
        var viewport = Descendants<ScrollContentPresenter>(pages).First(element => element.IsVisible && element.ActualHeight > 20);
        var scroller = Descendants<ScrollViewer>(pages).First();
        if (scroll)
        {
            Point bottom = viewport.TranslatePoint(strip ? new Point(viewport.ActualWidth - 4, viewport.ActualHeight - 20)
                : new Point(viewport.ActualWidth - 20, viewport.ActualHeight - 4), owner);
            end = bottom;
        }
        Point screen = owner.PointToScreen(start);
        Point rounded = owner.PointFromScreen(new Point(Math.Round(screen.X), Math.Round(screen.Y)));
        if (owner.InputHitTest(rounded) is not Border { Name: "Bd" } gutter
            || !ReferenceEquals(gutter.TemplatedParent, first))
            throw new InvalidOperationException("The gutter origin is not the verified empty ListBoxItem padding border.");
        pages.SelectedItems.Clear(); pages.SelectedItems.Add(pages.Items[0]);
        string switchedPath = Path.Combine(folder, "box-switched-source.pdf");
        byte[] switchedBytes = PageDeletionSmoke.Fixture(1);
        if (switchDocument) { File.WriteAllBytes(switchedPath, switchedBytes); Capture("box-switch-before.png"); }
        int switches = 0;
        Exception? switchFailure = null;
        bool box = false, reorder = false;
        double maximumOffset = 0, lastHeldOffset = 0;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Input, (_, _) =>
        {
            maximumOffset = Math.Max(maximumOffset, Offset());
            if (Mouse.LeftButton == MouseButtonState.Pressed) lastHeldOffset = Offset();
            if (owner.FindName("PageSelectionBox") is FrameworkElement { IsVisible: true, ActualWidth: > 2, ActualHeight: > 2 }) box = true;
            if (owner.FindName("PageDropIndicator") is FrameworkElement { IsVisible: true }) reorder = true;
            if (switchDocument && switches == 0 && Mouse.Captured == pages && Mouse.LeftButton == MouseButtonState.Pressed
                && owner.FindName("PageSelectionBox") is FrameworkElement { IsVisible: true, ActualWidth: > 2, ActualHeight: > 2 }
                && Offset() > (strip ? scroller.ViewportWidth : scroller.ViewportHeight))
            {
                switches++;
                try
                {
                    Capture("box-switch-during.png");
                    owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [switchedPath]);
                }
                catch (Exception error) { switchFailure = error; }
            }
        }, owner.Dispatcher);
        try
        {
            timer.Start();
            // The old hit test may enter OLE page dragging. The target is before page zero,
            // so this route is a no-op; native release, capture and source checks still apply.
            ThumbnailBoxSelectionSmoke.Drag(owner, start, end, allowNativeDropRelease: !scroll,
                holdMilliseconds: scroll ? 1800 : 150);
        }
        finally { timer.Stop(); }
        int[] selected = pages.SelectedItems.Cast<object>().Select(pages.Items.IndexOf).Order().ToArray();
        Console.WriteLine($"CONTROL gutter: realized={realized}/80, box={box}, reorderMarker={reorder}, selected={string.Join(',', selected)}, dirty={Dirty(owner)}.");
        if (Dirty(owner) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(original))
            throw new InvalidOperationException("The no-op gutter probe changed document state or source bytes.");
        if (switchDocument)
        {
            if (switchFailure is not null) throw new InvalidOperationException("Public document switch failed during the active box gesture.", switchFailure);
            Wait(() => pages.Items.Count == 1 && ((TextBlock)owner.FindName("FileNameLabel")).Text == Path.GetFileName(switchedPath),
                "replacement document after active box gesture");
            selected = pages.SelectedItems.Cast<object>().Select(pages.Items.IndexOf).Order().ToArray();
            if (switches != 1 || !box || reorder || !selected.SequenceEqual(new[] { 0 }) || Mouse.Captured is not null
                || owner.FindName("PageSelectionBox") is FrameworkElement { IsVisible: true }
                || Dirty(owner) || ((Button)owner.FindName("UndoBtn")).IsEnabled
                || !File.ReadAllBytes(switchedPath).AsSpan().SequenceEqual(switchedBytes))
                throw new InvalidOperationException("Document replacement retained a stale box, selection, capture, or edit history.");
            Capture("box-switch-finished.png");
            Console.WriteLine("PASS thumbnail box document switch: replacing the document during a scrolled native gesture releases capture and preserves both sources with clean replacement history.");
            return 0;
        }
        if (scroll)
        {
            double length = strip ? scroller.ViewportWidth : scroller.ViewportHeight;
            Console.WriteLine($"CONTROL box scroll: strip={strip}, scale={Setting("AppScale")}, offset={Offset()}, maximumOffset={maximumOffset}, lastHeldOffset={lastHeldOffset}, viewport={length}, selected={string.Join(',', selected)}.");
            if (!box || reorder || Offset() <= length || selected.Length < 5
                || !selected.SequenceEqual(Enumerable.Range(0, selected.Length)) || Mouse.Captured is not null
                || owner.FindName("PageSelectionBox") is FrameworkElement { IsVisible: true })
                throw new InvalidOperationException("Holding a thumbnail selection at the viewport edge did not scroll and retain a contiguous selection beyond the first viewport.");
            Console.WriteLine("PASS thumbnail box scrolling: native edge hold selects a contiguous range beyond the first viewport without editing the source.");
            if (actions) ThumbnailBoxActionsSmoke.Run(owner, folder, path, original, selected);
            return 0;
        }
        if (!box || reorder || !selected.SequenceEqual(new[] { 0 }))
            throw new InvalidOperationException("Empty thumbnail-row padding arms reordering instead of box selection in a virtualized document.");
        Console.WriteLine("PASS thumbnail box gutter: empty row padding starts selection without arming reorder in an eighty-page virtualized document.");
        return 0;

        double Offset() => strip ? scroller.HorizontalOffset : scroller.VerticalOffset;
        void Capture(string name)
        {
            owner.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(owner.ActualWidth), (int)Math.Ceiling(owner.ActualHeight),
                96, 96, PixelFormats.Pbgra32);
            bitmap.Render(owner);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, name));
            encoder.Save(output);
        }
        string? Setting(string key)
        {
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "data", "settings.json")));
            return settings.RootElement.TryGetProperty(key, out JsonElement value) ? value.GetString() : null;
        }
    }

    internal static byte[] LongFixture()
    {
        const int count = 80;
        List<string> objects = ["<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, count).Select(index => $"{4 + index * 2} 0 R")) + "] /Count 80 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"];
        string[] colors = ["1 0 0", "0 1 0", "0 0 1"];
        for (int index = 0; index < count; index++)
        {
            string content = $"{colors[index % colors.Length]} rg 0 0 200 200 re f 0 g BT /F1 20 Tf 20 100 Td (PAGE_{index + 1}) Tj ET\n";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + index * 2} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}endstream");
        }
        var pdf = new StringBuilder("%PDF-1.7\n");
        List<int> offsets = [0];
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
