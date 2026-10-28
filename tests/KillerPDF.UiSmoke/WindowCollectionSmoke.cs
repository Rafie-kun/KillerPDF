using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class WindowCollectionSmoke
{
    internal static int Run(Window survivor, string folder)
    {
        WeakReference control = CloseControl();
        Collect(control);
        if (control.IsAlive) throw new InvalidOperationException("The ordinary WPF window control is still retained.");
        string source = Path.Combine(folder, "collection-source.pdf");
        byte[] bytes = TableExportSmoke.Fixture();
        File.WriteAllBytes(source, bytes);
        for (int index = 0; index < 3; index++)
        {
            WeakReference closed = CloseDocumentWindow(survivor.GetType(), source);
            Collect(closed);
            if (!survivor.IsVisible || !File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes))
                throw new InvalidOperationException("Window collection changed the survivor or input PDF.");
            if (closed.IsAlive) throw new InvalidOperationException("APP-005: a closed document window remains retained while a sibling stays alive.");
            // The kit theme menu builds its rows when it opens, so it is opened the way a click opens it.
            DocumentStateSmoke.Click(survivor, "ThemeButton");
            var light = ThemeMenuRadios.Find(survivor, "ThemeLightRadio");
            var next = ThemeMenuRadios.Find(survivor, light.IsChecked == true ? "ThemeDarkRadio" : "ThemeLightRadio");
            ((System.Windows.Automation.Provider.ISelectionItemProvider)new System.Windows.Automation.Peers.RadioButtonAutomationPeer(next)
                .GetPattern(System.Windows.Automation.Peers.PatternInterface.SelectionItem)!).Select();
            DocumentStateSmoke.Wait(() => next.IsChecked == true, "live sibling theme selection");
            ((ContextMenu)survivor.FindName("ThemeMenu")).IsOpen = false;
            Pump();
            if (!survivor.IsVisible)
                throw new InvalidOperationException("The live sibling did not retain its selected theme.");
            Console.WriteLine("CONTROL closed document window collected and live sibling changed theme: " + (index + 1));
        }
        const string result = "PASS verify-window-collection: three closed document windows are collectible with a live theme-switching sibling and unchanged source.";
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        Console.WriteLine(result);
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CloseControl()
    {
        var window = new Window { Width = 200, Height = 100, Left = -10000, ShowInTaskbar = false };
        window.Show(); window.Close();
        return new WeakReference(window);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CloseDocumentWindow(Type type, string source)
    {
        var window = (Window)Activator.CreateInstance(type)!;
        window.Left = -10000; window.ShowInTaskbar = false; window.Show();
        type.GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(window, [source]);
        Pump();
        window.Close();
        var watch = Stopwatch.StartNew();
        while (window.IsVisible && watch.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); Thread.Sleep(10); }
        if (window.IsVisible) throw new InvalidOperationException("The synthetic document window did not close.");
        return new WeakReference(window);
    }

    private static void Collect(WeakReference reference)
    {
        for (int index = 0; index < 20 && reference.IsAlive; index++)
        {
            Pump(); GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); Thread.Sleep(50);
        }
    }

    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
