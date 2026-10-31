using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

internal static class PreviewExplorerSmoke
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void Key(byte key, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr first, IntPtr second);

    internal static void Verify(string folder)
    {
        string file = Path.Combine(folder, "explorer-input.pdf");
        File.WriteAllBytes(file, PreviewFailureSmoke.Fixture("incomplete"));
        using var launcher = Process.Start(new ProcessStartInfo("explorer.exe", $"/separate, /select,\"{file}\"") { UseShellExecute = true });
        AutomationElement? window = null;
        IntPtr handle = IntPtr.Zero;
        bool toggled = false;
        try
        {
            Wait(() =>
            {
                var candidates = AutomationElement.RootElement.FindAll(TreeScope.Children,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "CabinetWClass"))
                    .Cast<AutomationElement>().Where(element => element.Current.Name.Contains(Path.GetFileName(folder), StringComparison.Ordinal)).ToArray();
                if (candidates.Length > 1) throw new InvalidOperationException("The owned Explorer window is ambiguous.");
                window = candidates.SingleOrDefault();
                return window is not null;
            }, "owned File Explorer window");
            handle = new IntPtr(window!.Current.NativeWindowHandle);
            Dump(window, folder, "explorer-before.txt");
            if (!Notice(window))
            {
                SetForegroundWindow(handle);
                Wait(() => GetForegroundWindow() == handle, "owned Explorer foreground before Alt+P");
                AltP();
                toggled = true;
            }
            Wait(() => Notice(window), "File Explorer selected PDF preview warning");
            Dump(window, folder, "explorer-warning.txt");
            var retry = window.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                .Single(element => element.Current.ControlType == ControlType.Button
                    && element.Current.Name == "Retry" && !element.Current.IsOffscreen);
            ((InvokePattern)retry.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            Wait(() => retry.Current.IsEnabled && Notice(window), "File Explorer Retry completion");
            Console.WriteLine("PASS File Explorer selects the owned PDF and displays the accessible preview warning and working Retry.");
        }
        catch
        {
            if (window is not null) Dump(window, folder, "explorer-failure.txt");
            throw;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                if (toggled)
                {
                    SetForegroundWindow(handle);
                    if (GetForegroundWindow() == handle) AltP();
                }
                PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
            }
        }
    }

    private static bool Notice(AutomationElement window) => window.FindAll(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.NameProperty, "This page could not be displayed completely."))
        .Cast<AutomationElement>().Any(element => !element.Current.IsOffscreen);

    private static void Dump(AutomationElement window, string folder, string name)
    {
        var lines = new List<string>();
        foreach (AutomationElement element in window.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            try { lines.Add($"{element.Current.ControlType?.ProgrammaticName}|{element.Current.ClassName}|{element.Current.AutomationId}|{element.Current.Name}|offscreen={element.Current.IsOffscreen}"); }
            catch (ElementNotAvailableException) { lines.Add("Transient Explorer element disappeared during diagnostics."); }
        }
        File.WriteAllLines(Path.Combine(folder, name), lines);
    }

    private static void AltP()
    {
        try { Key(0x12, 0, 0, UIntPtr.Zero); Key(0x50, 0, 0, UIntPtr.Zero); }
        finally { Key(0x50, 0, 2, UIntPtr.Zero); Key(0x12, 0, 2, UIntPtr.Zero); }
    }

    private static void Wait(Func<bool> ready, string stage)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(stage);
            Thread.Sleep(100);
        }
    }
}
