using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Automation;

internal static class PreviewExplorerSmoke
{
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr first, IntPtr second);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    internal static void Verify(string folder, CancellationToken cancellationToken, Action? afterLaunch = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string file = Path.Combine(folder, "explorer-input.pdf");
        File.WriteAllBytes(file, PreviewFailureSmoke.Fixture("incomplete"));
        var existing = AutomationElement.RootElement.FindAll(TreeScope.Children,
            new PropertyCondition(AutomationElement.ClassNameProperty, "CabinetWClass"))
            .Cast<AutomationElement>().Select(element => element.Current.NativeWindowHandle).ToHashSet();
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!)!;
        File.WriteAllText(Path.Combine(folder, "explorer-launch.json"), System.Text.Json.JsonSerializer.Serialize(
            new { File = file, ExistingWindows = existing.ToArray() }));
        using var launcher = Process.Start(new ProcessStartInfo("explorer.exe", $"/separate, /select,\"{file}\"") { UseShellExecute = true });
        AutomationElement? window = null;
        IntPtr handle = IntPtr.Zero;
        bool? initialPane = null;
        try
        {
            afterLaunch?.Invoke();
            Wait(() =>
            {
                var candidates = OwnedWindows(shell, file, existing);
                if (candidates.Length > 1) throw new InvalidOperationException("The owned Explorer window is ambiguous.");
                window = candidates.SingleOrDefault();
                return window is not null;
            }, "owned File Explorer window", cancellationToken);
            handle = new IntPtr(window!.Current.NativeWindowHandle);
            if (OwnedWindows(shell, file, new HashSet<int>(existing) { handle.ToInt32() }).Length != 0
                || OwnedWindows(shell, Path.Combine(folder, "not-selected.pdf"), existing).Length != 0)
                throw new InvalidOperationException("Explorer ownership detector accepted a pre-existing window or wrong selected file.");
            Console.WriteLine("PASS ownership detector rejects pre-existing HWND and wrong selected-file controls.");
            initialPane = PaneVisible(window);
            File.WriteAllText(Path.Combine(folder, "pane-initial.json"), System.Text.Json.JsonSerializer.Serialize(
                new { File = file, Window = handle.ToInt64(), InitialPane = initialPane }));
            Dump(window, folder, "explorer-before.txt");
            if (!initialPane.Value)
            {
                RequireOwned(shell, file, existing, handle);
                cancellationToken.ThrowIfCancellationRequested();
                TogglePane(window);
                Wait(() => PaneVisible(window), "Explorer preview pane enabled", cancellationToken);
            }
            Wait(() => Notice(window), "File Explorer selected PDF preview warning", cancellationToken);
            Dump(window, folder, "explorer-warning.txt");
            var retry = window.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>()
                .Single(element => element.Current.ControlType == ControlType.Button
                    && element.Current.Name == "Retry" && !element.Current.IsOffscreen);
            bool rejectedNoOp = false;
            try { RequireRetryTransition(retry, () => { }, 300, cancellationToken); }
            catch (TimeoutException) { rejectedNoOp = true; }
            if (!rejectedNoOp) throw new InvalidOperationException("The Retry detector accepted a no-op control.");
            RequireOwned(shell, file, existing, handle);
            cancellationToken.ThrowIfCancellationRequested();
            RequireRetryTransition(retry, () => ((InvokePattern)retry.GetCurrentPattern(InvokePattern.Pattern)).Invoke(), 20000, cancellationToken);
            Wait(() => Notice(window), "File Explorer Retry outcome", cancellationToken);
            Console.WriteLine("PASS Retry detector rejects no-op and observes disabled then enabled after invocation.");
            Console.WriteLine("PASS File Explorer selects the owned PDF and displays the accessible preview warning and working Retry.");
        }
        catch (Exception error)
        {
            Console.WriteLine("CONTROL Explorer failure before cleanup: " + error);
            if (window is not null) Dump(window, folder, "explorer-failure.txt");
            throw;
        }
        finally
        {
            if (handle == IntPtr.Zero)
            {
                try
                {
                    // Cancellation stops normal actions, but the external launch still belongs
                    // to this test. Resolve it without the cancelled token before teardown.
                    Wait(() =>
                    {
                        var candidates = OwnedWindows(shell, file, existing, requireSelection: false);
                        if (candidates.Length > 1) throw new InvalidOperationException("Pending Explorer launch is ambiguous.");
                        window = candidates.SingleOrDefault();
                        if (window is not null) handle = new IntPtr(window.Current.NativeWindowHandle);
                        return handle != IntPtr.Zero;
                    }, "pending owned Explorer launch resolved for cleanup");
                }
                catch (Exception error)
                {
                    File.WriteAllText(Path.Combine(folder, "launch-cleanup-pending.json"), System.Text.Json.JsonSerializer.Serialize(
                        new { File = file, ExistingWindows = existing.ToArray(), Error = error.Message }));
                    throw new InvalidOperationException("Owned Explorer launch cleanup is unresolved. Preserve the launch record for recovery.", error);
                }
            }
            if (handle != IntPtr.Zero)
            {
                RequireOwned(shell, file, existing, handle, requireSelection: false);
                if (initialPane.HasValue && PaneVisible(window!) != initialPane.Value)
                {
                    TogglePane(window!);
                    Wait(() => PaneVisible(window!) == initialPane.Value, "original Explorer preview-pane state restored");
                }
                File.WriteAllText(Path.Combine(folder, "pane-restoration.txt"), $"initial={initialPane};restored={PaneVisible(window!)}");
                PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
                Wait(() => !IsWindow(handle), "owned Explorer window closed");
            }
            Marshal.ReleaseComObject(shell);
        }
    }

    internal static void Recover(string folder)
    {
        bool pendingLaunch = !File.Exists(Path.Combine(folder, "pane-initial.json"));
        using var record = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, pendingLaunch ? "explorer-launch.json" : "pane-initial.json")));
        string file = Path.GetFullPath(record.RootElement.GetProperty("File").GetString()!);
        if (!file.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Recovery file is outside the explicitly supplied owned folder.");
        IntPtr handle = pendingLaunch ? IntPtr.Zero : new(record.RootElement.GetProperty("Window").GetInt64());
        bool? initial = pendingLaunch ? null : record.RootElement.GetProperty("InitialPane").GetBoolean();
        var existing = pendingLaunch ? record.RootElement.GetProperty("ExistingWindows").EnumerateArray().Select(value => value.GetInt32()).ToHashSet() : new HashSet<int>();
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!)!;
        try
        {
            var window = OwnedWindows(shell, file, existing, requireSelection: !pendingLaunch)
                .Single(element => handle == IntPtr.Zero || element.Current.NativeWindowHandle == handle.ToInt32());
            handle = new IntPtr(window.Current.NativeWindowHandle);
            Dump(window, folder, "explorer-recovery-before.txt");
            if (initial.HasValue && PaneVisible(window) != initial.Value)
            {
                RequireOwned(shell, file, [], handle);
                TogglePane(window);
                Wait(() => PaneVisible(window) == initial, "owned recovery pane restored");
            }
            File.WriteAllText(Path.Combine(folder, "pane-restoration.txt"), $"initial={initial};restored={PaneVisible(window)};recovered=True");
            PostMessage(handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
            Wait(() => !IsWindow(handle), "owned recovery window closed");
            Console.WriteLine("PASS explicitly owned prior Explorer pane restored and window closed.");
        }
        finally { Marshal.ReleaseComObject(shell); }
    }

    private static AutomationElement[] OwnedWindows(object shell, string file, HashSet<int> existing, bool requireSelection = true)
    {
        var matches = new List<AutomationElement>();
        dynamic windows = ((dynamic)shell).Windows();
        try
        {
            foreach (object entry in windows)
            {
                try
                {
                    dynamic browser = entry;
                    int handle = (int)browser.HWND;
                    if (existing.Contains(handle)) continue;
                    if (!Uri.TryCreate((string)browser.LocationURL, UriKind.Absolute, out Uri? location)
                        || !location.IsFile || !string.Equals(Path.GetFullPath(location.LocalPath).TrimEnd('\\'),
                            Path.GetDirectoryName(file)!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) continue;
                    if (!requireSelection)
                    {
                        matches.Add(AutomationElement.FromHandle(new IntPtr(handle)));
                        continue;
                    }
                    object document = browser.Document;
                    object selected = ((dynamic)document).SelectedItems();
                    try
                    {
                        if ((int)((dynamic)selected).Count != 1) continue;
                        object item = ((dynamic)selected).Item(0);
                        try
                        {
                            if (string.Equals(Path.GetFullPath((string)((dynamic)item).Path), file, StringComparison.OrdinalIgnoreCase))
                                matches.Add(AutomationElement.FromHandle(new IntPtr(handle)));
                        }
                        finally { Marshal.ReleaseComObject(item); }
                    }
                    finally { Marshal.ReleaseComObject(selected); Marshal.ReleaseComObject(document); }
                }
                catch (COMException) { }
                finally { Marshal.ReleaseComObject(entry); }
            }
        }
        finally { Marshal.ReleaseComObject(windows); }
        return matches.ToArray();
    }

    private static void RequireOwned(object shell, string file, HashSet<int> existing, IntPtr handle, bool requireSelection = true)
    {
        if (!OwnedWindows(shell, file, existing, requireSelection).Any(window => window.Current.NativeWindowHandle == handle.ToInt32()))
            throw new InvalidOperationException("Explorer no longer shows the exact owned folder and selected PDF. No input or close sent.");
    }

    private static bool PaneVisible(AutomationElement window) => window.FindAll(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.AutomationIdProperty, "ReadingPaneModuleInner"))
        .Cast<AutomationElement>().Any(element => !element.Current.IsOffscreen);

    private static void RequireRetryTransition(AutomationElement retry, Action invoke, int timeout, CancellationToken cancellationToken)
    {
        int phase = 0;
        AutomationPropertyChangedEventHandler changed = (_, args) =>
        {
            if (args.NewValue is false) Interlocked.CompareExchange(ref phase, 1, 0);
            else if (args.NewValue is true) Interlocked.CompareExchange(ref phase, 2, 1);
        };
        Automation.AddAutomationPropertyChangedEventHandler(retry, TreeScope.Element, changed, AutomationElement.IsEnabledProperty);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            invoke();
            var watch = Stopwatch.StartNew();
            while (Volatile.Read(ref phase) != 2)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (watch.ElapsedMilliseconds > timeout) throw new TimeoutException("Retry did not disable and reenable.");
                Thread.Sleep(10);
            }
        }
        finally { Automation.RemoveAutomationPropertyChangedEventHandler(retry, changed); }
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

    private static void TogglePane(AutomationElement window)
    {
        var button = window.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "PreviewPaneToggleButton"))
            .Cast<AutomationElement>().Single(element => !element.Current.IsOffscreen);
        ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    }

    private static void Wait(Func<bool> ready, string stage, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        while (!ready())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException(stage);
            Thread.Sleep(100);
        }
    }
}
