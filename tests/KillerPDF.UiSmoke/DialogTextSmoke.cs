using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class DialogTextSmoke
{
    internal static int Run(Window owner, Assembly assembly, string folder)
    {
        Type theme = assembly.GetType("KillerPDF.Services.Theme", true)!;
        MethodInfo apply = assembly.GetType("KillerPDF.Services.ThemeManager", true)!
            .GetMethod("Apply", BindingFlags.Public | BindingFlags.Static)!;
        Type dialog = assembly.GetType("KillerPDF.KillerDialog", true)!;
        string message = (string)Application.Current.FindResource("Str_Dlg_UnsavedExit");
        foreach (string name in new[] { "Dark", "Greed", "SE98" })
        {
            apply.Invoke(null, [Enum.Parse(theme, name)]);
            foreach (bool withOptions in new[] { false, true })
            {
                Exception? failure = null;
                var clock = Stopwatch.StartNew();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
                timer.Tick += (_, _) =>
                {
                    Window? shown = Application.Current.Windows.Cast<Window>()
                        .FirstOrDefault(window => window != owner && window.IsVisible);
                    if (shown is null)
                    {
                        if (clock.Elapsed > TimeSpan.FromSeconds(10))
                            throw new TimeoutException("Save prompt did not open.");
                        return;
                    }
                    timer.Stop();
                    try
                    {
                        TextBlock text = Descendants(shown).OfType<TextBlock>().Single(block => block.Text == message);
                        if (text.FontSize > 13 || text.FontFamily.Source.Contains("Lacquer", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException($"{name} save message uses {text.FontSize} DIP {text.FontFamily.Source}.");
                        var image = new RenderTargetBitmap((int)Math.Ceiling(shown.ActualWidth),
                            (int)Math.Ceiling(shown.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        image.Render(shown);
                        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
                        using var file = File.Create(Path.Combine(folder, name + (withOptions ? "-options" : "") + ".png"));
                        png.Save(file);
                    }
                    catch (Exception error) { failure = error; }
                    finally { shown.Close(); }
                };
                timer.Start();
                try
                {
                    if (withOptions)
                        dialog.GetMethod("ShowTwoCheckChoice")!.Invoke(null,
                            [owner, message, "Close my open tabs", false, "Remember my choice", false,
                             "Save", "Don't Save", "Cancel", false]);
                    else
                        dialog.GetMethod("AskToSave")!.Invoke(null,
                            [owner, message, "Save", "Don't Save", "Cancel", false]);
                }
                finally { timer.Stop(); }
                if (failure is not null) throw failure;
            }
        }
        File.WriteAllText(Path.Combine(folder, "result.txt"),
            "PASS both save prompts use normal body text in Dark, Greed and 98SE.");
        Console.WriteLine("PASS save prompt typography in 6 rendered cases.");
        return 0;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject nested in Descendants(child)) yield return nested;
        }
    }
}
