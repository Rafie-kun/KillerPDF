using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

internal static class CertificateCatalogSmoke
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
        string data = Path.Combine(folder, "data");
        assembly.GetType("KillerPDF.Services.AppDataPaths", true)!
            .GetProperty("StartupSmokeRoot", Members)!.SetValue(null, data);
        Type appType = assembly.GetType("KillerPDF.App", true)!;
        var host = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("CertificateSmokeHost"),
            AssemblyBuilderAccess.Run).DefineDynamicModule("Host")
            .DefineType("IsolatedApplication", TypeAttributes.Public, appType);
        host.DefineDefaultConstructor(MethodAttributes.Public);
        MethodBuilder startup = host.DefineMethod("OnStartup",
            MethodAttributes.Family | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            typeof(void), [typeof(StartupEventArgs)]);
        startup.GetILGenerator().Emit(OpCodes.Ret);
        host.DefineMethodOverride(startup, appType.GetMethod("OnStartup", Members)!);
        var app = (Application)Activator.CreateInstance(host.CreateType()!)!;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        XDocument source = XDocument.Load(appXaml);
        XElement resources = source.Descendants().First(element => element.Name.LocalName == "ResourceDictionary");
        foreach (XAttribute declaration in source.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
            resources.SetAttributeValue(declaration.Name, declaration.Value);
        string xaml = resources.ToString();
        // Whole attribute values: "clr-namespace:KillerPDF" is also the start of KillerPDF.Controls.
        foreach (string name in new[] { "KillerPDF", "KillerPDF.Controls", "KillerFamily.Layout" })
            xaml = xaml.Replace("\"clr-namespace:" + name + "\"", "\"clr-namespace:" + name + ";assembly=KillerPDF\"", StringComparison.Ordinal);
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse(xaml,
            new System.Windows.Markup.ParserContext { BaseUri = new Uri("pack://application:,,,/KillerPDF;component/") });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        Window? owner = null;
        try
        {
            // App startup order: the kit theme hooks (settings, KillerPDF's own keys) before the services.
            assembly.GetType("KillerPDF.Services.AppTheme", true)!.GetMethod("Configure",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.Invoke(null, null);
            foreach (string service in new[] { "ThemeManager", "LocaleManager", "FilmGrain" })
                assembly.GetType("KillerPDF.Services." + service, true)!
                    .GetMethod("Initialize", Members)!.Invoke(null, null);
            owner = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.Shell.MainWindow", true)!)!;
            owner.WindowStartupLocation = WindowStartupLocation.Manual;
            owner.Left = -10000; owner.Top = -10000; owner.ShowInTaskbar = false;
            owner.ShowActivated = false; owner.Show();
            Type catalogType = assembly.GetType("KillerPDF.Services.Signing.CertificateCatalog", true)!;
            object catalog = Activator.CreateInstance(catalogType, Members, null, [data], null)!;
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Catalog Smoke Root, O=Certificate Catalog Test", key,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            string certificatePath = Path.Combine(folder, "root.cer");
            File.WriteAllBytes(certificatePath, certificate.RawData);
            catalogType.GetMethod("Import", Members)!.Invoke(catalog, [certificatePath]);
            Type dialogType = assembly.GetType("KillerPDF.Controls.CertificateCatalogDialog", true)!;
            Type themeType = assembly.GetType("KillerPDF.Services.Theme", true)!;
            MethodInfo applyTheme = assembly.GetType("KillerPDF.Services.ThemeManager", true)!.GetMethod("Apply", Members)!;
            int cases = 0;
            foreach (string theme in new[] { "Dark", "Light", "SE98" })
            {
                applyTheme.Invoke(null, [Enum.Parse(themeType, theme)]);
                var dialog = (Window)Activator.CreateInstance(dialogType, Members, null, [owner, catalog], null)!;
                try
                {
                    dialog.Show();
                    var search = Find<TextBox>(dialog, "CertificateCatalogSearch");
                    var list = Find<ListBox>(dialog, "CertificateCatalogList");
                    var trust = Find<Button>(dialog, "CertificateCatalogTrust");
                    var untrust = Find<Button>(dialog, "CertificateCatalogUntrust");
                    var remove = Find<Button>(dialog, "CertificateCatalogRemove");
                    Wait(() => Find<Button>(dialog, "CertificateCatalogRefresh").IsEnabled && list.Items.Count > 0);
                    search.Text = "Catalog Smoke Root"; Pump();
                    if (list.Items.Count != 1) throw new InvalidOperationException("Catalog search did not isolate the imported certificate.");
                    list.SelectedIndex = 0; Pump();
                    if (!trust.IsEnabled || untrust.IsEnabled || !remove.IsEnabled)
                        throw new InvalidOperationException("Import was trusted automatically or local actions are unavailable.");
                    Capture(dialog, Path.Combine(folder, theme + "-imported.png"));
                    Confirm(dialog, trust, accept: false);
                    if (IsTrusted()) throw new InvalidOperationException("Canceled trust changed the catalog.");
                    Confirm(dialog, trust, accept: true);
                    Wait(() => untrust.IsEnabled);
                    if (!IsTrusted()) throw new InvalidOperationException("Trust was not saved.");
                    untrust.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Wait(() => trust.IsEnabled);
                    if (IsTrusted()) throw new InvalidOperationException("Removing trust was not saved.");
                    Confirm(dialog, remove, accept: false);
                    if (list.Items.Count != 1) throw new InvalidOperationException("Canceled removal changed the catalog.");
                    search.Text = "no-such-certificate-7e1e"; Pump();
                    if (list.Items.Count != 0 || trust.IsEnabled || remove.IsEnabled)
                        throw new InvalidOperationException("Empty search left certificate actions enabled.");
                    search.Text = ""; Pump();
                    bool windowsFound = false;
                    for (int index = 0; index < list.Items.Count; index++)
                    {
                        list.SelectedIndex = index; Pump();
                        if (!remove.IsEnabled)
                        {
                            windowsFound = true;
                            if (trust.IsEnabled || untrust.IsEnabled)
                                throw new InvalidOperationException("Windows certificate trust is editable.");
                            break;
                        }
                    }
                    if (!windowsFound) throw new InvalidOperationException("Windows inventory was not exercised.");
                    search.Text = "Catalog Smoke Root"; Pump();
                    Confirm(dialog, remove, accept: true);
                    Wait(() => Find<Button>(dialog, "CertificateCatalogRefresh").IsEnabled && list.Items.Count == 0);
                    if (!Equals(dialogType.GetProperty("Changed", Members)!.GetValue(dialog), true))
                        throw new InvalidOperationException("Catalog changes did not signal signature refresh.");
                    catalogType.GetMethod("Import", Members)!.Invoke(catalog, [certificatePath]);
                    cases++;
                }
                finally { dialog.Close(); Pump(); }
            }
            Type localeType = assembly.GetType("KillerPDF.Services.Locale", true)!;
            MethodInfo applyLocale = assembly.GetType("KillerPDF.Services.LocaleManager", true)!.GetMethod("Apply", Members)!;
            applyTheme.Invoke(null, [Enum.Parse(themeType, "Dark")]);
            foreach (string locale in Enum.GetNames(localeType))
            {
                applyLocale.Invoke(null, [Enum.Parse(localeType, locale)]);
                var dialog = (Window)Activator.CreateInstance(dialogType, Members, null, [owner, catalog], null)!;
                try
                {
                    dialog.Width = dialog.MinWidth; dialog.Height = dialog.MinHeight;
                    dialog.Show();
                    Wait(() => Find<Button>(dialog, "CertificateCatalogRefresh").IsEnabled
                        && Find<ListBox>(dialog, "CertificateCatalogList").Items.Count > 0);
                    Find<TextBox>(dialog, "CertificateCatalogSearch").Text = "Catalog Smoke Root";
                    Pump();
                    foreach (Button button in Descendants(dialog).OfType<Button>().Where(button => button.IsVisible))
                    {
                        Point point = button.TranslatePoint(new Point(), dialog);
                        if (point.X < -1 || point.Y < -1 || point.X + button.ActualWidth > dialog.ActualWidth + 1
                            || point.Y + button.ActualHeight > dialog.ActualHeight + 1)
                            throw new InvalidOperationException(locale + ": a catalog action is outside the window.");
                    }
                    Capture(dialog, Path.Combine(folder, "locale-" + locale + ".png"));
                    cases++;
                }
                finally { dialog.Close(); Pump(); }
            }
            // Closing the manager must invalidate displayed signature status even when
            // another session or Windows changed trust and this dialog wrote nothing.
            Type mainType = owner.GetType();
            FieldInfo panel = mainType.GetField("_sidebarPanel", Members)!;
            panel.SetValue(owner, Enum.Parse(panel.FieldType, "Signatures"));
            FieldInfo generation = mainType.GetField("_sidebarPanelGeneration", Members)!;
            int before = (int)generation.GetValue(owner)!;
            bool closed = false;
            var existingWindows = Application.Current.Windows.Cast<Window>().ToHashSet();
            var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            closeTimer.Tick += (_, _) =>
            {
                Window? manager = Application.Current.Windows.Cast<Window>()
                    .FirstOrDefault(window => window.GetType() == dialogType && window.IsVisible
                        && !existingWindows.Contains(window));
                if (manager is null) return;
                closeTimer.Stop(); closed = true;
                manager.Close();
            };
            closeTimer.Start();
            try { mainType.GetMethod("OpenCertificateCatalog", Members)!.Invoke(owner, null); }
            finally { closeTimer.Stop(); }
            if (!closed || (int)generation.GetValue(owner)! <= before)
                throw new InvalidOperationException("Closing the catalog left signature status cached.");
            string result = $"PASS certificate catalog search, untrusted import, trust/untrust/removal and cancellation, Windows read-only controls, close refresh, and {cases} rendered cases; "
                + "app SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
            File.WriteAllText(Path.Combine(folder, "result.txt"), result);
            Console.WriteLine(result);
            return 0;

            bool IsTrusted()
            {
                var entries = (System.Collections.IEnumerable)catalogType.GetMethod("Read", Members)!.Invoke(catalog, [false])!;
                object entry = entries.Cast<object>().Single();
                return (bool)entry.GetType().GetProperty("IsTrustedRoot", Members)!.GetValue(entry)!;
            }
        }
        finally { owner?.Close(); app.Shutdown(); }
    }

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject =>
        Descendants(root).OfType<T>().Single(item => AutomationProperties.GetAutomationId(item) == id);

    private static void Confirm(Window dialog, Button action, bool accept)
    {
        bool answered = false;
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            Window? prompt = Application.Current.Windows.Cast<Window>()
                .FirstOrDefault(window => window.Owner == dialog && window.IsVisible);
            if (prompt is not null)
            {
                string label = (string)Application.Current.FindResource(accept ? "Str_Btn_Yes" : "Str_Btn_No");
                Button? response = Descendants(prompt).OfType<Button>()
                    .FirstOrDefault(button => Equals(button.Content, label));
                if (response is not null)
                {
                    timer.Stop(); answered = true;
                    response.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) { timer.Stop(); prompt.Close(); }
            }
            else if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) timer.Stop();
        };
        timer.Start();
        try { action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        finally { timer.Stop(); }
        if (!answered) throw new InvalidOperationException("Certificate confirmation was not handled.");
        Pump();
    }

    private static void Wait(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Certificate catalog did not finish loading.");
            Pump(); Thread.Sleep(20);
        }
        Pump();
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var image = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),
            (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); png.Save(file);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject nested in Descendants(child)) yield return nested;
        }
    }
}
