using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class ToolSearchPrerequisitesSmoke
{
    internal static int Run(Window owner, string folder, bool certificates = false)
    {
        string commandKey = certificates ? "Str_CertCatalog_Title" : "Str_DocumentTools_Macros";
        string commandName = (string)owner.FindResource(commandKey);
        var pages = (ListBox)owner.FindName("PageList");
        if (pages.Items.Count != 0) throw new InvalidOperationException("Search prerequisite fixture must have no document.");
        IEnumerable<DependencyObject> Roots() => PresentationSource.CurrentSources.Cast<PresentationSource>()
            .Select(source => source.RootVisual).OfType<DependencyObject>();
        ContextMenu? Menu() => Roots().OfType<ContextMenu>().Concat(Roots().SelectMany(Descendants<ContextMenu>))
            .Distinct().SingleOrDefault(menu => menu.IsOpen);
        Click(owner, "SaveMenuBtn");
        Wait(() => Menu() is not null, "empty-window Save menu");
        ContextMenu menu = Menu()!;
        MenuItem tools = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, owner.FindResource("Str_DocumentTools")));
        MenuItem command = tools.Items.OfType<MenuItem>().Single(item => Equals(item.Header, commandName));
        if (!command.IsEnabled) throw new InvalidOperationException("The real " + commandName + " menu is disabled without a document.");
        menu.IsOpen = false;
        var host = (FrameworkElement)owner.FindName("ToolSearchHost");
        Descendants<Button>(host).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var input = Descendants<TextBox>(host).Single();
        Border[] Rows(string key)
        {
            string label = (string)owner.FindResource(key);
            return Roots().SelectMany(Descendants<Border>).Where(border =>
                AutomationProperties.GetName(border) == label || AutomationProperties.GetName(border)
                    .StartsWith(label + " (", StringComparison.Ordinal)).Distinct().ToArray();
        }
        input.Text = (string)owner.FindResource("Str_KS_Open");
        Wait(() => Rows("Str_KS_Open").Length == 1, "searchable Open presence control");
        input.Text = (string)owner.FindResource("Str_Lbl_Text");
        owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        if (Rows("Str_Lbl_Text").Length != 0) throw new InvalidOperationException("Document-only Text unexpectedly appeared in empty-window search.");
        Console.WriteLine("CONTROL empty window: real " + commandName + " menu enabled; search Open present and document-only Text absent.");
        input.Text = commandName;
        owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Border? row = Rows(commandKey).SingleOrDefault();
        if (row is null) throw new InvalidOperationException("WIRE-004: working " + commandName + " command is absent from empty-window tool search.");
        bool opened = false;
        Exception? failure = null;
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (!seen.Add(dialog)) continue;
                try
                {
                    if (dialog.GetType().Name != (certificates ? "CertificateCatalogDialog" : "MacroDialog"))
                        throw new InvalidOperationException("Search opened an unexpected dialog: " + dialog.GetType().Name);
                    opened = true;
                    Button cancel = Descendants<Button>(dialog).Single(button => button.IsCancel);
                    dialog.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                        (Action)(() => cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                }
                catch (Exception error) { failure = error; dialog.Close(); }
            }
        };
        timer.Start();
        try
        {
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
            Wait(() => opened || failure is not null, commandName + " execution from empty-window search");
            if (failure is not null) throw failure;
            Wait(() => !Application.Current.Windows.Cast<Window>().Any(window => window != owner && window.IsVisible), "search dialog Cancel cleanup");
        }
        finally { timer.Stop(); }
        if (!opened || pages.Items.Count != 0) throw new InvalidOperationException("Empty-window search changed document state or failed to open " + commandName + ".");
        string result = "PASS " + (certificates ? "verify-search-certificates" : "verify-search-prerequisites")
            + ": " + commandName + " opens from empty-window search while document-only Text stays unavailable.";
        File.WriteAllText(Path.Combine(folder, "result.txt"), result); Console.WriteLine(result);
        return 0;
    }
}
