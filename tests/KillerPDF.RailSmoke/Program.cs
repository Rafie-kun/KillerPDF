using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

// Each process owns its settings and window. Reflection only initializes the isolated host.
internal static class Program
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static;
    private static string Folder = "";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if ((args.Length != 8 && args.Length != 10) || args[^1] != "--startup-smoke")
                throw new ArgumentException("APP_DLL APP_XAML NEW_OUTPUT_DIRECTORY SIDE SCALE WIDTH THEME [LOCALE open|collapsed] --startup-smoke");
            string appPath = Path.GetFullPath(args[0]);
            Folder = Path.GetFullPath(args[2]);
            if (Directory.Exists(Folder)) throw new IOException("Use a fresh output directory.");
            Directory.CreateDirectory(Path.Combine(Folder, "data"));
            string temporary = Path.Combine(Folder, "temp");
            Directory.CreateDirectory(temporary);
            Environment.SetEnvironmentVariable("TEMP", temporary);
            Environment.SetEnvironmentVariable("TMP", temporary);
            File.WriteAllText(Path.Combine(Folder, "data", "settings.json"), JsonSerializer.Serialize(
                new Dictionary<string, string> { ["SidebarSide"] = args[3], ["AppScale"] = args[4],
                    ["Theme"] = args[6], ["Locale"] = args.Length == 10 ? args[7] : "EnUS" }));
            Application app = Bootstrap(appPath, Path.GetFullPath(args[1]));
            Assembly assembly = Application.ResourceAssembly;
            var window = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.Shell.MainWindow", true)!)!;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -10000; window.Top = -10000; window.ShowInTaskbar = false;
            window.ShowActivated = false; window.Width = double.Parse(args[5], CultureInfo.InvariantCulture);
            window.Height = 800; window.Show(); Pump(500);
            string fixture = CreateFixture();
            window.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!
                .Invoke(window, [fixture]);
            Pump(550);
            var header = (FrameworkElement)window.FindName("SidebarHeaderStack");
            bool open = args.Length == 10 && args[8] == "open";
            if ((header.Visibility == Visibility.Visible) != open)
            {
                ((Button)window.FindName("SidebarToggleBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(350);
            }
            window.UpdateLayout(); Pump(200);
            var rail = (FrameworkElement)window.FindName("SidebarToggleStrip");
            var outer = (FrameworkElement)window.FindName("SidebarOuterGrid");
            if (Math.Abs(outer.LayoutTransform.Value.M11 - double.Parse(args[4], CultureInfo.InvariantCulture)) > 0.001
                || ((args[3] is "Top" or "Bottom") != (rail.ActualWidth > rail.ActualHeight)))
                throw new InvalidOperationException("Requested startup orientation or app scale was not applied.");
            var stack = (StackPanel)window.FindName("SidebarRailStack");
            var buttons = stack.Children.OfType<Button>().ToArray();
            var before = buttons.Select(button => Describe(button, rail)).ToArray();
            Save(window, "initial.png");
            var scroll = Descendants<ScrollViewer>(rail).FirstOrDefault(view => IsAncestor(view, stack));
            bool needsOverflow = before.Any(item => item.Outside);
            var next = Descendants<Button>(rail).FirstOrDefault(button => button.Name == "RailOverflowNext");
            var previous = Descendants<Button>(rail).FirstOrDefault(button => button.Name == "RailOverflowPrevious");
            File.WriteAllText(Path.Combine(Folder, "initial.json"), JsonSerializer.Serialize(
                new { Side = args[3], Scale = args[4], Width = window.ActualWidth,
                    Rail = new[] { rail.ActualWidth, rail.ActualHeight }, Buttons = before },
                new JsonSerializerOptions { WriteIndented = true }));
            if (!open && before.All(item => item.Outside))
                throw new InvalidOperationException("Collapsed rail initially hides every original command.");
            if (needsOverflow && (scroll is null || next?.IsVisible != true || previous?.IsVisible != true))
                throw new InvalidOperationException("BF-L001: rail commands are clipped without visible overflow navigation: "
                    + string.Join(", ", before.Where(item => item.Outside).Select(item => item.Name)));
            if (scroll is not null && scroll.ScrollableWidth > 0.5)
            {
                if (next?.IsVisible != true || previous?.IsVisible != true)
                    throw new InvalidOperationException("Overflow controls must be visible while commands need scrolling.");
                double offset = scroll.HorizontalOffset;
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(100);
                if (scroll.HorizontalOffset <= offset)
                    throw new InvalidOperationException("Next overflow button did not reveal more commands.");
                foreach (Button button in buttons)
                {
                    if (button.IsEnabled && !button.Focus())
                        throw new InvalidOperationException("Cannot focus enabled rail command " + button.Name);
                    if (!button.IsEnabled) button.BringIntoView();
                    Pump(40);
                    Rect bounds = button.TransformToAncestor(scroll).TransformBounds(new Rect(button.RenderSize));
                    if (bounds.Left < -0.5 || bounds.Right > scroll.ActualWidth + 0.5)
                        throw new InvalidOperationException("Cannot reveal original rail command " + button.Name);
                }
                Save(window, "last-command.png");
                double previousOffset = scroll.HorizontalOffset;
                previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(100);
                if (previousOffset > 0.5 && scroll.HorizontalOffset >= previousOffset - 0.5)
                    throw new InvalidOperationException("Previous overflow button did not reveal earlier commands.");
            }
            if (open)
            {
                Reveal((Control)window.FindName("PageJumpBox"), rail);
                var outline = (Button)window.FindName("SidebarOutlinesTab");
                outline.BringIntoView(); Pump(40);
                outline.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(250);
                Reveal((Control)window.FindName("OutlineExpandCollapseAllButton"), rail);
                Save(window, "outline-actions.png");
                if (args[3] == "Top" && args[4] == "2.5" && args[6] == "Dark")
                {
                    window.Height = 600; Pump(250);
                    Reveal((Control)window.FindName("OutlineExpandCollapseAllButton"), rail);
                    Save(window, "short-window.png");
                    window.Height = 1200; Pump(250);
                    Reveal((Control)window.FindName("OutlineExpandCollapseAllButton"), rail);
                    window.Height = 800; Pump(250);
                    var logo = (UIElement)window.FindName("LogoBar");
                    foreach (int direction in new[] { -120, 120 })
                    {
                        for (int step = 0; step < 90; step++)
                            logo.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, direction)
                                { RoutedEvent = Mouse.MouseWheelEvent });
                        Pump(300);
                        Reveal((Control)window.FindName("OutlineExpandCollapseAllButton"), rail);
                    }
                    Console.WriteLine("PASS real resize and logo-wheel scale changes keep header on screen");
                }
                var comments = (Button)window.FindName("SidebarCommentsTab");
                comments.BringIntoView(); Pump(40);
                comments.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(250);
                Reveal((Control)window.FindName("SidebarPanelEditButton"), rail);
                Save(window, "panel-edit.png");
            }
            foreach (string name in new[] { "RailLangBtn", "RailThemeBtn" })
            {
                var button = (Button)window.FindName(name);
                button.BringIntoView(); Pump(50);
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(170);
                var menu = (ContextMenu)window.FindName(name == "RailLangBtn" ? "LangFlyout" : "ThemeFlyout");
                if (!menu.IsOpen || !ReferenceEquals(menu.PlacementTarget, button))
                    throw new InvalidOperationException("Flyout lost its original button anchor: " + name);
                menu.IsOpen = false; Pump(50);
            }
            if (!buttons.SequenceEqual(stack.Children.OfType<Button>()))
                throw new InvalidOperationException("Overflow must retain original button instances and order.");
            if (args[4] == "2.5" && args[6] == "Dark")
            {
                ((Button)window.FindName("FullScreenRailBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(700);
                var content = (Grid)window.FindName("MainContentGrid");
                int row = args[3] == "Top" ? 0 : 1;
                double extent = content.RowDefinitions[row].ActualHeight;
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window),
                    Environment.TickCount, Key.F11) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Pump(700);
                if (extent > 0.5) throw new InvalidOperationException("Fullscreen retained sidebar track height " + extent);
                if (!rail.IsVisible) throw new InvalidOperationException("Fullscreen exit did not restore the rail.");
                Console.WriteLine("PASS fullscreen removes and restores the strip track");
            }
            if (args[3] == "Top" && args[4] == "1" && args[5] == "1000")
            {
                foreach (string side in new[] { "Left", "Bottom", "Right", "Top" })
                {
                    rail.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                        { RoutedEvent = UIElement.MouseRightButtonUpEvent });
                    Pump(80);
                    ContextMenu menu = PresentationSource.CurrentSources.Cast<PresentationSource>()
                        .Where(source => source.RootVisual is not null)
                        .SelectMany(source => Descendants<ContextMenu>(source.RootVisual))
                        .Single(candidate => candidate.IsOpen);
                    string caption = (string)window.FindResource("Str_Sidebar_" + side);
                    MenuItem item = menu.Items.OfType<MenuItem>().Single(candidate => Equals(candidate.Header, caption));
                    item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                    menu.IsOpen = false; Pump(230);
                    if (!buttons.SequenceEqual(stack.Children.OfType<Button>())
                        || (side is "Top" or "Bottom") != (stack.Orientation == Orientation.Horizontal))
                        throw new InvalidOperationException("Side transition changed original command instances or orientation.");
                    Console.WriteLine("PASS context-menu transition to " + side);
                }
            }
            File.WriteAllText(Path.Combine(Folder, "result.txt"), "PASS rail commands are reachable");
            Console.WriteLine("PASS " + args[3] + " scale=" + args[4] + " width=" + args[5]);
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

    private static void Reveal(Control control, FrameworkElement rail)
    {
        Console.WriteLine(control.Name + ": " + control.Visibility + " visible=" + control.IsVisible
            + " enabled=" + control.IsEnabled + " focusable=" + control.Focusable + " size=" + control.RenderSize);
        if (!control.IsVisible || !control.IsEnabled || control.ActualWidth < 1 || control.ActualHeight < 1)
            throw new InvalidOperationException("Cannot focus populated header action " + control.Name);
        if (control.Focusable && !control.Focus())
            throw new InvalidOperationException("Cannot focus populated header action " + control.Name);
        if (!control.Focusable) control.BringIntoView();
        Pump(60);
        Window window = Window.GetWindow(control);
        Rect visibleBounds = control.TransformToAncestor(window).TransformBounds(new Rect(control.RenderSize));
        Console.WriteLine(control.Name + " in window: " + visibleBounds);
        if (visibleBounds.Left < -0.5 || visibleBounds.Right > window.ActualWidth + 0.5
            || visibleBounds.Top < -0.5 || visibleBounds.Bottom > window.ActualHeight + 0.5)
            throw new InvalidOperationException("Populated header action lies outside the window: " + control.Name);
        for (DependencyObject? current = VisualTreeHelper.GetParent(control); current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement ancestor && (ancestor.ClipToBounds || ReferenceEquals(ancestor, rail)))
            {
                Rect bounds = control.TransformToAncestor(ancestor).TransformBounds(new Rect(control.RenderSize));
                Console.WriteLine(control.Name + " in " + ancestor.Name + ": " + bounds + " / " + ancestor.ActualWidth);
                if (bounds.Left < -0.5 || bounds.Right > ancestor.ActualWidth + 0.5)
                    throw new InvalidOperationException("Populated header action is clipped: " + control.Name);
            }
            if (ReferenceEquals(current, rail)) break;
        }
    }

    private static string CreateFixture()
    {
        const string contents = "BT /F1 12 Tf 72 720 Td (RAIL FIXTURE) Tj ET";
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {contents.Length} >>\nstream\n{contents}\nendstream",
            "<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>",
            "<< /Title (Rail chapter) /Parent 6 0 R /Dest [3 0 R /Fit] /First 8 0 R /Last 8 0 R /Count 1 >>",
            "<< /Title (Rail bookmark) /Parent 7 0 R /Dest [3 0 R /Fit] >>"
        ];
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (string item in objects)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{offsets.Count} 0 obj\n{item}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        string path = Path.Combine(Folder, "fixture.pdf");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(pdf.ToString()));
        return path;
    }

    private static Application Bootstrap(string appPath, string appXaml)
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
        var host = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("RailSmokeHost"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("Host").DefineType("IsolatedApplication", TypeAttributes.Public, appType);
        host.DefineDefaultConstructor(MethodAttributes.Public);
        var startup = host.DefineMethod("OnStartup", MethodAttributes.Family | MethodAttributes.Virtual
            | MethodAttributes.HideBySig, typeof(void), [typeof(StartupEventArgs)]);
        startup.GetILGenerator().Emit(OpCodes.Ret);
        host.DefineMethodOverride(startup, appType.GetMethod("OnStartup", Members)!);
        var app = (Application)Activator.CreateInstance(host.CreateType()!)!;
        var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(Folder, "data", "settings.json")))!;
        foreach (var setting in settings)
            appType.GetMethod("SetSetting", Members)!.Invoke(null, [setting.Key, setting.Value]);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.DispatcherUnhandledException += (_, e) => { Console.Error.WriteLine(e.Exception); Environment.Exit(1); };
        var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(35) };
        deadline.Tick += (_, _) => { Console.Error.WriteLine("FAIL rail smoke deadline"); Environment.Exit(1); };
        deadline.Start();
        XDocument source = XDocument.Load(appXaml);
        XElement resources = source.Descendants().First(element => element.Name.LocalName == "ResourceDictionary");
        foreach (var declaration in source.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            resources.SetAttributeValue(declaration.Name, declaration.Value);
        string xaml = resources.ToString();
        foreach (string name in new[] { "KillerPDF", "KillerFamily.Layout" })
            xaml = xaml.Replace("clr-namespace:" + name, "clr-namespace:" + name + ";assembly=KillerPDF", StringComparison.Ordinal);
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml,
            new System.Windows.Markup.ParserContext { BaseUri = new Uri("pack://application:,,,/KillerPDF;component/") });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        foreach (string service in new[] { "ThemeManager", "LocaleManager", "FilmGrain" })
            assembly.GetType("KillerPDF.Services." + service, true)!.GetMethod("Initialize", Members)!.Invoke(null, null);
        string locale = assembly.GetType("KillerPDF.Services.LocaleManager", true)!
            .GetProperty("Current", Members)!.GetValue(null)!.ToString()!;
        if (locale != settings["Locale"])
            throw new InvalidOperationException("Requested locale was not applied: " + locale);
        Console.WriteLine("Locale=" + locale + ", TEMP=" + Path.GetTempPath());
        string smokeRoot = (string)appType.GetProperty("SmokeDataRoot", Members)!.GetValue(null)!;
        if (!smokeRoot.StartsWith(Path.Combine(Folder, "temp") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Smoke data escaped the owned scratch folder.");
        Console.WriteLine("SmokeDataRoot=" + smokeRoot);
        return app;
    }

    private static bool IsAncestor(DependencyObject ancestor, DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private sealed record ButtonBounds(string Name, bool Outside, double Left, double Right);

    private static ButtonBounds Describe(Button button, FrameworkElement rail)
    {
        Rect bounds = button.TransformToAncestor(rail).TransformBounds(new Rect(button.RenderSize));
        return new(button.Name, bounds.Left < -0.5 || bounds.Right > rail.ActualWidth + 0.5
            || bounds.Top < -0.5 || bounds.Bottom > rail.ActualHeight + 0.5, bounds.Left, bounds.Right);
    }

    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private static void Save(FrameworkElement element, string name)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(Folder, name)); encoder.Save(stream);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T value) yield return value;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
