using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using static DocumentStateSmoke;

internal static class PageNavigationSidebarSmoke
{
    internal static int Run(Window owner, string folder)
    {
        owner.WindowState = WindowState.Normal;
        owner.Left = 16; owner.Top = 16; owner.Width = 950; owner.Height = 700;
        owner.Opacity = 1; owner.ShowActivated = true; owner.Activate();
        string path = Path.Combine(folder, "sidebar-two-pages.pdf");
        byte[] original = PageDeletionSmoke.Fixture(2);
        File.WriteAllBytes(path, original);
        var pages = (ListBox)owner.FindName("PageList");
        var jump = (TextBox)owner.FindName("PageJumpBox");
        var previous = (Button)owner.FindName("PreviousPageBtn");
        var next = (Button)owner.FindName("NextPageBtn");
        var pane = (FrameworkElement)owner.FindName("Viewer");
        ParagraphEditSmoke.RunSearchAction(owner, "Str_NavigationButtons_Show", "Alt+Shift+N");
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [path]);
        Wait(() => pages.Items.Count == 2 && previous.IsVisible && next.IsVisible, "sidebar navigation source");
        Snapshot("after open");
        var single = (RadioButton)owner.FindName("ViewSingleRadio");
        Wait(() => single.IsVisible && single.IsEnabled, "Single mode is available after document installation");
        ((ISelectionItemProvider)new RadioButtonAutomationPeer(single).GetPattern(PatternInterface.SelectionItem)!).Select();
        pages.SelectedIndex = 0;
        WaitPage(0, red: true);
        Snapshot("settled first Single page");
        Require(next.IsEnabled && !previous.IsEnabled, "Initial Single-page navigation boundaries are stale.");
        Click(owner, "NextPageBtn");
        WaitPage(1, red: false);
        Snapshot("Next reached second page");
        Require(previous.IsEnabled && !next.IsEnabled, "Last-page navigation boundaries are stale.");
        pages.SelectedIndex = 0;
        WaitPage(0, red: true);
        Snapshot("sidebar returned to first page");
        Require(next.IsEnabled && !previous.IsEnabled, "Sidebar selection left navigation boundaries stale after the first page rendered.");
        Click(owner, "NextPageBtn");
        WaitPage(1, red: false);
        Require(!Dirty(owner) && File.ReadAllBytes(path).AsSpan().SequenceEqual(original), "Sidebar navigation changed source or edit state.");
        Console.WriteLine("PASS navigation sidebar: initial Single load and thumbnail selection keep boundaries usable after actual red/green page rendering.");
        return 0;

        bool Color(bool red) => PageNavigationModesSmoke.ColorPresent(pane, red ? (byte)255 : (byte)0, red ? (byte)0 : (byte)255, 0);
        void WaitPage(int index, bool red) => Wait(() => pages.SelectedIndex == index && jump.Text == (index + 1).ToString()
            && single.IsChecked == true && Color(red) && !Color(!red), "settled Single page " + index);
        void Snapshot(string stage) => Console.WriteLine($"CONTROL {stage}: selected={pages.SelectedIndex}, jump={jump.Text}, previous={previous.IsVisible}/{previous.IsEnabled}, next={next.IsVisible}/{next.IsEnabled}, red={Color(true)}, green={Color(false)}.");
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
