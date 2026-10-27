using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

// These diagnostic arms compare input ordering with and without WPF acknowledgment.
// A legacy ordering failure is evidence about the driver, not an app-defect verdict.
internal static class PageNavigationFocusProbe
{
    private sealed class ClickState(FrameworkElement pane)
    {
        internal FrameworkElement Pane { get; } = pane;
        internal int InjectedAt;
        internal bool Down;
        internal bool Up;
    }

    internal static int Run(Window owner, FrameworkElement paneA, FrameworkElement paneB,
        string five, string single, IReadOnlyDictionary<string, byte[]> sources, string arm)
    {
        if (arm is not ("guarded" or "legacy")) throw new ArgumentException("Unknown focus probe arm.");
        bool guarded = arm == "guarded";
        var pages = (ListBox)owner.FindName("PageList");
        var filename = (TextBlock)owner.FindName("FileNameLabel");
        var next = (Button)owner.FindName("NextPageBtn");
        var previous = (Button)owner.FindName("PreviousPageBtn");
        string phase = "probe-start";
        var clock = Stopwatch.StartNew();
        ClickState? current = null;
        int normalPending = 0, backgroundPending = 0;
        NotifyInputEventHandler before = (_, input) => Observe("pre", input.StagingItem.Input);
        ProcessInputEventHandler after = (_, input) => Observe("post", input.StagingItem.Input);
        InputManager.Current.PreNotifyInput += before;
        InputManager.Current.PostProcessInput += after;
        using var heartbeat = new System.Threading.Timer(_ =>
        {
            Console.WriteLine($"WATCHDOG ms={clock.ElapsedMilliseconds} phase={Volatile.Read(ref phase)}");
            if (Interlocked.Exchange(ref normalPending, 1) == 0)
                owner.Dispatcher.BeginInvoke(DispatcherPriority.Normal, (Action)(() =>
                {
                    Interlocked.Exchange(ref normalPending, 0);
                    Console.WriteLine($"PROGRESS normal ms={clock.ElapsedMilliseconds} phase={phase}");
                }));
            if (Interlocked.Exchange(ref backgroundPending, 1) == 0)
                owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() =>
                {
                    Interlocked.Exchange(ref backgroundPending, 0);
                    Console.WriteLine($"PROGRESS background ms={clock.ElapsedMilliseconds} phase={phase}");
                }));
        }, null, 1000, 1000);
        try
        {
            Pane(paneB);
            if (guarded) Wait(() => pages.Items.Count == 0 && filename.Text == "", "focused empty B pane");
            phase = "B-open-start"; LogState();
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [single]);
            phase = "B-open-return"; LogState();
            Wait(() => pages.Items.Count == 1 && !next.IsVisible && !previous.IsVisible, "B single-page visible state");
            Pane(paneA);
            phase = "A-visible-wait"; LogState();
            Wait(() => pages.Items.Count == 5 && filename.Text == Path.GetFileName(five) && next.IsVisible
                && ((TextBox)owner.FindName("PageJumpBox")).Text == "1", "A five-page recovery after pane click");
            phase = "A-visible-verified"; LogState();
            foreach (var source in sources)
                if (!File.ReadAllBytes(source.Key).AsSpan().SequenceEqual(source.Value)) throw new InvalidOperationException("Focus probe changed source bytes.");
            if (DocumentStateSmoke.Dirty(owner)) throw new InvalidOperationException("Focus probe changed document edit state.");
            Console.WriteLine("PASS navigation focus " + arm + ": exact mode prefix, native B/open/A and visible document/navigation ownership preserved.");
            return 0;
        }
        finally
        {
            InputManager.Current.PreNotifyInput -= before;
            InputManager.Current.PostProcessInput -= after;
        }

        void Pane(FrameworkElement pane)
        {
            phase = pane.Name + "-click-start";
            var state = new ClickState(pane); current = state;
            Point point = NeutralPaneFocusPoint(owner, pane);
            ShortcutHintsSmoke.InjectClickAt(owner, point, () =>
            {
                state.InjectedAt = Environment.TickCount;
                Console.WriteLine($"INJECT ms={clock.ElapsedMilliseconds} phase={phase} tick={state.InjectedAt} pane={pane.Name}");
            });
            if (guarded)
                Wait(() => state.Down && state.Up && Mouse.Captured is null, "intended pane routed down/up and capture release");
            phase = pane.Name + "-click-return"; LogState();
        }
        void Observe(string kind, InputEventArgs input)
        {
            if (input is not MouseButtonEventArgs mouse || mouse.ChangedButton != MouseButton.Left) return;
            bool owned = ReferenceEquals(mouse.MouseDevice.ActiveSource, PresentationSource.FromVisual(owner));
            if (!owned) return;
            bool intended = current is not null && Within(mouse.OriginalSource as DependencyObject, current.Pane);
            Console.WriteLine($"INPUT {kind} ms={clock.ElapsedMilliseconds} phase={phase} event={mouse.RoutedEvent?.Name} tick={mouse.Timestamp} source={Name(mouse.OriginalSource as DependencyObject)} intended={intended} handled={mouse.Handled} captured={Name(Mouse.Captured as DependencyObject)}");
            if (kind != "post" || !intended || current is null
                || unchecked((uint)(mouse.Timestamp - current.InjectedAt)) >= 0x80000000u) return;
            if (mouse.RoutedEvent == Mouse.MouseDownEvent || mouse.RoutedEvent == Mouse.PreviewMouseDownEvent && mouse.Handled) current.Down = true;
            if (current.Down && (mouse.RoutedEvent == Mouse.MouseUpEvent || mouse.RoutedEvent == Mouse.PreviewMouseUpEvent && mouse.Handled)) current.Up = true;
        }
        void LogState() => Console.WriteLine($"STATE ms={clock.ElapsedMilliseconds} phase={phase} pages={pages.Items.Count} file={filename.Text} previous={previous.IsVisible}/{previous.IsEnabled} next={next.IsVisible}/{next.IsEnabled} captured={Name(Mouse.Captured as DependencyObject)}");
        void Wait(Func<bool> ready, string stage)
        {
            var watch = Stopwatch.StartNew();
            while (!ready())
            {
                PumpSlice();
                if (watch.Elapsed > TimeSpan.FromSeconds(15))
                {
                    LogState();
                    throw new TimeoutException("Focus probe at " + stage);
                }
            }
        }
    }

    private static void PumpSlice()
    {
        var frame = new DispatcherFrame();
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        using var timer = new System.Threading.Timer(_ => dispatcher.BeginInvoke(DispatcherPriority.Send,
            (Action)(() => frame.Continue = false)), null, 20, Timeout.Infinite);
        Dispatcher.PushFrame(frame);
    }

    internal static Point NeutralPaneFocusPoint(Window owner, FrameworkElement pane)
    {
        Border border = DocumentStateSmoke.Descendants<Border>(pane).Single(element => element.Name == "PaneBorder");
        DocumentStateSmoke.Wait(() => border.IsVisible && border.ActualWidth > 4 && border.ActualHeight > 4,
            "owned neutral pane border is ready");
        owner.UpdateLayout();
        foreach (Point candidate in new[] { new Point(.25, border.ActualHeight / 2),
                     new Point(border.ActualWidth - .25, border.ActualHeight / 2),
                     new Point(border.ActualWidth / 2, border.ActualHeight - .25) })
        {
            Point screen = border.PointToScreen(candidate);
            screen = new Point(Math.Round(screen.X), Math.Round(screen.Y));
            var hit = pane.InputHitTest(pane.PointFromScreen(screen));
            if (!ReferenceEquals(hit, border)) continue;
            Console.WriteLine($"CONTROL neutral pane target: pane={pane.Name}, hit={Name(border)}, screen={screen}.");
            return owner.PointFromScreen(screen);
        }
        throw new InvalidOperationException("No native-pixel-safe neutral border target in " + pane.Name);
    }

    private static string Name(DependencyObject? value) => value is FrameworkElement element
        ? element.GetType().Name + ":" + element.Name : value?.GetType().Name ?? "none";

    private static bool Within(DependencyObject? value, DependencyObject expected)
    {
        for (DependencyObject? node = value; node is not null; node = node is Visual
            ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            if (ReferenceEquals(node, expected)) return true;
        return false;
    }
}
