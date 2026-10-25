using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

internal static class WindowSessionSmoke
{
    internal static int Run(Window first, Assembly assembly, string appPath, string folder,
        string scenario = "--verify-window-session")
    {
        Type windowType = first.GetType();
        var second = (Window)Activator.CreateInstance(windowType)!;
        Window? restored = null;
        string firstPath = Path.Combine(folder, "session-first.pdf");
        string secondPath = Path.Combine(folder, "session-second.pdf");
        byte[] firstBytes = SignedCopySmoke.Fixture(measurement: false);
        byte[] secondBytes = SignedCopySmoke.Fixture(measurement: false, pageWidth: 300);
        File.WriteAllBytes(firstPath, firstBytes);
        File.WriteAllBytes(secondPath, secondBytes);
        try
        {
            second.Show();
            Pump();
            first.WindowState = second.WindowState = WindowState.Normal;
            first.Width = 880; first.Height = 620; first.Left = 32; first.Top = 40;
            second.Width = 660; second.Height = 500; second.Left = 110; second.Top = 80;
            MethodInfo open = windowType.GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!;
            open.Invoke(first, [firstPath]);
            open.Invoke(second, [secondPath]);
            Pump();
            double width = first.ActualWidth, height = first.ActualHeight, left = first.Left, top = first.Top;
            bool manualClose = scenario.EndsWith("manual-close", StringComparison.Ordinal);
            bool closeAll = scenario.EndsWith("close-all", StringComparison.Ordinal);
            bool forget = scenario.EndsWith("forget", StringComparison.Ordinal);
            if (forget)
                assembly.GetType("KillerPDF.App", true)!.GetMethod("SetSetting",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                    .Invoke(null, ["RememberOpenFiles", "0"]);
            Console.WriteLine("Session: " + scenario);
            if (scenario.EndsWith("secondary-first", StringComparison.Ordinal))
            {
                Close(second);
                Close(first);
            }
            else
            {
                Close(first);
                if (manualClose || closeAll)
                {
                    open.Invoke(second, [firstPath]);
                    Pump();
                    TextBlock label = Descendants(second).OfType<TextBlock>()
                        .Single(block => block.Name == "tabLbl" && block.Text == Path.GetFileNameWithoutExtension(firstPath));
                    DependencyObject tab = label;
                    while (tab is not FrameworkElement { Name: "tabBd" })
                        tab = VisualTreeHelper.GetParent(tab) ?? throw new InvalidOperationException("Tab frame is missing.");
                    if (closeAll)
                    {
                        ((UIElement)tab).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Right)
                            { RoutedEvent = UIElement.MouseRightButtonUpEvent });
                        Pump();
                        ContextMenu menu = PresentationSource.CurrentSources.Cast<PresentationSource>()
                            .Select(source => source.RootVisual).OfType<DependencyObject>()
                            .SelectMany(Descendants).OfType<ContextMenu>().Single(candidate => candidate.IsOpen);
                        MenuItem action = menu.Items.OfType<MenuItem>().Single(item =>
                            item.InputGestureText.Contains("Ctrl", StringComparison.Ordinal)
                            && item.InputGestureText.EndsWith("Q", StringComparison.Ordinal));
                        action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                        menu.IsOpen = false;
                    }
                    else Descendants(tab).OfType<Button>().Single(button => button.Name == "tabClose")
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Pump();
                }
            }
            Close(second);
            Console.WriteLine("Session: construct a fresh document window from persisted settings");
            restored = (Window)Activator.CreateInstance(windowType)!;
            restored.Show();
            Pump();
            string[] captions = Descendants(restored).OfType<ItemsControl>()
                .Where(control => control.Name == "TabStrip" && control.IsVisible)
                .SelectMany(Descendants).OfType<TextBlock>().Select(block => block.Text).ToArray();
            bool tabs = captions.Contains(Path.GetFileNameWithoutExtension(firstPath))
                && captions.Contains(Path.GetFileNameWithoutExtension(secondPath));
            if (manualClose)
                tabs = Descendants(restored).OfType<ItemsControl>().Where(control => control.Name == "TabStrip")
                    .All(control => !control.IsVisible)
                    && Descendants(restored).OfType<TextBlock>().Any(block => block.Name == "FileNameLabel"
                        && block.Text == Path.GetFileName(secondPath));
            if (forget || closeAll)
                tabs = Descendants(restored).OfType<FrameworkElement>()
                    .Any(element => element.Name == "DropZone" && element.IsVisible);
            bool geometry = Math.Abs(restored.ActualWidth - width) < 1 && Math.Abs(restored.ActualHeight - height) < 1
                && Math.Abs(restored.Left - left) < 1 && Math.Abs(restored.Top - top) < 1;
            Console.WriteLine("Session: restored captions=" + string.Join("|", captions)
                + $"; bounds={restored.ActualWidth},{restored.ActualHeight},{restored.Left},{restored.Top}"
                + $"; expected={width},{height},{left},{top}");
            if (!tabs || !geometry)
                throw new InvalidOperationException($"Session restore failed: expected tabs={tabs}, original geometry={geometry}.");
            if (!File.ReadAllBytes(firstPath).AsSpan().SequenceEqual(firstBytes)
                || !File.ReadAllBytes(secondPath).AsSpan().SequenceEqual(secondBytes))
                throw new InvalidOperationException("Session restoration changed a source PDF.");
            string result = "PASS " + scenario + " expected tab restoration, original-window geometry and unchanged sources. App SHA256="
                + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
            File.WriteAllText(Path.Combine(folder, "result.txt"), result);
            Console.WriteLine(result);
            return 0;
        }
        finally
        {
            if (restored is not null) Close(restored);
            Close(second);
        }
    }

    private static void Close(Window window)
    {
        if (PresentationSource.FromVisual(window) is null) return;
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        var elapsed = Stopwatch.StartNew();
        while (!closed && elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            Pump();
            Thread.Sleep(15);
        }
        if (!closed) throw new TimeoutException("A session fixture window did not close.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (DependencyObject child in Descendants(VisualTreeHelper.GetChild(parent, index)))
                yield return child;
    }

    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
