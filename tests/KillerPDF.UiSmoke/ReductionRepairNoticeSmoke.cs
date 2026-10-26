using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

internal static class ReductionRepairNoticeSmoke
{
    private const string Notice = "Saved with repairs to broken references. Missing content was not recovered.";
    private const string LiveText = "Keep reduced text";

    internal static int Run(Window owner, Assembly app, string appPath, string folder)
    {
        Type locale = app.GetType("KillerPDF.Services.Locale", true)!;
        app.GetType("KillerPDF.Services.LocaleManager", true)!.GetMethod("Apply")!
            .Invoke(null, [Enum.Parse(locale, "EnUS")]);
        if (!Equals(Application.Current.FindResource("Str_Save_ReferenceRepairs"), Notice))
            throw new InvalidOperationException("The approved English repair notice resource is unavailable.");
        VerifyDetector(owner);
        var failures = new List<Exception>();
        foreach (bool repaired in new[] { false, true })
        {
            string name = repaired ? "repaired" : "pristine";
            try
            {
                RunCase(owner, folder, name, repaired);
                Console.WriteLine("PASS " + name + " Reduce File Size default-downsample workflow");
            }
            catch (Exception error)
            {
                failures.Add(new InvalidOperationException(name + ": " + error.Message, error));
                File.WriteAllText(Path.Combine(folder, name + "-error.txt"), error.ToString());
                Console.WriteLine("FAIL " + name + ": " + error.Message);
            }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
        string result = "PASS real Save menu Reduce File Size with default image-downsampling settings: "
            + "pristine success omits repair notice; repaired success includes it. Both saved copies reopen "
            + "with known live text and unchanged source bytes. Status names the output and its actual size. "
            + "Notice detector passed target-present and target-removed controls. App SHA256="
            + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        Console.WriteLine(result);
        return 0;
    }

    private static bool HasNotice(DependencyObject root) => Children(root).OfType<TextBlock>()
        .Any(block => block.IsVisible && block.ActualWidth > 0 && block.ActualHeight > 0
            && block.Text.Contains(Notice, StringComparison.Ordinal));

    private static void VerifyDetector(Window owner)
    {
        var target = new TextBlock { Text = "Saved synthetic-reduced.pdf: 0.01 MB to 0.01 MB, images resampled: 0 " + Notice,
            TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Ready" });
        panel.Children.Add(target);
        var control = new Window { Owner = owner, Content = panel, Width = 440, Height = 160,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false };
        try
        {
            control.Show(); Pump(); control.UpdateLayout();
            if (!HasNotice(control)) throw new InvalidOperationException("Notice detector missed its present control.");
            target.Text = "Saved synthetic-reduced.pdf: 0.01 MB to 0.01 MB, images resampled: 0";
            control.UpdateLayout(); Pump();
            if (HasNotice(control)) throw new InvalidOperationException("Notice detector accepted neighboring success text after target removal.");
        }
        finally { control.Close(); }
        Console.WriteLine("PASS rendered reduction notice detector present/removed controls");
    }

    private static void RunCase(Window owner, string folder, string name, bool repaired)
    {
        string source = Path.Combine(folder, name + "-source.pdf");
        string output = Path.Combine(folder, name + "-reduced.pdf");
        byte[] original = Fixture(repaired);
        File.WriteAllBytes(source, original);
        var baseline = Application.Current.Windows.Cast<Window>().ToHashSet();
        var events = new List<string>();
        var handled = new HashSet<Window>();
        int stage = 0;
        bool done = false;
        Exception? failure = null;
        string? status = null;
        ContextMenu? openedMenu = null;
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        Window[] OwnedWindows() => Application.Current.Windows.Cast<Window>()
            .Where(window => !baseline.Contains(window) && OwnedBy(window, owner)).ToArray();
        void CloseOwned()
        {
            if (openedMenu is not null) openedMenu.IsOpen = false;
            foreach (Window window in OwnedWindows().Reverse()) if (window.IsVisible) window.Close();
        }
        void Fail(Exception error)
        {
            failure ??= error; done = true; timer.Stop(); CloseOwned();
        }
        void Queue(Action action) => owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (failure is not null) return;
            try { action(); } catch (Exception error) { Fail(error); }
        }));
        timer.Tick += (_, _) =>
        {
            try
            {
                if (watch.Elapsed.TotalSeconds > 45)
                    throw new TimeoutException("Reduction workflow timed out at stage " + stage + ".");
                Window[] windows = OwnedWindows().Where(window => window.IsVisible).ToArray();
                Window? options = windows.SingleOrDefault(window => window.GetType().FullName == "KillerPDF.ReduceSizeDialog");
                Window? picker = windows.SingleOrDefault(window => window.GetType().FullName == "KillerPDF.Controls.FileDialog");
                foreach (Window message in windows.Where(window => window != options && window != picker))
                {
                    if (!handled.Add(message)) continue;
                    string text = string.Join("\n", Children(message).OfType<TextBlock>()
                        .Where(block => block.IsVisible).Select(block => block.Text));
                    events.Add("Unexpected application message: " + text);
                    throw new InvalidOperationException("Reduction displayed an unexpected modal at stage " + stage + ": " + text);
                }
                if (stage == 0)
                {
                    stage = 1; events.Add("Public OpenFromExternal: " + source);
                    Queue(() => owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!
                        .Invoke(owner, [source]));
                    return;
                }
                if (stage == 1)
                {
                    if (!owner.IsEnabled || Children(owner).OfType<ListBox>()
                        .All(list => list.Name != "PageList" || list.Items.Count != 1)
                        || FindName<TextBlock>(owner, "FileNameLabel").Text != Path.GetFileName(source)) return;
                    stage = 2; events.Add("Opened rendered Save menu");
                    Queue(() => Click(FindName<Button>(owner, "SaveMenuBtn")));
                    return;
                }
                if (stage == 2)
                {
                    string label = (string)Application.Current.FindResource("Str_Menu_ReduceSize");
                    MenuItem? item = PresentationSource.CurrentSources.Cast<PresentationSource>()
                        .Select(presentation => presentation.RootVisual).OfType<DependencyObject>()
                        .SelectMany(root => Children(root).OfType<MenuItem>()).SingleOrDefault(candidate =>
                            candidate.IsVisible && Equals(candidate.Header, label)
                            && ItemsControl.ItemsControlFromItemContainer(candidate) is ContextMenu menu
                            && menu.PlacementTarget is FrameworkElement target && Window.GetWindow(target) == owner);
                    if (item is null) return;
                    if (!item.IsEnabled) throw new InvalidOperationException("Reduce File Size menu item is disabled.");
                    openedMenu = (ContextMenu)ItemsControl.ItemsControlFromItemContainer(item);
                    stage = 3; events.Add("Invoked rendered Reduce File Size menu item");
                    Queue(() => { openedMenu.IsOpen = false; item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); });
                    return;
                }
                if (stage == 3 && options is not null)
                {
                    options.UpdateLayout();
                    CheckBox downsample = Children(options).OfType<CheckBox>().Single(box => box.IsVisible
                        && box.Content is TextBlock label
                        && Equals(label.Text, Application.Current.FindResource("Str_Reduce_Downsample")));
                    if (downsample.IsChecked != true) throw new InvalidOperationException("Default image downsampling is not enabled.");
                    Button save = Children(options).OfType<Button>().Single(button => button.IsDefault && button.IsVisible && button.IsEnabled);
                    stage = 4; events.Add("Accepted real ReduceSizeDialog with default downsampling enabled");
                    Queue(() => Click(save)); return;
                }
                if (stage == 4 && picker is not null)
                {
                    TextBox filename = FindName<TextBox>(picker, "FileNameBox");
                    if (!filename.IsVisible || !filename.IsEnabled || filename.IsReadOnly)
                        throw new InvalidOperationException("Real save picker filename field is unavailable.");
                    filename.Text = output;
                    Button accept = FindName<Button>(picker, "AcceptButton");
                    if (!accept.IsVisible || !accept.IsEnabled) return;
                    stage = 5; events.Add("Accepted real save picker: " + output);
                    Queue(() => Click(accept)); return;
                }
                if (stage == 5 && owner.IsEnabled && options is null && picker is null && File.Exists(output))
                {
                    TextBlock block = FindName<TextBlock>(owner, "StatusText");
                    if (!block.IsVisible || block.ActualWidth <= 0 || block.ActualHeight <= 0)
                        throw new InvalidOperationException("The actual reduction status is not visible.");
                    string expectedStart = "Saved " + Path.GetFileName(output) + ": ";
                    if (!block.Text.StartsWith(expectedStart, StringComparison.Ordinal)) return;
                    status = block.Text;
                    bool notice = HasNotice(owner);
                    if (notice != repaired || status.Contains(Notice, StringComparison.Ordinal) != repaired)
                        throw new InvalidOperationException(repaired ? "Successful repaired reduction omitted the repair notice."
                            : "Successful pristine reduction incorrectly displayed the repair notice.");
                    long size = new FileInfo(output).Length;
                    string after = (size / 1048576.0).ToString(size < 10 * 1048576 ? "0.00" : "0.0", CultureInfo.CurrentCulture) + " MB";
                    string pattern = "^" + Regex.Escape(expectedStart) + @"\d+[.,]\d+ MB to " + Regex.Escape(after)
                        + ", images resampled: 0" + (repaired ? " " + Regex.Escape(Notice) : "") + "$";
                    if (!Regex.IsMatch(status, pattern, RegexOptions.CultureInvariant))
                        throw new InvalidOperationException("Reduction status did not report the expected filename, actual output size and image count: " + status);
                    events.Add("Observed successful status: " + status);
                    done = true; timer.Stop();
                }
            }
            catch (Exception error) { Fail(error); }
        };
        timer.Start();
        try
        {
            while (!done) { Pump(); Thread.Sleep(10); }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            if (status is null || !File.Exists(output) || new FileInfo(output).Length == 0)
                throw new InvalidOperationException("Reduction did not finish with a saved output and observed success status.");
            if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original))
                throw new InvalidOperationException("Reduction changed the source bytes.");
            VerifySavedText(output);
            File.WriteAllText(Path.Combine(folder, name + "-status.txt"), status);
        }
        finally
        {
            timer.Stop(); CloseOwned();
            File.WriteAllLines(Path.Combine(folder, name + "-workflow.log"), events);
        }
    }

    private static void VerifySavedText(string path)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        Type documentType = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!;
        object document = documentType.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!
            .Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(path))])!;
        try
        {
            Type readerType = engine.GetType("KillerPdf.Engine.Documents.PdfPageContentReader", true)!;
            object reader = Activator.CreateInstance(readerType, [document])!;
            object page = readerType.GetMethod("Read", [typeof(int), typeof(CancellationToken)])!
                .Invoke(reader, [0, CancellationToken.None])!;
            if (!Equals(page.GetType().GetProperty("Text")!.GetValue(page), LiveText))
                throw new InvalidOperationException("Reduced output lost known live text.");
        }
        finally { (document as IDisposable)?.Dispose(); }
    }

    private static bool OwnedBy(Window window, Window owner)
    {
        for (Window? parent = window.Owner; parent is not null; parent = parent.Owner)
            if (parent == owner) return true;
        return false;
    }

    private static byte[] Fixture(bool dangling)
    {
        const string content = "BT /F1 12 Tf 20 80 Td (Keep reduced text) Tj ET\n";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R" + (dangling ? " /Optional 999 0 R" : "") + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {content.Length} >>\nstream\n{content}endstream"];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (int i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static T FindName<T>(DependencyObject root, string name) where T : FrameworkElement =>
        Children(root).OfType<T>().Single(element => element.Name == name);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject nested in Children(child)) yield return nested;
        }
    }
}
