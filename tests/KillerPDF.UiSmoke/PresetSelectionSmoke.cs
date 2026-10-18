using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;

internal static class PresetSelectionSmoke
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
        var hostType = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("PresetSmokeHost"),
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
        Window? owner = null;
        try
        {
            foreach (string service in new[] { "ThemeManager", "LocaleManager", "FilmGrain" })
                assembly.GetType("KillerPDF.Services." + service, true)!
                    .GetMethod("Initialize", Members)!.Invoke(null, null);
            owner = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.MainWindow", true)!)!;
            owner.WindowStartupLocation = WindowStartupLocation.Manual;
            owner.Left = -10000; owner.Top = -10000; owner.ShowInTaskbar = false;
            owner.ShowActivated = false;
            owner.Show();
            Assembly engine = Assembly.Load("KillerPdf.Engine");
            Type presetType = engine.GetType("KillerPdf.Engine.Authoring.PdfTextFieldPreset", true)!;
            Array presets = Array.CreateInstance(presetType, 2);
            presets.SetValue(Activator.CreateInstance(presetType, ["Customer", 140d, 24d, 12d, null]), 0);
            presets.SetValue(Activator.CreateInstance(presetType, ["Reference", 80d, 20d, 10d, null]), 1);
            object collection = Activator.CreateInstance(
                engine.GetType("KillerPdf.Engine.Authoring.PdfTextFieldPresetCollection", true)!, [presets])!;
            Type themeType = assembly.GetType("KillerPDF.Services.Theme", true)!;
            MethodInfo apply = assembly.GetType("KillerPDF.Services.ThemeManager", true)!
                .GetMethod("Apply", Members)!;
            int count = 0;
            foreach (string theme in Enum.GetNames(themeType))
            {
                apply.Invoke(null, [Enum.Parse(themeType, theme)]);
                var dialog = (Window)Activator.CreateInstance(
                    assembly.GetType("KillerPDF.Controls.TextFieldPresetsDialog", true)!, Members,
                    null, [owner, collection, null], null)!;
                try
                {
                    dialog.Show();
                    dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    ListBox list = Descendants<ListBox>(dialog).Single();
                    foreach (int index in new[] { 0, 1 })
                    {
                        list.SelectedIndex = index;
                        dialog.UpdateLayout();
                        foreach (ListBoxItem item in list.Items)
                        {
                            TextBlock[] text = Descendants<TextBlock>(item).ToArray();
                            Color name = ColorOf(dialog.FindResource(item.IsSelected ? "SelectionFg" : "TextBrush"));
                            Color detail = ColorOf(dialog.FindResource(item.IsSelected ? "SelectionFg" : "MutedTextBrush"));
                            if (text.Length != 2 || ColorOf(item.Foreground) != name
                                || ColorOf(text[0].Foreground) != name || ColorOf(text[1].Foreground) != detail)
                                throw new InvalidOperationException($"{theme}: preset selection colors are overridden.");
                        }
                    }
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth),
                        (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(dialog);
                    var png = new PngBitmapEncoder();
                    png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(folder, theme + ".png"));
                    png.Save(stream);
                    count++;
                }
                finally { dialog.Close(); }
            }
            string result = $"PASS selected/unselected preset name and detail colors across {count} themes; "
                + "app SHA256=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
            File.WriteAllText(Path.Combine(folder, "result.txt"), result);
            Console.WriteLine(result);
            return 0;
        }
        finally { owner?.Close(); app.Shutdown(); }
    }

    private static Color ColorOf(object value) => value is SolidColorBrush brush
        ? brush.Color : throw new InvalidOperationException("Expected a solid semantic text brush.");

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) yield return found;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
}
