using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

internal static class ScrollOverlaySmoke
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static;

    internal static int Run(string appPath, string appXaml, string folder)
    {
        if (!Environment.GetCommandLineArgs().Contains("--startup-smoke"))
            throw new InvalidOperationException("Startup smoke isolation is required.");
        if (Directory.Exists(folder)) throw new IOException("Use a fresh evidence directory.");
        Directory.CreateDirectory(folder);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string path = Path.Combine(Path.GetDirectoryName(appPath)!, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        Assembly assembly = Assembly.LoadFrom(appPath);
        typeof(Application).GetField("_resourceAssembly", Members)!.SetValue(null, assembly);
        assembly.GetType("KillerPDF.Services.AppDataPaths", true)!
            .GetProperty("StartupSmokeRoot", Members)!.SetValue(null, Path.Combine(folder, "data"));
        Type appType = assembly.GetType("KillerPDF.App", true)!;
        var hostType = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ScrollSmokeHost"),
            AssemblyBuilderAccess.Run).DefineDynamicModule("Host")
            .DefineType("IsolatedApplication", TypeAttributes.Public, appType);
        hostType.DefineDefaultConstructor(MethodAttributes.Public);
        MethodBuilder startup = hostType.DefineMethod("OnStartup",
            MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(void), [typeof(StartupEventArgs)]);
        startup.GetILGenerator().Emit(OpCodes.Ret);
        hostType.DefineMethodOverride(startup, appType.GetMethod("OnStartup", Members)!);
        var app = (Application)Activator.CreateInstance(hostType.CreateType()!)!;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        XDocument source = XDocument.Load(appXaml);
        XElement resources = source.Descendants().First(element => element.Name.LocalName == "ResourceDictionary");
        foreach (XAttribute declaration in source.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            resources.SetAttributeValue(declaration.Name, declaration.Value);
        string xaml = resources.ToString();
        foreach (string name in new[] { "KillerPDF", "KillerFamily.Layout" })
            xaml = xaml.Replace("clr-namespace:" + name, "clr-namespace:" + name + ";assembly=KillerPDF", StringComparison.Ordinal);
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml,
            new System.Windows.Markup.ParserContext { BaseUri = new Uri("pack://application:,,,/KillerPDF;component/") });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var report = new List<string>();
        var failures = new List<string>();
        Window? window = null;
        try
        {
            foreach (string service in new[] { "ThemeManager", "LocaleManager", "FilmGrain" })
                assembly.GetType("KillerPDF.Services." + service, true)!
                    .GetMethod("Initialize", Members)!.Invoke(null, null);
            window = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.MainWindow", true)!)!;
            var root = (FrameworkElement)window.Content;
            var overlay = (FrameworkElement)window.FindName("ShortcutOverlay");
            overlay.Visibility = Visibility.Visible;
            var card = (FrameworkElement)window.FindName("ShortcutCardGrid");
            var scroll = Descendants<ScrollViewer>(card).Single();
            var modernClose = Descendants<Button>(card).First(b => b.Width == 28 || b.HorizontalAlignment == HorizontalAlignment.Right);
            Type themeType = assembly.GetType("KillerPDF.Services.Theme", true)!;
            MethodInfo apply = assembly.GetType("KillerPDF.Services.ThemeManager", true)!.GetMethod("Apply", Members)!;
            foreach (string theme in new[] { "Dark", "Light", "SE98" })
            {
                apply.Invoke(null, [Enum.Parse(themeType, theme)]);
                foreach (double height in new[] { 480d, 900d })
                {
                    Settle(root, 1000, height);
                    var bar = Descendants<ScrollBar>(scroll).First(b => b.Orientation == Orientation.Vertical);
                    Rect edge = Bounds(bar, card);
                    report.Add($"{theme} height={height} card={card.ActualWidth}x{card.ActualHeight} bar={edge} overflow={scroll.ScrollableHeight}");
                    Check(Math.Abs(card.ActualWidth - 1 - edge.Right) <= 1, "Scrollbar must touch the card's inner right edge.");
                    if (modernClose.Visibility == Visibility.Visible)
                    {
                        Rect overlap = Rect.Intersect(edge, Bounds(modernClose, card));
                        Check(overlap.IsEmpty || overlap.Width <= 0 || overlap.Height <= 0, "Close and scrollbar must not overlap.");
                    }
                    foreach (string state in new[] { "top", "middle", "bottom" })
                    {
                        scroll.ScrollToVerticalOffset(state == "top" ? 0 : state == "middle" ? scroll.ScrollableHeight / 2 : scroll.ScrollableHeight);
                        Settle(root, 1000, height);
                        var cues = Descendants<Border>(scroll).Where(b => b.Background is LinearGradientBrush g
                            && g.GradientStops.Any(s => s.Color == Color.FromArgb(0x50, 0, 0, 0))).ToArray();
                        Check(cues.Length == 2, "Both overflow cues must exist.");
                        foreach (var cue in cues)
                        {
                            bool expected = cue.VerticalAlignment == VerticalAlignment.Top
                                ? scroll.VerticalOffset > .5 : scroll.ScrollableHeight - scroll.VerticalOffset > .5;
                            Check((cue.Visibility == Visibility.Visible) == expected, "Cue must follow remaining content.");
                            Check(cue.Opacity == (theme == "SE98" ? 0 : 1), "Cue must follow theme opacity.");
                        }
                        Render(card, Path.Combine(folder, $"{theme}-{height}-{state}.png"));
                    }
                }
                card.MaxHeight = double.PositiveInfinity;
                scroll.ScrollToTop();
                Settle(root, 1000, 2200);
                Check(scroll.ScrollableHeight == 0, "A fitting list must not overflow.");
                Check(Descendants<Border>(scroll).Where(b => b.Background is LinearGradientBrush g
                    && g.GradientStops.Any(s => s.Color == Color.FromArgb(0x50, 0, 0, 0)))
                    .All(b => b.Visibility == Visibility.Collapsed), "Fitting content must have no edge cues.");
                card.MaxHeight = 620;
                ((Button)window.FindName("KsViewKeyboardBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Settle(root, 1000, 480);
                Check(((FrameworkElement)window.FindName("ShortcutKeyboardHost")).Visibility == Visibility.Visible,
                    "The keyboard view must remain available.");
                Render(card, Path.Combine(folder, theme + "-keyboard.png"));
                ((Button)window.FindName("KsViewListBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                report.Add(theme + " fitting content and keyboard view checked");
            }
        }
        catch (Exception ex) { failures.Add(ex.ToString()); }
        finally { window?.Close(); app.Shutdown(); }
        report.AddRange(failures.Select(f => "FAIL " + f));
        report.Add(failures.Count == 0 ? "result=ok" : "result=error");
        File.WriteAllLines(Path.Combine(folder, "report.txt"), report);
        return failures.Count == 0 ? 0 : 1;

        void Check(bool success, string message) { if (!success) failures.Add(message); }
    }

    private static void Settle(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        root.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        root.UpdateLayout();
    }

    private static Rect Bounds(FrameworkElement element, Visual relative)
        => element.TransformToAncestor(relative).TransformBounds(new Rect(element.RenderSize));

    private static void Render(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
            context.DrawRectangle(new VisualBrush(element), null, new Rect(element.RenderSize));
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
