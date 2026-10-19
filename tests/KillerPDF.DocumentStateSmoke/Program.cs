using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;

// This process owns every window, PDF, and setting it creates. Reflection is limited to
// startup isolation and service initialization; workflow actions use actual controls.
internal static class Program
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static;
    private static Application App = null!;
    private static Window Owner = null!;
    private static string Folder = "";
    private static Exception? Failure;
    private static System.Threading.Timer? Deadline;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 5 || args[4] != "--startup-smoke")
                throw new ArgumentException("APP_DLL APP_XAML NEW_OUTPUT_DIRECTORY CASE --startup-smoke");
            string appPath = Path.GetFullPath(args[0]);
            Folder = Path.GetFullPath(args[2]);
            if (Directory.Exists(Folder)) throw new IOException("Use a fresh evidence folder.");
            Directory.CreateDirectory(Folder);
            string temporary = Path.Combine(Folder, "temp");
            Directory.CreateDirectory(temporary);
            Environment.SetEnvironmentVariable("TEMP", temporary);
            Environment.SetEnvironmentVariable("TMP", temporary);
            Deadline = new System.Threading.Timer(_ =>
            {
                Console.Error.WriteLine("FAIL isolated process deadline");
                Environment.Exit(1);
            }, null, TimeSpan.FromSeconds(45), Timeout.InfiniteTimeSpan);
            Console.WriteLine("Initializing isolated host");
            Bootstrap(appPath, Path.GetFullPath(args[1]));
            string loadedEngine = typeof(PdfDocument).Assembly.Location;
            string EngineHash(string path) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            string expectedEngineHash = EngineHash(Path.Combine(Path.GetDirectoryName(appPath)!, "KillerPdf.Engine.dll"));
            if (EngineHash(loadedEngine) != expectedEngineHash)
                throw new InvalidOperationException("The runner must use the engine from the tested application build.");
            File.WriteAllText(Path.Combine(Folder, "app-identity.txt"),
                "app=" + appPath + "\nappSha256=" + EngineHash(appPath) + "\nengineSha256=" + expectedEngineHash);
            Console.WriteLine("Host ready");
            if (args[3] == "redaction") Redaction();
            else if (args[3] == "macro-source") MacroSource();
            else if (args[3] == "macro-batch") MacroSource(batch: true);
            else if (args[3] == "macro-rotation") MacroRotation();
            else if (args[3] == "merge-rotation") MacroRotation(merge: true);
            else if (args[3] == "extract-form") ExtractForm();
            else if (args[3] == "extract-rotation") ExtractForm(rotate: true);
            else if (args[3] == "print-rotation") PrintRotation();
            else if (args[3] == "print-switch") PrintSwitch();
            else throw new ArgumentException("Unknown case.");
            Console.WriteLine("PASS " + args[3]);
            File.WriteAllText(Path.Combine(Folder, "result.txt"), "PASS " + args[3]);
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + ex);
            if (Directory.Exists(Folder)) File.WriteAllText(Path.Combine(Folder, "result.txt"), "FAIL " + ex);
            Environment.Exit(1);
        }
        return 1;
    }

    private static void Bootstrap(string appPath, string appXaml)
    {
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(Path.GetDirectoryName(appPath)!, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        Assembly assembly = Assembly.LoadFrom(appPath);
        typeof(Application).GetField("_resourceAssembly", Members)!.SetValue(null, assembly);
        assembly.GetType("KillerPDF.Services.AppDataPaths", true)!
            .GetProperty("StartupSmokeRoot", Members)!.SetValue(null, Path.Combine(Folder, "data"));
        Type appType = assembly.GetType("KillerPDF.App", true)!;
        string smokeRoot = (string)appType.GetProperty("SmokeDataRoot", Members)!.GetValue(null)!;
        File.WriteAllText(Path.Combine(Folder, "owned-smoke-root.txt"), smokeRoot);
        Console.WriteLine("Owned app temporary root: " + smokeRoot);
        var host = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DocumentStateHost"),
            AssemblyBuilderAccess.Run).DefineDynamicModule("Host")
            .DefineType("IsolatedApplication", TypeAttributes.Public, appType);
        host.DefineDefaultConstructor(MethodAttributes.Public);
        var startup = host.DefineMethod("OnStartup", MethodAttributes.Family | MethodAttributes.Virtual
            | MethodAttributes.HideBySig, typeof(void), [typeof(StartupEventArgs)]);
        startup.GetILGenerator().Emit(OpCodes.Ret);
        host.DefineMethodOverride(startup, appType.GetMethod("OnStartup", Members)!);
        App = (Application)Activator.CreateInstance(host.CreateType()!)!;
        App.DispatcherUnhandledException += (_, e) => { Console.Error.WriteLine(e.Exception); Environment.Exit(1); };
        var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(40) };
        deadline.Tick += (_, _) =>
        {
            foreach (Window window in App.Windows)
                Console.Error.WriteLine(window.GetType().Name + ": " + window.Title + " "
                    + string.Join(" | ", Descendants<TextBlock>(window).Select(t => t.Text)));
            Console.Error.WriteLine("FAIL isolated host deadline");
            Environment.Exit(1);
        };
        deadline.Start();
        App.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        XDocument source = XDocument.Load(appXaml);
        XElement resources = source.Descendants().First(e => e.Name.LocalName == "ResourceDictionary");
        foreach (var declaration in source.Root!.Attributes().Where(a => a.IsNamespaceDeclaration))
            resources.SetAttributeValue(declaration.Name, declaration.Value);
        string xaml = resources.ToString();
        foreach (string name in new[] { "KillerPDF", "KillerFamily.Layout" })
            xaml = xaml.Replace("clr-namespace:" + name, "clr-namespace:" + name
                + ";assembly=KillerPDF", StringComparison.Ordinal);
        App.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml,
            new System.Windows.Markup.ParserContext { BaseUri = new Uri("pack://application:,,,/KillerPDF;component/") });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        foreach (string service in new[] { "ThemeManager", "LocaleManager", "FilmGrain" })
            assembly.GetType("KillerPDF.Services." + service, true)!
                .GetMethod("Initialize", Members)!.Invoke(null, null);
        Owner = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.MainWindow", true)!)!;
        Owner.WindowStartupLocation = WindowStartupLocation.Manual;
        Owner.Left = -10000; Owner.Top = -10000; Owner.ShowInTaskbar = false;
        Owner.ShowActivated = false; Owner.Width = 1200; Owner.Height = 800;
        Owner.Show();
        Console.WriteLine("Window shown");
        Pump(300);
    }

    private static void Redaction()
    {
        string macros = Path.Combine(Folder, "data", "macros");
        Directory.CreateDirectory(macros);
        File.WriteAllText(Path.Combine(macros, "redact.json"), new PdfMacro("Redact test",
            [new PdfMacroStep(PdfMacroOperation.Redact), new PdfMacroStep(PdfMacroOperation.Save)]).ToJson());
        string left = Fixture("left", "PUBLIC", 20, 100, 10, 80, 120, 120);
        string right = Fixture("right", "SECRET", 150, 300, 140, 280, 250, 320);
        Open(left);
        Console.WriteLine("Left fixture opened");
        Click((Button)Owner.FindName("SplitPaneRailBtn"));
        Pump(350);
        var rightViewer = (FrameworkElement)Owner.FindName("ViewerB");
        rightViewer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount,
            MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        Open(right);
        Console.WriteLine("Right fixture opened");
        RunMacro(Path.Combine(Folder, "result.pdf"));
        PdfDocument document = PdfDocument.Open(File.ReadAllBytes(Path.Combine(Folder, "result.pdf")));
        string text = string.Join(" ", new PdfPageContentReader(document).Read(0).GetWords().Select(w => w.Text));
        Console.WriteLine("Extracted macro output: " + text);
        if (text.Contains("SECRET", StringComparison.Ordinal))
            throw new InvalidOperationException("BF-F001: right-pane SECRET remains extractable after Redact macro.");
        if (!text.Contains("KEPT-RIGHT", StringComparison.Ordinal))
            throw new InvalidOperationException("The macro must preserve right-document content outside its redaction mark.");
    }

    private static void MacroSource(bool batch = false)
    {
        Open(Fixture("source", "SOURCE", 20, 100, 10, 80, 120, 120));
        Exception? assertion = null;
        bool sourceChosen = false;
        bool batchPicker = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            Window? dialog = App.Windows.Cast<Window>().LastOrDefault(w => !ReferenceEquals(w, Owner) && w.IsVisible);
            if (dialog is null) return;
            if (batch && sourceChosen && dialog.GetType().Name == "FileDialog")
            { timer.Stop(); batchPicker = true; dialog.Close(); return; }
            if (sourceChosen || dialog.GetType().Name != "MacroDialog") return;
            sourceChosen = true;
            timer.Stop();
            var options = Descendants<RadioButton>(dialog).ToArray();
            if (options.Length != 2 || options.Count(o => o.IsChecked == true) != 1
                || !options.Any(o => Text(o).Contains("current", StringComparison.OrdinalIgnoreCase) && o.IsChecked == true))
                assertion = new InvalidOperationException("Macro source choice must be in the macro window, defaulting to the current document.");
            if (assertion is null)
            {
                if (batch) options.Single(o => o.IsChecked != true).IsChecked = true;
                Capture(dialog, batch ? "macro-batch.png" : "macro-current.png");
                if (batch)
                { timer.Start(); QueueClick(Descendants<Button>(dialog).Single(b => b.IsDefault)); return; }
            }
            dialog.Close();
        };
        timer.Start();
        OpenMacroWindow();
        if (assertion is not null) throw assertion;
        if (batch && !batchPicker) throw new InvalidOperationException("Batch selection did not open the file picker directly.");
    }

    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(Folder, name));
        encoder.Save(file);
    }

    private static void MacroRotation(bool merge = false)
    {
        string macros = Path.Combine(Folder, "data", "macros");
        Directory.CreateDirectory(macros);
        PdfMacro macro = merge
            ? new PdfMacro("Merge test", [new PdfDataMergeProfile("Names", [new("Name", "Name")],
                "{{Name}}.pdf").ToMacroStep(), new PdfMacroStep(PdfMacroOperation.Save)])
            : new PdfMacro("Save test", [new PdfMacroStep(PdfMacroOperation.Save)]);
        File.WriteAllText(Path.Combine(macros, "save.json"), macro.ToJson());
        string path = Path.Combine(Folder, "rotate.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(300, 500)
            .AddTextField(0, "Name", 20, 200, 150, 25, "Original").Build());
        Open(path);
        Rotate();
        string output = Path.Combine(Folder, merge ? "record.pdf" : "result.pdf");
        string? records = merge ? Path.Combine(Folder, "records.csv") : null;
        if (records is not null) File.WriteAllText(records, "Name\nrecord\n");
        RunMacro(output, redact: false, mergeData: records);
        int rotation = PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(output)))[0].Rotation;
        if (rotation != 90) throw new InvalidOperationException("BF-F003: macro output rotation was " + rotation + ", expected 90.");
        if (merge && PdfFormWidgetReader.ReadPage(PdfDocument.Open(File.ReadAllBytes(output)), 0)
                .Single(w => w.FieldName == "Name").Value != "record")
            throw new InvalidOperationException("The data-merge macro did not apply the selected record.");
    }

    private static void Rotate()
    {
        var pages = (ListBox)Owner.FindName("PageList");
        pages.SelectedIndex = 0;
        var thumbnail = (ListBoxItem)pages.ItemContainerGenerator.ContainerFromIndex(0);
        thumbnail.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
            { RoutedEvent = Mouse.MouseUpEvent });
        Pump(80);
        string label = (string)Owner.FindResource("Str_Ctx_RotateCWShort");
        MenuItem clockwise = Menus().SelectMany(m => Items(m.Items)).First(m => Header(m) == label);
        clockwise.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        foreach (ContextMenu menu in Menus()) menu.IsOpen = false;
        Pump(250);
    }

    private static void ExtractForm(bool rotate = false)
    {
        string path = Path.Combine(Folder, "form.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(300, 500)
            .AddTextField(0, "customer", 20, 200, 150, 25, "Original").Build());
        Open(path);
        if (rotate) Rotate();
        var input = Descendants<TextBox>((DependencyObject)Owner.FindName("Viewer")).Single(t => t.Text == "Original");
        input.Text = "Updated";
        ((ListBox)Owner.FindName("PageList")).SelectedIndex = 0;
        string output = Path.Combine(Folder, "extracted.pdf");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            Window? picker = App.Windows.Cast<Window>().FirstOrDefault(w => w.GetType().Name == "FileDialog");
            if (picker is null) return;
            timer.Stop();
            ((TextBox)picker.FindName("FileNameBox")).Text = output;
            QueueClick((Button)picker.FindName("AcceptButton"));
        };
        timer.Start();
        Click((Button)Owner.FindName("ExtractPagesBtn"));
        string value = PdfFormWidgetReader.ReadPage(PdfDocument.Open(File.ReadAllBytes(output)), 0)
            .Single(w => w.FieldName == "customer").Value;
        if (value != "Updated") throw new InvalidOperationException("BF-F004: extraction returned " + value + ", expected Updated.");
        if (rotate && PdfPageInformation.Read(PdfDocument.Open(File.ReadAllBytes(output)))[0].Rotation != 90)
            throw new InvalidOperationException("Extraction must apply the pending 90-degree rotation exactly once.");
    }

    private static void PrintRotation()
    {
        string path = Path.Combine(Folder, "print.pdf");
        File.WriteAllBytes(path, new PdfDocumentBuilder().AddBlankPage(300, 500).Build());
        Open(path);
        Rotate();
        Exception? assertion = null;
        bool rendered = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            Window? preview = App.Windows.Cast<Window>().FirstOrDefault(w => w.GetType().Name == "PrintPreviewWindow");
            if (preview is null) return;
            var image = Descendants<Image>(preview).FirstOrDefault(i => i.Source is System.Windows.Media.Imaging.BitmapSource b && b.PixelWidth > 100);
            if (image?.Source is not System.Windows.Media.Imaging.BitmapSource bitmap) return;
            rendered = true; timer.Stop();
            Console.WriteLine("Print preview raster: " + bitmap.PixelWidth + "x" + bitmap.PixelHeight);
            if (bitmap.PixelWidth <= bitmap.PixelHeight)
                assertion = new InvalidOperationException("BF-F002: rotated page prints in its original portrait orientation.");
            preview.Close();
        };
        timer.Start();
        Click((Button)Owner.FindName("PrintBtn"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!rendered && watch.Elapsed < TimeSpan.FromSeconds(25)) Pump(100);
        if (!rendered) throw new TimeoutException("Print preview raster did not load.");
        if (assertion is not null) throw assertion;
    }

    private static void PrintSwitch()
    {
        string original = Path.Combine(Folder, "source.pdf");
        var builder = new PdfDocumentBuilder().AddBlankPage(300, 500);
        for (int i = 0; i < 350; i++)
            builder.AddTextField(0, "field" + i, 20, 100, 150, 25, "Original" + i);
        File.WriteAllBytes(original, builder.Build());
        string other = Path.Combine(Folder, "other.pdf");
        File.WriteAllBytes(other, new PdfDocumentBuilder().AddBlankPage(500, 300)
            .AddBlankPage(500, 300).AddBlankPage(500, 300).Build());
        Open(original);
        Descendants<TextBox>((DependencyObject)Owner.FindName("Viewer"))
            .First(t => t.Text.StartsWith("Original", StringComparison.Ordinal)).Text = "Updated";
        bool switched = false;
        bool previewShown = false;
        bool cancellationReported = false;
        var status = (TextBlock)Owner.FindName("StatusText");
        string canceled = (string)Owner.FindResource("Str_St_Canceled");
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .AddValueChanged(status, (_, _) => { if (status.Text == canceled) cancellationReported = true; });
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            Window? preview = App.Windows.Cast<Window>().FirstOrDefault(w => w.GetType().Name == "PrintPreviewWindow");
            if (preview is null) return;
            previewShown = true; timer.Stop(); preview.Close();
        };
        timer.Start();
        Click((Button)Owner.FindName("PrintBtn"));
        Owner.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { Open(other); switched = true; }));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!previewShown && !cancellationReported && watch.Elapsed < TimeSpan.FromSeconds(12)) Pump(100);
        timer.Stop();
        if (!switched) throw new InvalidOperationException("The timed document switch did not execute.");
        if (previewShown) throw new InvalidOperationException("BF-F006: print preview opened after the source document changed during preparation.");
        if (!cancellationReported) throw new TimeoutException("Print preparation did not report cancellation after the document switch.");
    }

    private static string Fixture(string name, string text, double x, double y,
        double left, double bottom, double right, double top)
    {
        string path = Path.Combine(Folder, name + ".pdf");
        var stream = new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .MoveText(x, y).ShowLatin1Text(text).EndText().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .MoveText(20, 450).ShowLatin1Text("KEPT-" + name.ToUpperInvariant()).EndText();
        var quad = new PdfTextQuad(new(left, top), new(right, top), new(left, bottom), new(right, bottom));
        File.WriteAllBytes(path, new PdfDocumentBuilder().AddPage(300, 500, stream)
            .AddRedactionMark(0, [quad]).Build());
        return path;
    }

    private static void Open(string path)
    {
        Owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(Owner, [path]);
        Pump(500);
    }

    private static void RunMacro(string destination, bool redact = true, string? mergeData = null)
    {
        int stage = 0;
        string lastDialog = "";
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) =>
        {
            try
            {
                Window? dialog = App.Windows.Cast<Window>().LastOrDefault(w => !ReferenceEquals(w, Owner) && w.IsVisible);
                if (dialog is null) return;
                string type = dialog.GetType().Name;
                if (lastDialog != type + stage)
                { Console.WriteLine("Macro dialog: " + type + ", stage " + stage); lastDialog = type + stage; }
                if (type == "MacroDialog" && stage == 0)
                {
                    stage = Descendants<RadioButton>(dialog).Any(b => b.Content is string text && text.Contains("current", StringComparison.OrdinalIgnoreCase)) ? 2 : 1;
                    QueueClick(Descendants<Button>(dialog).Single(b => b.IsDefault));
                }
                else if (type == "DocumentCommandDialog" && stage == 1)
                {
                    stage++;
                    QueueClick(Descendants<Button>(dialog).First(b => Text(b).Contains("current", StringComparison.OrdinalIgnoreCase)));
                }
                else if (type == "FileDialog" && stage == 2)
                {
                    stage++;
                    ((TextBox)dialog.FindName("FileNameBox")).Text = mergeData ?? destination;
                    QueueClick((Button)dialog.FindName("AcceptButton"));
                }
                else if (mergeData is not null && type == "FileDialog" && stage == 3
                    && dialog.Title.Contains((string)Owner.FindResource("Str_DataMerge_SelectOutput"), StringComparison.Ordinal))
                {
                    stage++;
                    ((TextBox)dialog.FindName("FileNameBox")).Text = destination;
                    QueueClick((Button)dialog.FindName("AcceptButton"));
                }
                else if (mergeData is not null && type == "ImportReviewDialog" && stage == 4)
                { stage++; QueueClick(Descendants<Button>(dialog).Single(b => b.IsDefault)); }
                else if (stage == 3 && Descendants<Button>(dialog).Any(b => Text(b) == "Yes"))
                { stage++; QueueClick(Descendants<Button>(dialog).First(b => Text(b) == "Yes")); }
                else if (redact && stage == 4 && Descendants<TextBox>(dialog).Any(t => t.AcceptsReturn))
                { stage++; QueueClick(Descendants<Button>(dialog).First(b => Text(b) == "OK")); }
                else if (stage >= (redact ? 5 : 4) && type == "DocumentReportDialog")
                { timer.Stop(); dialog.Close(); }
            }
            catch (Exception ex) { Console.Error.WriteLine("Macro stage " + stage + ": " + ex); Failure = ex; timer.Stop(); foreach (Window w in App.Windows.Cast<Window>().ToArray()) if (!ReferenceEquals(w, Owner)) w.Close(); }
        };
        timer.Start();
        OpenMacroWindow();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!File.Exists(destination) && Failure is null && watch.Elapsed < TimeSpan.FromSeconds(25)) Pump(100);
        timer.Stop();
        if (Failure is not null) throw Failure;
        if (!File.Exists(destination)) throw new TimeoutException("Macro output missing; dialog stage " + stage);
    }

    private static void OpenMacroWindow()
    {
        Click((Button)Owner.FindName("SaveMenuBtn"));
        MenuItem macros = Menus().SelectMany(m => Items(m.Items)).Single(m => Header(m).Contains("Macros", StringComparison.OrdinalIgnoreCase));
        macros.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static string Header(MenuItem item) => item.Header?.ToString() ?? "";
    private static string Text(ContentControl b) => b.Content is string s ? s : string.Join("", Descendants<TextBlock>(b).Select(t => t.Text));
    private static IEnumerable<MenuItem> Items(ItemCollection items)
    {
        foreach (MenuItem item in items.OfType<MenuItem>())
        { yield return item; foreach (var nested in Items(item.Items)) yield return nested; }
    }
    private static IEnumerable<ContextMenu> Menus() => PresentationSource.CurrentSources.Cast<PresentationSource>()
        .Where(s => s.RootVisual is not null).SelectMany(s => Descendants<ContextMenu>(s.RootVisual));
    private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void QueueClick(Button b) => b.Dispatcher.BeginInvoke(new Action(() => Click(b)));
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { DependencyObject child = VisualTreeHelper.GetChild(parent, i); if (child is T t) yield return t; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
}
