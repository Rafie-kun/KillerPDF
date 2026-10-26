using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class WatchReadinessSmoke
{
    internal static int Run(Window owner, string folder)
    {
        const string name = "NATIVE_WATCH_DEU";
        string input = Path.Combine(folder, "watch-input"), output = Path.Combine(folder, "watch-output");
        Directory.CreateDirectory(input); Directory.CreateDirectory(output);
        byte[] bytes = TableExportSmoke.Fixture();
        string held = Path.Combine(input, "preserved.hold"), source = Path.Combine(folder, "source.pdf");
        File.WriteAllBytes(held, bytes); File.WriteAllBytes(source, bytes);
        string macroPath = Path.Combine(folder, "macro-input.json");
        string json = JsonSerializer.Serialize(new { version = 1, name,
            steps = new[] { new { operation = "ocr", settings = new { language = "deu" } } } });
        File.WriteAllText(macroPath, json);
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        _ = engine.GetType("KillerPdf.Engine.Documents.PdfMacro", true)!.GetMethod("FromJson")!.Invoke(null, [json]);
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [source]);
        Wait(() => ((Button)owner.FindName("SaveMenuBtn")).IsEnabled, "watch setup source readiness");
        int phase = 0;
        bool saved = false, startReturned = false, pauseReturned = false, started = false, usefulError = false;
        string errorText = "";
        Exception? failure = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        void Queue(Button button, Action? after = null) => owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,
            (Action)(() => { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); after?.Invoke(); }));
        Button Named(Window window, string key) => Descendants<Button>(window).Single(button => Equals(button.Content, owner.FindResource(key)));
        T Id<T>(Window window, string id) where T : DependencyObject => Descendants<T>(window).Single(item => AutomationProperties.GetAutomationId(item) == id);
        timer.Tick += (_, _) =>
        {
            Window? dialog = Application.Current.Windows.Cast<Window>().LastOrDefault(window => window != owner && window.IsVisible);
            if (dialog is null) return;
            try
            {
                string type = dialog.GetType().Name;
                if (type == "MacroDialog" && phase == 0) { phase = 1; Queue(Named(dialog, "Str_Exchange_Import")); }
                else if (type == "FileDialog" && phase == 1)
                { ((TextBox)dialog.FindName("FileNameBox")).Text = macroPath; phase = 2; Queue((Button)dialog.FindName("AcceptButton")); }
                else if (type == "MacroDialog" && phase == 2)
                { phase = 3; Queue(Named(dialog, "Str_DocInfo_Save"), () => saved = true); }
                else if (type == "MacroDialog" && phase == 3 && saved)
                { phase = 4; Queue(Id<Button>(dialog, "OpenWatchedFolders")); }
                else if (type == "WatchedFolderDialog" && phase == 4)
                {
                    Id<TextBox>(dialog, "WatchInput").Text = input;
                    Id<TextBox>(dialog, "WatchOutput").Text = output;
                    Id<TextBox>(dialog, "WatchFilter").Text = "*.pdf";
                    ComboBox macros = Id<ComboBox>(dialog, "WatchMacro");
                    macros.SelectedIndex = Enumerable.Range(0, macros.Items.Count).Single(index =>
                        Equals(macros.Items[index].GetType().GetProperty("Label")!.GetValue(macros.Items[index]), name));
                    object macro = macros.SelectedItem.GetType().GetProperty("Macro")!.GetValue(macros.SelectedItem)!;
                    object step = ((System.Collections.IEnumerable)macro.GetType().GetProperty("Steps")!.GetValue(macro)!).Cast<object>().Single();
                    var settings = (IReadOnlyDictionary<string, string>)step.GetType().GetProperty("Settings")!.GetValue(step)!;
                    if (settings["language"] != "deu" || Directory.GetFiles(input, "*.pdf").Length != 0
                        || Directory.GetFiles(folder, "deu.*", SearchOption.AllDirectories).Length != 0
                        || Directory.GetDirectories(folder, "deu", SearchOption.AllDirectories).Length != 0)
                        throw new InvalidOperationException("Missing-language and empty-input fixture controls failed.");
                    Console.WriteLine("CONTROL selected saved OCR macro requests deu, no German model exists in isolation, and the input has no matching PDFs.");
                    phase = 5;
                }
                else if (type == "WatchedFolderDialog" && phase == 5)
                {
                    Button start = Id<Button>(dialog, "WatchStart");
                    if (start.IsEnabled) { phase = 6; Queue(start, () => startReturned = true); }
                    else { phase = 6; startReturned = true; }
                }
                else if (type == "WatchedFolderDialog" && phase == 6 && startReturned)
                {
                    errorText = Id<TextBlock>(dialog, "WatchError").Text;
                    started = Id<Button>(dialog, "WatchPause").IsEnabled;
                    usefulError = errorText.Contains("deu", StringComparison.OrdinalIgnoreCase);
                    Console.WriteLine($"CONTROL watch readiness: started={started}, error={errorText}");
                    phase = 7;
                    if (started) Queue(Id<Button>(dialog, "WatchPause"), () => pauseReturned = true);
                    else pauseReturned = true;
                }
                else if (type == "WatchedFolderDialog" && phase == 7 && pauseReturned && !Id<Button>(dialog, "WatchPause").IsEnabled)
                { phase = 8; Queue(Id<Button>(dialog, "WatchClose")); }
                else if (type == "MacroDialog" && phase == 8)
                { phase = 9; Queue(Descendants<Button>(dialog).Single(button => button.IsCancel)); }
                else if (type is not ("MacroDialog" or "FileDialog" or "WatchedFolderDialog"))
                    throw new InvalidOperationException("Unexpected watched setup dialog: " + type + ": "
                        + string.Join(" ", Descendants<TextBlock>(dialog).Select(text => text.Text)));
            }
            catch (Exception error)
            {
                failure = error;
                foreach (Window window in Application.Current.Windows.Cast<Window>().Where(window => window != owner).Reverse().ToArray()) window.Close();
            }
        };
        Click(owner, "SaveMenuBtn");
        ContextMenu? Menu() => PresentationSource.CurrentSources.Cast<PresentationSource>().Select(item => item.RootVisual)
            .OfType<DependencyObject>().SelectMany(Descendants<ContextMenu>).FirstOrDefault(menu => menu.IsOpen);
        ContextMenu? menu = Menu();
        menu ??= PresentationSource.CurrentSources.Cast<PresentationSource>().Select(item => item.RootVisual).OfType<ContextMenu>().Single(item => item.IsOpen);
        MenuItem tools = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, owner.FindResource("Str_DocumentTools")));
        MenuItem macrosItem = tools.Items.OfType<MenuItem>().Single(item => Equals(item.Header, owner.FindResource("Str_DocumentTools_Macros")));
        menu.IsOpen = false;
        timer.Start();
        try
        {
            macrosItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Wait(() => phase == 9 || failure is not null, "watched-folder missing-language setup");
            if (failure is not null) throw failure;
            Wait(() => !Application.Current.Windows.Cast<Window>().Any(window => window != owner && window.IsVisible), "watched setup modal cleanup");
        }
        finally { timer.Stop(); }
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes) || !File.ReadAllBytes(held).AsSpan().SequenceEqual(bytes)
            || Directory.GetFiles(output).Length != 0) throw new InvalidOperationException("Watched setup changed synthetic sources or produced output.");
        File.WriteAllText(Path.Combine(folder, "readiness.txt"), $"started={started}\nerror={errorText}\n");
        if (started || !usefulError) throw new InvalidOperationException("UI-005: watched setup started or failed to identify missing German OCR support before work.");
        const string result = "PASS verify-watch-readiness: missing German OCR support is identified before Start; empty input stays paused and source bytes/output remain intact.";
        File.WriteAllText(Path.Combine(folder, "result.txt"), result); Console.WriteLine(result);
        return 0;
    }
}
