using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

internal static class UpdateWindowSmoke
{
    internal static int Run(Window first, Assembly assembly, string appPath, string folder)
    {
        var second = (Window)Activator.CreateInstance(first.GetType())!;
        Type hostContract = assembly.GetType("KillerPDF.Features.IAboutUpdateRestart", true)!;
        PropertyInfo dirty = hostContract.GetProperty("IsDirty")!;
        bool Unsaved(Window window) => (bool)dirty.GetValue(window)!;
        string source = Path.Combine(folder, "update-secondary.pdf");
        byte[] original = SignedCopySmoke.Fixture(measurement: false, pageWidth: 300);
        File.WriteAllBytes(source, original);
        try
        {
            second.Show();
            first.GetType().GetMethod("OpenFromExternal", BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(second, [source]);
            Pump();
            if (Unsaved(first) || Unsaved(second)) throw new InvalidOperationException("The clean fixture starts dirty.");
            SessionEndingSmoke.RotateDocument(second);
            if (!Unsaved(second)) throw new InvalidOperationException("The real Rotate command did not leave unsaved edits.");
            Console.WriteLine("Update policy: primary is clean; real Rotate leaves secondary unsaved.");
            if (!Unsaved(first))
                throw new InvalidOperationException("The primary update host ignores unsaved edits in the secondary window.");
            if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original))
                throw new InvalidOperationException("The update policy probe changed its source PDF.");
            string result = "PASS update-window-state: the primary update host sees secondary-window edits. App SHA256="
                + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
            File.WriteAllText(Path.Combine(folder, "result.txt"), result);
            Console.WriteLine(result);
            return 0;
        }
        finally
        {
            var cleanup = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
            cleanup.Tick += (_, _) =>
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                    .Where(window => window != first && window != second && window.IsVisible).ToArray())
                    Children(dialog).OfType<Button>().FirstOrDefault(button =>
                        Equals(button.Content, Application.Current.FindResource("Str_Btn_DontSave")))?
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            cleanup.Start();
            try
            {
                second.Close();
                var elapsed = Stopwatch.StartNew();
                while (second.IsVisible && elapsed.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); Thread.Sleep(15); }
                if (second.IsVisible) throw new TimeoutException("The owned secondary window did not close.");
            }
            finally { cleanup.Stop(); }
        }
    }

    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Children(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
