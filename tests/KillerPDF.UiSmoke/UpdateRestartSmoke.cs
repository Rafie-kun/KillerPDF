using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

internal static class UpdateRestartSmoke
{
    internal static int Stage(Window first, Assembly assembly, string appPath, string folder, string sharedState, bool earlyExit = false)
    {
        Type windowType = first.GetType();
        var second = (Window)Activator.CreateInstance(windowType)!;
        var closed = (Window)Activator.CreateInstance(windowType)!;
        MethodInfo setting = assembly.GetType("KillerPDF.App", true)!
            .GetMethod("SetSetting", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo getSetting = assembly.GetType("KillerPDF.App", true)!
            .GetMethod("GetSetting", BindingFlags.Static | BindingFlags.NonPublic)!;
        MethodInfo prepare = windowType
            .GetMethod("PrepareUpdateRestart", BindingFlags.Instance | BindingFlags.NonPublic)!;
        MethodInfo open = windowType.GetMethod("OpenFromExternal", BindingFlags.Instance | BindingFlags.Public)!;
        object? handoff = null;
        string fixtures = Path.Combine(sharedState, "fixtures");
        string statePath = Path.Combine(sharedState, "fixture-state.json");
        if (File.Exists(statePath) || Directory.Exists(fixtures)) throw new IOException("Use fresh restart fixture state.");
        Directory.CreateDirectory(fixtures);
        string[] files = new[] { "first.pdf", "second.pdf", "third.pdf", "closed.pdf" }
            .Select(name => Path.Combine(fixtures, name)).ToArray();
        foreach (string file in files) File.WriteAllBytes(file, SignedCopySmoke.Fixture(measurement: false));
        try
        {
            first.WindowState = WindowState.Normal;
            first.Width = 880; first.Height = 620; first.Left = 32; first.Top = 40;
            first.ShowInTaskbar = true;
            second.Show(); closed.Show();
            open.Invoke(first, [files[0]]); Pump();
            open.Invoke(first, [files[1]]); Pump();
            open.Invoke(second, [files[2]]); Pump();
            open.Invoke(closed, [files[3]]); Pump();
            Close(closed);
            setting.Invoke(null, ["RememberOpenFiles", "0"]);
            double normalWidth = first.ActualWidth, normalHeight = first.ActualHeight;
            PressF11(first); PumpFor(700);
            if (Math.Abs(first.ActualWidth - normalWidth) < 1 && Math.Abs(first.ActualHeight - normalHeight) < 1)
                throw new InvalidOperationException("The F11 positive control did not change window geometry.");
            PressF11(first); PumpFor(700);
            if (Math.Abs(first.ActualWidth - normalWidth) >= 1 || Math.Abs(first.ActualHeight - normalHeight) >= 1)
                throw new InvalidOperationException("The F11 positive control did not restore normal geometry.");
            Console.WriteLine("Update restart: three saved live files, one previously closed file, remember-files disabled.");
            handoff = prepare.Invoke(first, null)
                ?? throw new InvalidOperationException("Clean saved windows did not produce an update restart handoff.");
            if (((UIElement)first.FindName("RootBorder")).IsEnabled || ((UIElement)second.FindName("RootBorder")).IsEnabled)
                throw new InvalidOperationException("Prepared windows remain editable.");
            if (!first.IsEnabled || !second.IsEnabled) throw new InvalidOperationException("Prepared native windows cannot be closed by the installer.");
            double preparedWidth = first.ActualWidth, preparedHeight = first.ActualHeight;
            PressF11(first); PumpFor(700);
            if (Math.Abs(first.ActualWidth - preparedWidth) >= 1 || Math.Abs(first.ActualHeight - preparedHeight) >= 1)
                throw new InvalidOperationException("Window shortcuts remain active during update preparation.");
            ((IDisposable)handoff).Dispose(); handoff = null;
            if (!((UIElement)first.FindName("RootBorder")).IsEnabled || !((UIElement)second.FindName("RootBorder")).IsEnabled)
                throw new InvalidOperationException("Canceled preparation left editing disabled.");
            handoff = prepare.Invoke(first, null)
                ?? throw new InvalidOperationException("Canceled preparation left a pending restart record.");
            setting.Invoke(null, ["CloseToTray", "1"]);
            Type contract = assembly.GetType("KillerPDF.Features.IUpdateRestartHandoff", true)!;
            if (!earlyExit)
            {
                contract.GetMethod("Started")!.Invoke(handoff, null);
                contract.GetMethod("Commit")!.Invoke(handoff, null);
                ((IDisposable)handoff).Dispose(); handoff = null;
                if (((UIElement)first.FindName("RootBorder")).IsEnabled)
                    throw new InvalidOperationException("Committed scope disposal released the shutdown handoff.");
            }
            var closeRequest = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true };
            closeRequest.ArgumentList.Add(typeof(UpdateRestartSmoke).Assembly.Location);
            closeRequest.ArgumentList.Add("--request-process-close");
            closeRequest.ArgumentList.Add(Environment.ProcessId.ToString());
            using (var helper = Process.Start(closeRequest)!)
            {
                var elapsed = Stopwatch.StartNew();
                while (!helper.HasExited && elapsed.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); Thread.Sleep(15); }
                if (!helper.HasExited) { helper.Kill(entireProcessTree: true); throw new TimeoutException("Native close helper timed out."); }
                string output = helper.StandardOutput.ReadToEnd();
                string error = helper.StandardError.ReadToEnd();
                if (helper.ExitCode != 0 || !output.Contains("PASS native updater close request") || error.Length != 0)
                    throw new InvalidOperationException("Native updater close failed: " + output + error);
                Console.Write(output);
            }
            Close(second); Close(first);
            if (earlyExit)
            {
                var exitFrame = new DispatcherFrame();
                bool exited = false;
                Application app = Application.Current;
                app.Exit += (_, _) =>
                {
                    ((IDisposable)handoff!).Dispose(); handoff = null;
                    exited = true; exitFrame.Continue = false;
                };
                app.Shutdown();
                Dispatcher.PushFrame(exitFrame);
                if (!exited) throw new InvalidOperationException("The early native-close fixture never reached application exit.");
                Console.WriteLine("Update restart: application exited before updater activation returned.");
            }
            if (!Equals(getSetting.Invoke(null, ["RememberOpenFiles"]), "0"))
                throw new InvalidOperationException("Update preparation changed the remembered-files preference.");
            File.WriteAllText(statePath, JsonSerializer.Serialize(new
            {
                files = files.Take(3).ToArray(), excluded = files[3], active = files[1],
                hashes = files.Select(file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))).ToArray(),
                width = 880, height = 620, left = 32, top = 40
            }));
            string result = "PASS update-restart-stage: saved live files captured, cancellation restored editing, clean windows closed despite tray mode, preference unchanged. App SHA256="
                + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
            File.WriteAllText(Path.Combine(folder, "result.txt"), result);
            Console.WriteLine(result);
            return 0;
        }
        finally
        {
            (handoff as IDisposable)?.Dispose();
            setting.Invoke(null, ["CloseToTray", "0"]);
            Close(closed); Close(second);
        }
    }

    private static void Close(Window window)
    {
        if (PresentationSource.FromVisual(window) is null) return;
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        var elapsed = Stopwatch.StartNew();
        while (!closed && elapsed.Elapsed < TimeSpan.FromSeconds(10)) { Pump(); Thread.Sleep(15); }
        if (!closed) throw new TimeoutException("An owned restart fixture window did not close.");
    }

    internal static int Consume(Window restored, Assembly assembly, string appPath, string folder, string sharedState)
    {
        using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(sharedState, "fixture-state.json")));
        JsonElement fixture = state.RootElement;
        string[] expected = fixture.GetProperty("files").EnumerateArray().Select(item => item.GetString()!).ToArray();
        string excluded = fixture.GetProperty("excluded").GetString()!;
        Pump();
        string[] captions = Descendants(restored).OfType<System.Windows.Controls.ItemsControl>()
            .Where(control => control.Name == "TabStrip" && control.IsVisible)
            .SelectMany(Descendants).OfType<System.Windows.Controls.TextBlock>()
            .Where(block => block.Name == "tabLbl").Select(block => block.Text).ToArray();
        if (!captions.Order().SequenceEqual(expected.Select(Path.GetFileNameWithoutExtension).Order()))
            throw new InvalidOperationException("The restored window does not contain exactly the current saved files: " + string.Join("|", captions));
        string active = fixture.GetProperty("active").GetString()!;
        if (!Descendants(restored).OfType<System.Windows.Controls.TextBlock>()
            .Any(block => block.Name == "FileNameLabel" && block.Text == Path.GetFileName(active)))
            throw new InvalidOperationException("The previously active saved file was not selected.");
        if (Math.Abs(restored.ActualWidth - fixture.GetProperty("width").GetDouble()) >= 1
            || Math.Abs(restored.ActualHeight - fixture.GetProperty("height").GetDouble()) >= 1
            || Math.Abs(restored.Left - fixture.GetProperty("left").GetDouble()) >= 1
            || Math.Abs(restored.Top - fixture.GetProperty("top").GetDouble()) >= 1)
            throw new InvalidOperationException("The original window geometry was not retained.");
        Type app = assembly.GetType("KillerPDF.App", true)!;
        MethodInfo getSetting = app.GetMethod("GetSetting", BindingFlags.Static | BindingFlags.NonPublic)!;
        if (!Equals(getSetting.Invoke(null, ["RememberOpenFiles"]), "0"))
            throw new InvalidOperationException("Restoration changed the remembered-files preference.");
        app.GetMethod("LoadPendingUpdateRestart", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        var next = (Window)Activator.CreateInstance(restored.GetType())!;
        try
        {
            next.Show(); Pump();
            if (!Descendants(next).OfType<FrameworkElement>().Any(element => element.Name == "DropZone" && element.IsVisible))
                throw new InvalidOperationException("The one-time update session was replayed.");
        }
        finally { Close(next); }
        string[] hashes = fixture.GetProperty("hashes").EnumerateArray().Select(item => item.GetString()!).ToArray();
        string[] all = [.. expected, excluded];
        for (int i = 0; i < all.Length; i++)
            if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(all[i]))) != hashes[i])
                throw new InvalidOperationException("Update restoration changed a source PDF.");
        string result = "PASS update-restart-consume: exact saved files in one window, active file and original geometry restored, closed history excluded, preference and sources unchanged, no replay. App SHA256="
            + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        Console.WriteLine(result);
        return 0;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(System.Windows.Media.VisualTreeHelper.GetChild(parent, i))) yield return child;
    }

    private static void PressF11(Window window) => window.RaiseEvent(new System.Windows.Input.KeyEventArgs(
        System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount,
        System.Windows.Input.Key.F11) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });

    private static void PumpFor(int milliseconds)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < milliseconds) { Pump(); Thread.Sleep(15); }
    }

    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
