using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class PageNavigationSmoke
{
    internal static int Run(Window owner, string folder)
    {
        owner.WindowState = WindowState.Normal;
        owner.Left = 32; owner.Top = 32; owner.Width = 950; owner.Height = 700;
        owner.Opacity = 1; owner.ShowActivated = true; owner.Activate();
        var previous = owner.FindName("PreviousPageBtn") as Button;
        var next = owner.FindName("NextPageBtn") as Button;
        if (previous is null || next is null)
            throw new InvalidOperationException("USER-001: optional page-navigation controls are missing.");
        var pages = (ListBox)owner.FindName("PageList");
        Hidden("empty window defaults to off");
        string single = Path.Combine(folder, "navigation-single.pdf");
        string multiple = Path.Combine(folder, "navigation-multiple.pdf");
        byte[] one = TableExportSmoke.Fixture(), five = PageDeletionSmoke.Fixture();
        File.WriteAllBytes(single, one); File.WriteAllBytes(multiple, five);
        Open(single, 1); Hidden("one-page default");
        Open(multiple, 5); Hidden("multi-page default");
        Toggle();
        Wait(() => previous.IsVisible && next.IsVisible, "enabled page navigation");
        if (previous.IsEnabled || !next.IsEnabled || pages.SelectedIndex != 0)
            throw new InvalidOperationException("Navigation boundaries do not match the first page.");
        DocumentStateSmoke.Click(owner, "NextPageBtn");
        Wait(() => pages.SelectedIndex == 1, "Next page");
        DocumentStateSmoke.Click(owner, "PreviousPageBtn");
        Wait(() => pages.SelectedIndex == 0, "Previous page");
        for (int index = 1; index < 5; index++)
        {
            DocumentStateSmoke.Click(owner, "NextPageBtn");
            int expected = index;
            Wait(() => pages.SelectedIndex == expected, "next page boundary");
        }
        if (!previous.IsEnabled || next.IsEnabled)
            throw new InvalidOperationException("Navigation boundaries do not match the last page.");
        Toggle(); Hidden("saved preference off");
        Open(single, 1); Toggle(); Hidden("enabled preference on a one-page document");
        Open(multiple, 5);
        Wait(() => previous.IsVisible && next.IsVisible, "enabled preference follows document change");
        if (!File.ReadAllBytes(single).AsSpan().SequenceEqual(one)
            || !File.ReadAllBytes(multiple).AsSpan().SequenceEqual(five) || pages.Items.Count != 5)
            throw new InvalidOperationException("Page navigation changed document bytes or page count.");
        Console.WriteLine("PASS optional page navigation: default off, zero/one-page hiding, toggle discovery, page boundaries and unchanged source bytes.");
        return 0;

        void Toggle() => ParagraphEditSmoke.RunSearchAction(owner, "Str_NavigationButtons_Show", "Alt+Shift+N");
        void Hidden(string stage)
        {
            Pump();
            if (previous.Visibility != Visibility.Collapsed || next.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException("USER-001: navigation arrows are exposed during " + stage + ".");
            Console.WriteLine("CONTROL navigation arrows hidden: " + stage + ".");
        }
        void Open(string path, int count)
        {
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [path]);
            Wait(() => pages.Items.Count == count && pages.SelectedIndex >= 0, "synthetic navigation source");
        }
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static void Wait(Func<bool> ready, string stage)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            Pump();
            if (timer.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(stage);
            Thread.Sleep(10);
        }
    }
}
