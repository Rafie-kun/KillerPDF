using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using static DocumentStateSmoke;

internal static class ThumbnailBoxSelectionSmoke
{
    internal static int Run(Window owner, string folder, bool controls = false)
    {
        owner.WindowState = WindowState.Normal;
        Rect workArea = SystemParameters.WorkArea;
        owner.Left = workArea.Left + 8; owner.Top = workArea.Top + 8;
        owner.Width = Math.Min(950, workArea.Width - 16); owner.Height = Math.Min(700, workArea.Height - 16);
        owner.Opacity = 1; owner.Activate();
        string path = Path.Combine(folder, "box-selection-two-pages.pdf");
        byte[] original = PageDeletionSmoke.Fixture(2);
        File.WriteAllBytes(path, original);
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [path]);
        var pages = (ListBox)owner.FindName("PageList");
        Wait(() => pages.Items.Count == 2 && pages.ItemContainerGenerator.ContainerFromIndex(1) is ListBoxItem { ActualHeight: > 20 },
            "two realized sidebar thumbnails");
        Rect previousBounds = Rect.Empty;
        long stableSince = Environment.TickCount64;
        Wait(() =>
        {
            Rect current = new(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight);
            if (current != previousBounds) { previousBounds = current; stableSince = Environment.TickCount64; }
            return workArea.Contains(current) && Environment.TickCount64 - stableSince > 150;
        }, "stable owned window entirely inside the current work area");
        owner.UpdateLayout();
        var viewport = Descendants<ScrollContentPresenter>(pages).First(element => element.IsVisible && element.ActualHeight > 20);
        var first = (ListBoxItem)pages.ItemContainerGenerator.ContainerFromIndex(0);
        var last = (ListBoxItem)pages.ItemContainerGenerator.ContainerFromIndex(1);
        Point viewOrigin = viewport.TranslatePoint(new Point(), owner);
        Point firstOrigin = first.TranslatePoint(new Point(), owner);
        Point lastBottom = last.TranslatePoint(new Point(0, last.ActualHeight), owner);
        Point start = new(viewOrigin.X + 3, viewOrigin.Y + viewport.ActualHeight - 8);
        Point end = new(viewOrigin.X + viewport.ActualWidth - 20, firstOrigin.Y + 3);
        if (start.Y <= lastBottom.Y + 8) throw new InvalidOperationException("The fixture has no verified empty sidebar space below both thumbnails.");
        Point rounded = owner.PointFromScreen(Round(owner.PointToScreen(start)));
        bool insideList = false;
        for (DependencyObject? hit = owner.InputHitTest(rounded) as DependencyObject; hit is not null;
             hit = System.Windows.Media.VisualTreeHelper.GetParent(hit))
        {
            if (hit is ListBoxItem or ScrollBar or ButtonBase)
                throw new InvalidOperationException("The empty-space selection origin hits an interactive sidebar control.");
            if (ReferenceEquals(hit, pages)) { insideList = true; break; }
        }
        if (!insideList) throw new InvalidOperationException("The selection origin is outside the owned thumbnail list.");
        pages.SelectedItems.Clear();
        bool overlaySeen = false;
        bool spanningCaptured = false;
        int observedMoves = 0;
        Capture("box-before-initial.png");
        ProcessInputEventHandler observe = (_, input) =>
        {
            if (input.StagingItem.Input is not MouseEventArgs mouse
                || !ReferenceEquals(mouse.MouseDevice.ActiveSource?.RootVisual, owner)
                || mouse.RoutedEvent != Mouse.PreviewMouseMoveEvent && mouse.RoutedEvent != Mouse.MouseMoveEvent) return;
            if (mouse.LeftButton == MouseButtonState.Pressed && observedMoves++ < 4
                && owner.FindName("PageSelectionBox") is FrameworkElement diagnostic)
                Console.WriteLine($"CONTROL box observer: event={mouse.RoutedEvent.Name}, handled={mouse.Handled}, visible={diagnostic.IsVisible}, size={diagnostic.ActualWidth}x{diagnostic.ActualHeight}.");
            if (owner.FindName("PageSelectionBox") is FrameworkElement { IsVisible: true, ActualWidth: > 2, ActualHeight: > 2 })
            {
                if (!overlaySeen) Capture("box-visible-initial.png");
                overlaySeen = true;
                if (!spanningCaptured && pages.SelectedItems.Count == 2)
                {
                    spanningCaptured = true;
                    Capture("box-spanning-initial.png");
                }
            }
        };
        InputManager.Current.PostProcessInput += observe;
        try { Drag(owner, start, end); }
        finally { InputManager.Current.PostProcessInput -= observe; }
        Capture("box-finished-initial.png");
        int[] selected = pages.SelectedItems.Cast<object>().Select(pages.Items.IndexOf).Order().ToArray();
        Console.WriteLine($"CONTROL box selection: selected={string.Join(',', selected)}, overlay={overlaySeen}, dirty={Dirty(owner)}.");
        if (!selected.SequenceEqual(new[] { 0, 1 }) || !overlaySeen)
            throw new InvalidOperationException("Dragging from empty sidebar space did not show a selection box and select both intersected thumbnails.");
        if (owner.FindName("PageSelectionBox") is FrameworkElement { IsVisible: true })
            throw new InvalidOperationException("The thumbnail selection box remained visible after release.");
        if (Dirty(owner) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(original))
            throw new InvalidOperationException("Thumbnail box selection changed the document or its source.");
        Console.WriteLine("PASS thumbnail box selection: native empty-space drag selects both pages with a temporary visible box and no document change.");
        if (controls)
        {
            SetSelection(0);
            Capture("box-absent.png");
            RunControl("cancel", end, [0], cancel: true);
            Point secondOnly = new(end.X, last.TranslatePoint(new Point(), owner).Y + 3);
            RunControl("control-add", secondOnly, [0, 1], modifier: 0x11);
            RunControl("control-remove", secondOnly, [0], modifier: 0x11);
            RunControl("shift-add", secondOnly, [0, 1], modifier: 0x10);
            RunControl("replace", secondOnly, [1]);
            Console.WriteLine("PASS thumbnail box controls: cancellation restores selection, Control toggles, Shift adds, ordinary drag replaces, and the source stays unchanged.");
        }
        return 0;

        void SetSelection(params int[] indexes)
        {
            pages.SelectedItems.Clear();
            foreach (int index in indexes) pages.SelectedItems.Add(pages.Items[index]);
            owner.UpdateLayout();
        }
        void RunControl(string name, Point destination, int[] expected, byte modifier = 0, bool cancel = false)
        {
            bool visible = false, captured = false;
            ProcessInputEventHandler controlObserver = (_, input) =>
            {
                if (input.StagingItem.Input is not MouseEventArgs mouse
                    || !ReferenceEquals(mouse.MouseDevice.ActiveSource?.RootVisual, owner)
                    || mouse.RoutedEvent != Mouse.PreviewMouseMoveEvent && mouse.RoutedEvent != Mouse.MouseMoveEvent) return;
                if (owner.FindName("PageSelectionBox") is not FrameworkElement { IsVisible: true, ActualWidth: > 2, ActualHeight: > 2 }) return;
                visible = true;
                if (!captured) { captured = true; Capture("box-visible-" + name + ".png"); }
            };
            InputManager.Current.PostProcessInput += controlObserver;
            try { Drag(owner, start, destination, modifier, cancel); }
            finally { InputManager.Current.PostProcessInput -= controlObserver; }
            int[] actual = pages.SelectedItems.Cast<object>().Select(pages.Items.IndexOf).Order().ToArray();
            if (!visible || !actual.SequenceEqual(expected) || Mouse.Captured is not null
                || owner.FindName("PageSelectionBox") is FrameworkElement { IsVisible: true }
                || Dirty(owner) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(original))
                throw new InvalidOperationException($"Thumbnail box {name} failed: visible={visible}, selected={string.Join(',', actual)}, expected={string.Join(',', expected)}.");
            Capture("box-finished-" + name + ".png");
            Console.WriteLine($"CONTROL thumbnail box {name}: selected={string.Join(',', actual)}, inputs released, source and dirty state unchanged.");
        }
        void Capture(string name)
        {
            owner.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(owner.ActualWidth), (int)Math.Ceiling(owner.ActualHeight),
                96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(owner);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(folder, name));
            encoder.Save(output);
        }
    }

    internal static void Drag(Window owner, Point start, Point end, byte modifier = 0, bool cancel = false,
        bool allowNativeDropRelease = false, int holdMilliseconds = 150)
    {
        ShortcutHintsSmoke.MovePointer(owner, start);
        IntPtr handle = new WindowInteropHelper(owner).Handle;
        Point source = Round(owner.PointToScreen(start)), target = Round(owner.PointToScreen(end));
        Exception? failure = null;
        bool released = false;
        ProcessInputEventHandler processed = (_, input) =>
        {
            if (input.StagingItem.Input is MouseButtonEventArgs { ChangedButton: MouseButton.Left, ButtonState: MouseButtonState.Released } mouse
                && ReferenceEquals(mouse.MouseDevice.ActiveSource?.RootVisual, owner)) released = true;
        };
        InputManager.Current.PostProcessInput += processed;
        var thread = new Thread(() =>
        {
            try
            {
                Check(source);
                if (modifier != 0) KeyEvent(modifier, 0, 0, UIntPtr.Zero);
                MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
                Thread.Sleep(100);
                for (int step = 1; step <= 12; step++)
                {
                    Point next = Round(source + (target - source) * (step / 12.0));
                    Check(next);
                    if (!SetCursorPos((int)next.X, (int)next.Y)) throw new InvalidOperationException("Cannot move the owned drag pointer.");
                    Thread.Sleep(30);
                }
                Thread.Sleep(holdMilliseconds);
                if (cancel)
                {
                    Check(target);
                    KeyEvent(0x1B, 0, 0, UIntPtr.Zero);
                    KeyEvent(0x1B, 0, 2, UIntPtr.Zero);
                    Thread.Sleep(100);
                }
            }
            catch (Exception error) { failure = error; }
            finally
            {
                MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
                if (modifier != 0) KeyEvent(modifier, 0, 2, UIntPtr.Zero);
                if (cancel) KeyEvent(0x1B, 0, 2, UIntPtr.Zero);
            }
        }) { IsBackground = true, Name = "Owned thumbnail selection drag" };
        try
        {
            thread.Start();
            Wait(() => !thread.IsAlive && (GetAsyncKeyState(1) & 0x8000) == 0 && Mouse.LeftButton == MouseButtonState.Released
                && (released || allowNativeDropRelease && Mouse.Captured is null),
                "native sidebar drag release consumed by WPF");
            if (failure is not null) throw new InvalidOperationException("Owned sidebar drag failed.", failure);
        }
        finally
        {
            MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
            if (modifier != 0) KeyEvent(modifier, 0, 2, UIntPtr.Zero);
            if (cancel) KeyEvent(0x1B, 0, 2, UIntPtr.Zero);
            thread.Join(TimeSpan.FromSeconds(2));
            InputManager.Current.PostProcessInput -= processed;
        }
        void Check(Point point)
        {
            if (GetForegroundWindow() != handle || WindowFromPoint(new NativePoint { X = (int)point.X, Y = (int)point.Y }) != handle)
                throw new InvalidOperationException("The drag point is outside the owned foreground window.");
        }
    }

    private static Point Round(Point point) => new(Math.Round(point.X), Math.Round(point.Y));
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeyEvent(byte key, byte scan, uint flags, UIntPtr extra);
}
