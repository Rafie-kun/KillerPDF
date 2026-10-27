using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class ThumbnailDragControlsSmoke
{
    internal static int Run(Window owner, string folder)
    {
        Rect work = SystemParameters.WorkArea;
        owner.WindowState = WindowState.Normal;
        owner.Left = work.Left + 8; owner.Top = work.Top + 8;
        owner.Width = Math.Min(950, work.Width - 16); owner.Height = Math.Min(700, work.Height - 16);
        owner.Opacity = 1; owner.Activate();
        string source = Path.Combine(folder, "thumbnail-drag-controls.pdf");
        byte[] original = ThumbnailBoxGutterSmoke.LongFixture();
        File.WriteAllBytes(source, original);
        string allLabels = string.Join('|', Enumerable.Range(1, 80).Select(page => "PAGE_" + page));
        if (PageDeletionSmoke.Read(source) != (80, allLabels)) throw new InvalidOperationException("Strict source control failed.");
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [source]);
        var pages = (ListBox)owner.FindName("PageList");
        Wait(() => pages.Items.Count == 80 && pages.ItemContainerGenerator.ContainerFromIndex(2) is ListBoxItem { ActualHeight: > 50 },
            "realized thumbnail drag controls");
        var scroller = Descendants<ScrollViewer>(pages).First();
        var bar = Descendants<ScrollBar>(pages).Single(element => element.IsVisible && element.Orientation == Orientation.Vertical);
        var thumb = Descendants<Thumb>(bar).Single(element => element.IsVisible);
        double[]? previousGeometry = null;
        long stableSince = Environment.TickCount64;
        Wait(() =>
        {
            owner.UpdateLayout();
            double[] geometry = [owner.ActualWidth, owner.ActualHeight, thumb.ActualWidth, thumb.ActualHeight,
                scroller.ViewportHeight, scroller.ExtentHeight];
            if (previousGeometry is null || !geometry.SequenceEqual(previousGeometry))
            { previousGeometry = geometry; stableSince = Environment.TickCount64; }
            return work.Contains(new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight))
                && Environment.TickCount64 - stableSince >= 150;
        }, "stable owned scrollbar geometry");
        Point start = thumb.TranslatePoint(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2), owner);
        RequireHit(start, target => ReferenceEquals(target, thumb), "actual scrollbar thumb");
        bool box = false, marker = false;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(20), DispatcherPriority.Input, (_, _) =>
        {
            if (owner.FindName("PageSelectionBox") is FrameworkElement { IsVisible: true }) box = true;
            if (owner.FindName("PageDropIndicator") is FrameworkElement { IsVisible: true }) marker = true;
        }, owner.Dispatcher);
        timer.Start();
        try { ThumbnailBoxSelectionSmoke.Drag(owner, start, start + new Vector(0, 40)); }
        finally { timer.Stop(); }
        if (scroller.VerticalOffset <= 0 || box || marker || Dirty(owner) || Mouse.Captured is not null
            || !File.ReadAllBytes(source).AsSpan().SequenceEqual(original))
            throw new InvalidOperationException("Dragging the scrollbar started selection, reordering, or changed the source.");
        Console.WriteLine($"CONTROL native scrollbar: offset={scroller.VerticalOffset}, box={box}, reorderMarker={marker}, source unchanged.");
        scroller.ScrollToTop();
        Wait(() => scroller.VerticalOffset == 0 && pages.ItemContainerGenerator.ContainerFromIndex(2) is ListBoxItem { ActualHeight: > 50 },
            "thumbnail drag returns to first viewport");
        owner.UpdateLayout();
        pages.SelectedIndex = 0;
        var first = (ListBoxItem)pages.ItemContainerGenerator.ContainerFromIndex(0);
        var third = (ListBoxItem)pages.ItemContainerGenerator.ContainerFromIndex(2);
        var imageBorder = Descendants<Border>(first).Single(element => element.Name == "ThumbBorder");
        start = imageBorder.TranslatePoint(new Point(imageBorder.ActualWidth / 2, imageBorder.ActualHeight / 2), owner);
        Point end = third.TranslatePoint(new Point(third.ActualWidth / 2, 8), owner);
        RequireHit(start, target => ReferenceEquals(target, imageBorder), "actual page thumbnail");
        box = false; marker = false;
        timer.Start();
        try { ThumbnailBoxSelectionSmoke.Drag(owner, start, end, allowNativeDropRelease: true); }
        finally { timer.Stop(); }
        Wait(() => Dirty(owner) && pages.Items.Count == 80, "native thumbnail reorder completion");
        if (box || !marker || Mouse.Captured is not null || !File.ReadAllBytes(source).AsSpan().SequenceEqual(original))
            throw new InvalidOperationException("A thumbnail drag became box selection or failed to preserve the source before Save.");
        string reorderedLabels = "PAGE_2|PAGE_1|" + string.Join('|', Enumerable.Range(3, 78).Select(page => "PAGE_" + page));
        Save(owner);
        if (PageDeletionSmoke.Read(source) != (80, reorderedLabels) || Dirty(owner))
            throw new InvalidOperationException("Native thumbnail reorder did not save exactly the expected original page identities.");
        Click(owner, "UndoBtn");
        Wait(() => Dirty(owner), "native reorder Undo");
        Save(owner);
        if (PageDeletionSmoke.Read(source) != (80, allLabels) || Dirty(owner))
            throw new InvalidOperationException("Undo did not restore the original order after a native thumbnail drag.");
        Console.WriteLine("PASS thumbnail drag controls: native scrollbar drag only scrolls, thumbnail drag still reorders, and strict Save/Undo retains exact page identities.");
        return 0;

        void RequireHit(Point point, Func<DependencyObject, bool> expected, string stage)
        {
            Point screen = owner.PointToScreen(point);
            Point rounded = owner.PointFromScreen(new Point(Math.Round(screen.X), Math.Round(screen.Y)));
            for (DependencyObject? hit = owner.InputHitTest(rounded) as DependencyObject; hit is not null; hit = VisualTreeHelper.GetParent(hit))
                if (expected(hit)) return;
            throw new InvalidOperationException("Native drag origin did not hit the " + stage + ".");
        }
    }
}
