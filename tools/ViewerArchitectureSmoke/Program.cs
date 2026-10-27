using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using System.Windows.Markup;

internal static class Program
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is < 2 or > 3) { Console.Error.WriteLine("Usage: ViewerArchitectureSmoke APP_DLL OUTPUT_DIRECTORY [SOURCE_DIRECTORY]"); return 2; }
        string path = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        if (Directory.EnumerateFileSystemEntries(output).Any()) throw new IOException("Use a new empty output directory.");
        _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(120)); Environment.Exit(124); });
        string launcher = Path.Combine(output, "test-launcher.exe");
        File.WriteAllBytes(launcher, []);
        Environment.SetEnvironmentVariable("KILLERPDF_LAUNCHER_PATH", launcher);
        AssemblyLoadContext.Default.Resolving += (_, name) =>
        {
            string dll = Path.Combine(Path.GetDirectoryName(path)!, name.Name + ".dll");
            return File.Exists(dll) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dll) : null;
        };
        Assembly assembly = Assembly.LoadFrom(path);
        typeof(Application).GetField("_resourceAssembly", Flags)!.SetValue(null, assembly);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var root = new DirectoryInfo(args.Length == 3 ? Path.GetFullPath(args[2]) : Path.GetDirectoryName(path)!);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "App.xaml"))) root = root.Parent;
        if (root is null) throw new FileNotFoundException("Pass the source directory containing App.xaml when using a frozen app payload.");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var applicationSource = XDocument.Load(Path.Combine(root.FullName, "App.xaml"));
        var resources = applicationSource.Root!.Element(presentation + "Application.Resources")!.Elements().Single();
        foreach (var element in resources.DescendantsAndSelf())
        {
            if (element.Name.NamespaceName == "clr-namespace:KillerPDF")
                element.Name = XName.Get(element.Name.LocalName, "clr-namespace:KillerPDF;assembly=KillerPDF");
            foreach (var declaration in element.Attributes().Where(a => a.IsNamespaceDeclaration && a.Value == "clr-namespace:KillerPDF"))
                declaration.Value = "clr-namespace:KillerPDF;assembly=KillerPDF";
        }
        var context = new ParserContext { BaseUri = new Uri("pack://application:,,,/KillerPDF;component/") };
        context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
        context.XmlnsDictionary.Add("local", "clr-namespace:KillerPDF;assembly=KillerPDF");
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString(), context);
        var manager = assembly.GetType("KillerPDF.Services.ThemeManager", true)!;
        var themeType = assembly.GetType("KillerPDF.Services.Theme", true)!;
        manager.GetMethod("LoadDict", Flags)!.Invoke(null, [Enum.Parse(themeType, "Dark"), null]);
        int exit = 1;
        // A real viewer in an unshown test-owned window. No app startup or user document is used.
        app.Dispatcher.BeginInvoke(async () =>
        {
            Window? window = null;
            try
            {
                File.WriteAllText(Path.Combine(output, "progress.txt"), "constructing viewer");
                window = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.Shell.MainWindow", true)!)!;
                var viewer = (FrameworkElement)Get(window, "ActiveViewer");
                if (viewer.Parent is Panel originalParent) originalParent.Children.Remove(viewer);
                viewer.Resources.MergedDictionaries.Add(window.Resources);
                window = new Window { Content = viewer, Width = 900, Height = 700,
                    Opacity = 0, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Left = -10000 };
                window.Show();
                string fixture = Path.Combine(output, "navigation.pdf");
                File.WriteAllBytes(fixture, Fixture());
                object working = assembly.GetType("KillerPDF.Services.PdfWorkingDocument", true)!
                    .GetMethod("Open", Flags)!.Invoke(null, [fixture, false])!;
                Set(viewer, "_doc", working);
                Set(viewer, "_currentFile", fixture);
                object state = Get(viewer, "State");
                Set(state, "Mode", Enum.Parse(assembly.GetType("KillerPDF.ViewMode", true)!, "Single"));
                Set(state, "CurrentPage", 0);
                Call(viewer, "EnsureInitialSession");
                for (DependencyObject? node = (Image)Get(state, "PageImage"); node is not null; node = VisualTreeHelper.GetParent(node))
                    if (node is FrameworkElement element) element.Visibility = Visibility.Visible;
                viewer.Width = 900; viewer.Height = 700;
                viewer.Measure(new Size(900, 700)); viewer.Arrange(new Rect(0, 0, 900, 700));
                var scroll = (ScrollViewer)Get(viewer, "PreviewScroller");
                scroll.Visibility = Visibility.Visible;
                ((FrameworkElement)Get(viewer, "DropSurface")).Visibility = Visibility.Collapsed;
                ((FrameworkElement)Get(viewer, "RecentBox")).Visibility = Visibility.Collapsed;
                viewer.UpdateLayout();
                Call(viewer, "WireScrollChanged");
                object queue = Get(viewer, "_viewportRenderQueue");
                using var entered = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var blocker = (Task<int>)queue.GetType().GetMethod("Enqueue", Flags)!.MakeGenericMethod(typeof(int)).Invoke(queue,
                    ["test-blocker", 0, 16L, (Func<CancellationToken, int>)(token =>
                    { entered.Set(); release.Wait(TimeSpan.FromSeconds(15)); token.ThrowIfCancellationRequested(); return 0; }), CancellationToken.None])!;
                if (!entered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Raster worker did not start.");
                Call(viewer, "RenderPage", 0, false);
                var first = (Task)Get(viewer, "PrimaryRenderTask");
                if (first.IsCompleted) throw new Exception("Primary render did not defer while raster worker was busy.");
                if (((Canvas)Get(state, "AnnotationCanvas")).IsHitTestVisible)
                    throw new Exception("Pending navigation accepts annotations on the previous page.");
                bool heartbeat = false;
                await app.Dispatcher.InvokeAsync(() => heartbeat = true, DispatcherPriority.Input);
                if (!heartbeat) throw new Exception("Dispatcher did not respond while raster was pending.");
                Call(viewer, "RenderPage", 1, false);
                Call(viewer, "RenderPage", 2, false);
                release.Set();
                await blocker;
                await ((Task)Get(viewer, "PrimaryRenderTask")).WaitAsync(TimeSpan.FromSeconds(30));
                await first;
                if ((int)Get(state, "RenderedPrimaryPage") != 2) throw new Exception("Superseded page replaced the latest navigation.");
                var measurements = new List<object> { Get(viewer, "LastPrimaryRenderMeasurement") };
                using var revisionEntered = new ManualResetEventSlim();
                using var revisionRelease = new ManualResetEventSlim();
                var revisionBlocker = (Task<int>)queue.GetType().GetMethod("Enqueue", Flags)!.MakeGenericMethod(typeof(int)).Invoke(queue,
                    ["revision-blocker", 0, 16L, (Func<CancellationToken, int>)(token =>
                    { revisionEntered.Set(); revisionRelease.Wait(TimeSpan.FromSeconds(15)); token.ThrowIfCancellationRequested(); return 0; }), CancellationToken.None])!;
                if (!revisionEntered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Revision worker did not start.");
                Call(viewer, "RenderPage", 1, false);
                var obsolete = (Task)Get(viewer, "PrimaryRenderTask");
                object session = Get(viewer, "_active");
                Set(session, "RenderRevision", (long)Get(session, "RenderRevision") + 1);
                revisionRelease.Set();
                await revisionBlocker;
                await obsolete;
                if (((Image)Get(state, "PageImage")).Source is not null)
                    throw new Exception("Obsolete document revision presented pixels.");
                Call(viewer, "RenderPage", 2, false);
                await ((Task)Get(viewer, "PrimaryRenderTask"));
                var image = (Image)Get(state, "PageImage");
                for (DependencyObject? node = image; node is not null; node = VisualTreeHelper.GetParent(node))
                    if (node is FrameworkElement element) element.Visibility = Visibility.Visible;
                await app.Dispatcher.InvokeAsync(() => viewer.UpdateLayout(), DispatcherPriority.ContextIdle);
                if (image.Source is not BitmapSource) throw new Exception("Latest page has no bitmap.");
                Set(state, "ZoomLevel", 3.0);
                ((FrameworkElement)Get(state, "PageContentGrid")).LayoutTransform = new ScaleTransform(3, 3);
                viewer.UpdateLayout();
                await app.Dispatcher.InvokeAsync(() => viewer.UpdateLayout(), DispatcherPriority.ContextIdle);
                Call(viewer, "RefineVisibleRegions");
                await ((Task)Get(viewer, "VisibleRefinementTask")).WaitAsync(TimeSpan.FromSeconds(30));
                if (((System.Collections.IDictionary)Get(viewer, "_visibleDetails")).Count == 0)
                    throw new Exception($"Zoomed viewport has no region refinement. Image={image.ActualWidth}x{image.ActualHeight}; viewport={scroll.ViewportWidth}x{scroll.ViewportHeight}; error={Get(viewer, "LastVisibleRefinementError")}");
                Capture(viewer, Path.Combine(output, "zoom.png"));
                scroll.ScrollToVerticalOffset(600); scroll.ScrollToHorizontalOffset(200);
                viewer.UpdateLayout();
                Call(viewer, "RefineVisibleRegions");
                await ((Task)Get(viewer, "VisibleRefinementTask")).WaitAsync(TimeSpan.FromSeconds(30));
                Capture(viewer, Path.Combine(output, "scroll.png"));
                double scrollX = scroll.HorizontalOffset, scrollY = scroll.VerticalOffset;
                if (scrollX <= 0 || scrollY <= 0) throw new Exception("Zoomed viewport did not scroll.");
                Call(viewer, "CancelRenderWork");
                if (((System.Collections.IDictionary)Get(viewer, "_visibleDetails")).Count != 0)
                    throw new Exception("Canceled viewport retained detail bitmaps.");
                using var fitEntered = new ManualResetEventSlim();
                using var fitRelease = new ManualResetEventSlim();
                var fitBlocker = (Task<int>)queue.GetType().GetMethod("Enqueue", Flags)!.MakeGenericMethod(typeof(int)).Invoke(queue,
                    ["fit-blocker", 0, 16L, (Func<CancellationToken, int>)(token =>
                    { fitEntered.Set(); fitRelease.Wait(TimeSpan.FromSeconds(15)); token.ThrowIfCancellationRequested(); return 0; }), CancellationToken.None])!;
                if (!fitEntered.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fit worker did not start.");
                Call(viewer, "BootstrapDocumentView", 1, true, false, null!, null!);
                await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                fitRelease.Set();
                await fitBlocker;
                await ((Task)Get(viewer, "PrimaryRenderTask"));
                await Until(() => (double)Get(state, "ZoomLevel") < 1, app);
                Capture(viewer, Path.Combine(output, "initial-fit.png"));
                Set(state, "ZoomLevel", 3.0);
                ((FrameworkElement)Get(state, "PageContentGrid")).LayoutTransform = new ScaleTransform(3, 3);
                Set(state, "CurrentPage", 0);
                var modeType = assembly.GetType("KillerPDF.ViewMode", true)!;
                Call(viewer, "ApplyViewMode", Enum.Parse(modeType, "TwoPage"), true);
                await Until(() => ((System.Collections.IDictionary)Get(viewer, "_continuousCanvases")).Contains(1), app);
                await ((Task)Get(viewer, "PrimaryRenderTask"));
                await Until(() => (double)Get(state, "ZoomLevel") < 1, app);
                await app.Dispatcher.InvokeAsync(() => viewer.UpdateLayout(), DispatcherPriority.ContextIdle);
                Capture(viewer, Path.Combine(output, "spread.png"));
                Call(viewer, "ApplyViewMode", Enum.Parse(modeType, "Continuous"), true);
                var panel = (Panel)Get(viewer, "_continuousPanel");
                await Until(() => panel.Children.Count == 3 && panel.Children[0] is Border { Child: Grid g }
                    && g.Children[0] is Image { Source: BitmapSource }, app);
                Set(state, "ZoomLevel", 10.0);
                ((FrameworkElement)Get(state, "PageContentGrid")).LayoutTransform = new ScaleTransform(10, 10);
                scroll.ScrollToVerticalOffset(0); scroll.ScrollToHorizontalOffset(0);
                await app.Dispatcher.InvokeAsync(() => viewer.UpdateLayout(), DispatcherPriority.ContextIdle);
                Call(viewer, "RefineVisibleRegions");
                await ((Task)Get(viewer, "VisibleRefinementTask")).WaitAsync(TimeSpan.FromSeconds(30));
                if (((System.Collections.IDictionary)Get(viewer, "_visibleDetails")).Count == 0)
                    throw new Exception("Continuous viewport has no refinement: " + Get(viewer, "LastVisibleRefinementError"));
                Capture(viewer, Path.Combine(output, "continuous.png"));
                Call(viewer, "CancelRenderWork");
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                { status = "passed", appSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                    dispatcherResponsive = heartbeat, latestPage = 2, zoom = 3, scrollX,
                    scrollY, revisionRejected = true, initialFit = true, spread = true, continuousRefinement = true, measurements }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("PASS dispatcher response, navigation, revision rejection, zoom, scroll, spread, continuous refinement and cancellation.");
                exit = 0;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); Console.Error.WriteLine(ex); }
            finally { Environment.Exit(exit); }
        });
        app.Run();
        return exit;
    }

    private static object Get(object target, string name) => target.GetType().GetProperty(name, Flags)?.GetValue(target)
        ?? target.GetType().GetField(name, Flags)?.GetValue(target) ?? throw new MissingMemberException(name);
    private static void Set(object target, string name, object value)
    {
        var property = target.GetType().GetProperty(name, Flags);
        if (property is not null) property.SetValue(target, value);
        else (target.GetType().GetField(name, Flags) ?? throw new MissingMemberException(name)).SetValue(target, value);
    }
    private static void Call(object target, string method, params object[] args) =>
        target.GetType().GetMethod(method, Flags)!.Invoke(target, args);
    private static async Task Until(Func<bool> ready, Application app)
    {
        var timer = Stopwatch.StartNew();
        while (!ready())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Viewer layout or raster did not settle.");
            await app.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await Task.Delay(20);
        }
    }
    private static void Capture(FrameworkElement element, string path)
    {
        var bitmap = new RenderTargetBitmap(900, 700, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static byte[] Fixture()
    {
        var content = new StringBuilder("%PDF-1.4\n");
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R 5 0 R 7 0 R] /Count 3 >>" };
        for (int p = 0; p < 3; p++)
        {
            string paint = $"{(p == 0 ? "1 0 0" : p == 1 ? "0 1 0" : "0 0 1")} rg 0 0 200 300 re f 0 0 0 RG 1.25 w 0 0 m 200 300 l S\n";
            for (int y = 5; y < 300; y += 10) paint += $"0.15 w 0 {y} m 200 {y} l S\n";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources << >> /Contents {4 + p * 2} 0 R >>");
            objects.Add($"<< /Length {paint.Length} >>\nstream\n{paint}endstream");
        }
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++) { offsets.Add(content.Length); content.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = content.Length; content.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) content.Append($"{offset:D10} 00000 n \n");
        content.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(content.ToString());
    }
}
