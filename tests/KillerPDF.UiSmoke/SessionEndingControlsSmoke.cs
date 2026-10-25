using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

internal static class SessionEndingControlsSmoke
{
    internal static int Run(Window first, Assembly assembly, string appPath, string folder)
    {
        var second = (Window)Activator.CreateInstance(first.GetType())!;
        string[] sources = [Path.Combine(folder, "first.pdf"), Path.Combine(folder, "second.pdf")];
        byte[][] bytes = [SignedCopySmoke.Fixture(false), SignedCopySmoke.Fixture(false, 300)];
        for (int index = 0; index < sources.Length; index++) File.WriteAllBytes(sources[index], bytes[index]);
        MethodInfo setting = assembly.GetType("KillerPDF.App", true)!
            .GetMethod("SetSetting", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        setting.Invoke(null, ["CloseToTray", "1"]);
        try
        {
            second.Show();
            Window[] windows = [first, second];
            for (int index = 0; index < windows.Length; index++)
            {
                first.GetType().GetMethod("OpenFromExternal", BindingFlags.Instance | BindingFlags.Public)!
                    .Invoke(windows[index], [sources[index]]);
                Pump();
                SessionEndingSmoke.RotateDocument(windows[index]);
                if (!Dirty(windows[index])) throw new InvalidOperationException("A control window did not become dirty.");
            }

            bool canceled = Request(index => index == 1 ? "Str_Btn_DontSave" : "Str_Btn_CancelDlg", windows);
            if (!canceled || windows.Any(window => !window.IsVisible || !Dirty(window)))
                throw new InvalidOperationException("Cancel on the later window did not preserve every dirty window.");
            Console.WriteLine("PASS later-window Cancel preserves both unsaved windows after the first approval.");

            if (Request(_ => "Str_Btn_DontSave", windows))
                throw new InvalidOperationException("Approving both dirty windows did not permit session ending.");
            foreach (Window window in windows) window.Close();
            Pump();
            if (windows.Any(window => window.IsVisible || PresentationSource.FromVisual(window) is not null))
                throw new InvalidOperationException("Approved session ending hid a window to tray or delayed closure.");
            for (int index = 0; index < sources.Length; index++)
                if (!File.ReadAllBytes(sources[index]).AsSpan().SequenceEqual(bytes[index]))
                    throw new InvalidOperationException("Don't Save changed an original source PDF.");
            string result = "PASS later-window Cancel preserves both edits; Don't Save in both windows permits shutdown and closes despite tray mode, without repeated prompts or source changes. App SHA256="
                + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
            File.WriteAllText(Path.Combine(folder, "result.txt"), result);
            Console.WriteLine(result);
            return 0;
        }
        finally
        {
            setting.Invoke(null, ["CloseToTray", "0"]);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            timer.Tick += (_, _) =>
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                    .Where(window => window != first && window != second && window.IsVisible).ToArray())
                    Children(dialog).OfType<Button>().FirstOrDefault(button =>
                        Equals(button.Content, Application.Current.FindResource("Str_Btn_DontSave")))?
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            timer.Start();
            try
            {
                if (PresentationSource.FromVisual(second) is not null) second.Close();
                var elapsed = Stopwatch.StartNew();
                while (second.IsVisible && elapsed.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); Thread.Sleep(15); }
            }
            finally { timer.Stop(); }
        }
    }

    private static bool Request(Func<int, string> choice, Window[] windows)
    {
        int prompts = 0;
        Exception? failure = null;
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        var elapsed = Stopwatch.StartNew();
        timer.Tick += (_, _) =>
        {
            try
            {
                if (elapsed.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Control prompt timed out.");
                foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                    .Where(window => !windows.Contains(window) && window.IsVisible).ToArray())
                {
                    if (!seen.Add(dialog)) continue;
                    string label = (string)Application.Current.FindResource(choice(++prompts));
                    Children(dialog).OfType<Button>().Single(button => Equals(button.Content, label))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            }
            catch (Exception error)
            {
                failure = error; timer.Stop();
                foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                    .Where(window => !windows.Contains(window)).ToArray()) dialog.Close();
            }
        };
        var arguments = (SessionEndingCancelEventArgs)Activator.CreateInstance(typeof(SessionEndingCancelEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [ReasonSessionEnding.Shutdown], null)!;
        timer.Start();
        try { Application.Current.GetType().GetMethod("SimulateSessionEnding")!.Invoke(Application.Current, [arguments]); }
        finally { timer.Stop(); }
        if (failure is not null) throw failure;
        if (prompts != 2) throw new InvalidOperationException($"Expected two real save prompts, got {prompts}.");
        return arguments.Cancel;
    }

    private static bool Dirty(Window window) => ((Button)window.FindName("SaveAsBtn")).Foreground
        is SolidColorBrush brush && brush.Color == Color.FromRgb(0xE0, 0x73, 0);
    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Children(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
