using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace KillerPDF.LatencySmoke;

internal static class LiveViewerMemory
{
    private sealed record Visit(string Document, string WindowTitle, string DocumentLabel,
        int LastPage, int PageCount, int PageDownKeys);
    private sealed record Sample(string Version, int Run, bool Warmup, double FinalMiB,
        double PeakMiB, List<Visit> Documents, string[] VisibleTabs, string? Error);

    internal static int Run(Options options)
    {
        if (options.Documents.Count != 5 || options.Documents.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 5)
            throw new ArgumentException("M3 requires exactly five distinct documents.");
        if (!Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0)
            throw new InvalidOperationException("M3 requires an unlocked interactive test desktop.");
        if (Process.GetProcessesByName("KillerPDF").Length != 0 || Process.GetProcessesByName("KillerPDF.App").Length != 0)
            throw new InvalidOperationException("Close existing KillerPDF sessions before M3; none are closed automatically.");
        if (Directory.Exists(options.OutputDirectory)) throw new IOException("Use a fresh M3 output directory.");
        Directory.CreateDirectory(options.OutputDirectory);
        var inputs = options.Documents.ToDictionary(path => path, Hash);
        var samples = new List<Sample>();
        string csv = Path.Combine(options.OutputDirectory, "results.csv");
        File.WriteAllText(csv, "Version,Run,Warmup,FinalWorkingSetMiB,PeakWorkingSetMiB,Documents,Error" + Environment.NewLine);
        for (int run = 0; run < options.Warmup + options.Runs; run++)
        {
            IEnumerable<VersionSpec> order = run % 2 == 0 ? options.Versions : options.Versions.AsEnumerable().Reverse();
            foreach (VersionSpec version in order)
            {
                Sample sample = Measure(version, run, run < options.Warmup, options);
                samples.Add(sample);
                File.AppendAllText(csv, string.Join(",", Quote(sample.Version), run,
                    sample.Warmup ? "true" : "false", sample.FinalMiB.ToString("F3", CultureInfo.InvariantCulture),
                    sample.PeakMiB.ToString("F3", CultureInfo.InvariantCulture), sample.Documents.Count,
                    Quote(sample.Error ?? "")) + Environment.NewLine);
                bool changed = inputs.Any(pair => Hash(pair.Key) != pair.Value);
                File.WriteAllText(Path.Combine(options.OutputDirectory, "results.json"), JsonSerializer.Serialize(new
                {
                    inputs, inputsUnchanged = !changed, samples,
                    scope = "One live process with five visible document tabs, each traversed to its last page at Fit Width. No intermediate final-quality assertion."
                }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine($"M3 {version.Label} run {run}: {sample.Documents.Count}/5 documents, final {sample.FinalMiB:F1} MiB, peak {sample.PeakMiB:F1} MiB, error {sample.Error ?? "none"}");
                if (changed || sample.Error is not null) return 1;
            }
        }
        return 0;
    }

    private static Sample Measure(VersionSpec version, int run, bool warm, Options options)
    {
        string folder = Path.Combine(options.OutputDirectory, version.Label + "-" + run);
        if (Directory.Exists(folder)) throw new IOException("M3 sample output already exists.");
        Directory.CreateDirectory(folder);
        string marker = Path.Combine(folder, "launcher.marker");
        File.WriteAllText(marker, "");
        var visits = new List<Visit>();
        string[] tabs = [];
        double final = 0, peak = 0;
        string? failure = null;
        FrameCapture? capture = null;
        using Process process = Start(version.ExePath, options.Documents[0], marker);
        try
        {
            IntPtr hwnd = IntPtr.Zero;
            Wait(() =>
            {
                process.Refresh();
                if (process.HasExited) throw new IOException("M3 application exited before its window appeared.");
                hwnd = process.MainWindowHandle;
                return hwnd != IntPtr.Zero && Native.IsWindowVisible(hwnd);
            }, "M3 main window", 60000);
            Native.MoveWindow(hwnd, 0, 0, 1600, 1000, true);
            Native.SetForegroundWindow(hwnd);
            AutomationElement window = AutomationElement.FromHandle(hwnd);
            capture = new FrameCapture(hwnd);
            var clock = Stopwatch.StartNew();
            for (int index = 0; index < options.Documents.Count; index++)
            {
                string document = options.Documents[index];
                if (index > 0)
                {
                    using Process forwarder = Start(version.ExePath, document, marker);
                    if (!forwarder.WaitForExit(30000))
                    {
                        forwarder.Kill(true);
                        throw new TimeoutException("M3 document forwarding did not exit.");
                    }
                    if (forwarder.ExitCode != 0) throw new IOException("M3 document forwarding failed.");
                }
                AutomationElement documentLabel = Find(window, "FileNameLabel");
                Wait(() => documentLabel.Current.Name.Equals(Path.GetFileName(document), StringComparison.OrdinalIgnoreCase),
                    "M3 document label", 60000);
                capture.WaitForContentThenStable(clock, 60000);
                ZoomPreset.SelectFitWidth(hwnd, process.Id);
                AutomationElement scroll = Find(window, "PagePreviewPanel");
                scroll.SetFocus();
                Foreground(hwnd);
                Native.Tap(Native.VK_HOME);
                capture.WaitForStable(clock, 60000, requireChange: false);
                AutomationElement status = Find(window, "StatusText");
                (int page, int total) = Page(status);
                if (page != 1 || total < 1) throw new InvalidDataException("M3 did not start at page one.");
                int keys = 0;
                var traverse = Stopwatch.StartNew();
                while (page < total)
                {
                    if (keys >= total + 16 || traverse.Elapsed > TimeSpan.FromMinutes(5))
                        throw new TimeoutException("M3 page traversal did not reach the last page.");
                    Foreground(hwnd);
                    Native.Tap(Native.VK_NEXT);
                    keys++;
                    Thread.Sleep(80);
                    (page, int reportedTotal) = Page(status);
                    if (reportedTotal != total) throw new InvalidDataException("M3 document page count changed.");
                }
                capture.WaitForStable(clock, 60000, requireChange: false);
                (page, int settledTotal) = Page(status);
                if (page != total || settledTotal != total) throw new InvalidDataException("M3 did not settle on the last page.");
                capture.Save(Path.Combine(folder, $"document-{index + 1}-last.png"));
                visits.Add(new(document, window.Current.Name, documentLabel.Current.Name, page, total, keys));
            }
            var labels = window.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            tabs = labels.Cast<AutomationElement>().Where(element => !element.Current.IsOffscreen)
                .Select(element => element.Current.Name).Distinct().ToArray();
            foreach (string document in options.Documents)
                if (!tabs.Any(title => title.Equals(Path.GetFileName(document), StringComparison.OrdinalIgnoreCase)
                    || title.Equals(Path.GetFileNameWithoutExtension(document), StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("M3 did not retain a visible tab for " + Path.GetFileName(document));
            Thread.Sleep(2000);
            process.Refresh();
            final = process.WorkingSet64 / 1048576.0;
            peak = process.PeakWorkingSet64 / 1048576.0;
            capture.Save(Path.Combine(folder, "five-documents-final.png"));
        }
        catch (Exception error)
        {
            failure = error.ToString();
            capture?.Save(Path.Combine(folder, "error.png"));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(4000)) process.Kill(true);
            }
        }
        return new(version.Label, run, warm, final, peak, visits, tabs, failure);
    }

    private static Process Start(string exe, string document, string marker)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        start.ArgumentList.Add(document);
        start.Environment["KILLERPDF_LAUNCHER_PATH"] = marker;
        return Process.Start(start) ?? throw new IOException("M3 process did not start.");
    }

    private static AutomationElement Find(AutomationElement window, string id) =>
        window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id))
            .Cast<AutomationElement>().FirstOrDefault(element => !element.Current.IsOffscreen)
        ?? throw new InvalidDataException("M3 control is unavailable: " + id);

    private static (int Page, int Total) Page(AutomationElement status)
    {
        Match match = Regex.Match(status.Current.Name, @"\bPage\s+(\d+)\s+of\s+(\d+)\b", RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidDataException("M3 requires the English page status: " + status.Current.Name);
        return (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    private static void Foreground(IntPtr hwnd)
    {
        if (Native.GetForegroundWindow() != hwnd) throw new InvalidOperationException("M3 measured window lost foreground.");
    }

    private static void Wait(Func<bool> predicate, string stage, int timeout)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate())
        {
            if (clock.ElapsedMilliseconds > timeout) throw new TimeoutException(stage);
            Thread.Sleep(20);
        }
    }

    private static string Hash(string path)
    {
        using FileStream input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
