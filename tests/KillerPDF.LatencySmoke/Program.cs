using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace KillerPDF.LatencySmoke;

/// <summary>
/// Black-box interactive latency measurement for KillerPDF release gates S1 to S3 and M3.
/// Launches a built KillerPDF.exe with a document, captures the window at a fixed cadence,
/// and times three events from frame stability: first paint, scroll to final quality, and
/// zoom to refined output. The same procedure runs against every version so PDFium and the
/// managed engine are measured identically. Nothing in either application is instrumented.
/// </summary>
internal static class Program
{
    private const int ScenarioTimeoutMs = 60_000;
    private const int WindowWidth = 1600;
    private const int WindowHeight = 1000;
    private const int ScrollPages = 19;

    private static int Main(string[] args)
    {
        try
        {
            if (args is ["--verify-content-frame", var framePath, var expected])
                return FrameCapture.VerifyContentFrame(framePath, expected);
            var options = Options.Parse(args);
            if (options.VerifyCaptureStability)
                return CaptureStabilityProbe.Run();
            if (options.SelfCheck)
            {
                Console.WriteLine("Self-check: argument parsing and capture backend OK.");
                using var probe = new Bitmap(8, 8, PixelFormat.Format32bppArgb);
                return 0;
            }

            if (Process.GetProcessesByName("KillerPDF").Length > 0)
            {
                Console.Error.WriteLine("A KillerPDF instance is already running. Close it first; the harness never closes a user session.");
                return 2;
            }

            Directory.CreateDirectory(options.OutputDirectory);
            var results = new List<RunResult>();
            string csvPath = Path.Combine(options.OutputDirectory, "results.csv");
            File.WriteAllText(csvPath, RunResult.CsvHeader + Environment.NewLine);

            // Balanced order: for each run index, each document, alternate version order so neither
            // version always follows the other. Warmup runs are recorded and flagged, never dropped.
            for (int run = 0; run < options.Warmup + options.Runs; run++)
            {
                bool warm = run < options.Warmup;
                foreach (string doc in options.Documents)
                {
                    var order = run % 2 == 0 ? options.Versions : options.Versions.AsEnumerable().Reverse().ToList();
                    foreach (var version in order)
                    {
                        var result = Measure(version, doc, run, warm, options);
                        results.Add(result);
                        File.AppendAllText(csvPath, result.ToCsv() + Environment.NewLine);
                        Console.WriteLine(result.ToConsole());
                        if (!string.IsNullOrEmpty(result.Error))
                        {
                            File.WriteAllText(Path.Combine(options.OutputDirectory, "summary.md"), Summary.Build(results, options));
                            Console.Error.WriteLine("Measurement phase stopped after an invalid sample. All completed rows are retained.");
                            return 1;
                        }
                    }
                }
            }

            File.WriteAllText(Path.Combine(options.OutputDirectory, "summary.md"), Summary.Build(results, options));
            Console.WriteLine($"Results: {csvPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// AppDataPaths treats the directory of KILLERPDF_LAUNCHER_PATH as the portable root, so each
    /// launch gets its own KillerPDF-Data folder and the user's real settings are never touched.
    /// The first launch of a version seeds a settings folder with a plain open and close so every
    /// measured run starts from the same default view instead of whatever zoom the previous run
    /// left behind.
    /// </summary>
    private static string PrepareSettings(VersionSpec version, string document, int run, Options options)
    {
        string root = Path.Combine(options.OutputDirectory, "settings", version.Label);
        string seed = Path.Combine(root, "seed");
        string seedData = Path.Combine(seed, "KillerPDF-Data");
        if (!Directory.Exists(seedData))
        {
            Directory.CreateDirectory(seed);
            string seedMarker = Path.Combine(seed, "launcher.marker");
            File.WriteAllText(seedMarker, string.Empty);
            var start = StartInfo(version, document, seedMarker);
            var clock = Stopwatch.StartNew();
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Seed process did not start.");
            try
            {
                IntPtr hwnd = WaitForMainWindow(process, clock);
                Native.MoveWindow(hwnd, 0, 0, WindowWidth, WindowHeight, true);
                new FrameCapture(hwnd).WaitForContentThenStable(clock, ScenarioTimeoutMs);
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(10_000)) process.Kill(true);
                }
            }
            if (!Directory.Exists(seedData)) Directory.CreateDirectory(seedData);
        }

        string runDir = Path.Combine(root, $"run-{run}");
        if (Directory.Exists(runDir)) Directory.Delete(runDir, true);
        CopyTree(seedData, Path.Combine(runDir, "KillerPDF-Data"));
        string marker = Path.Combine(runDir, "launcher.marker");
        File.WriteAllText(marker, string.Empty);
        return marker;
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), true);
    }

    private static ProcessStartInfo StartInfo(VersionSpec version, string document, string launcherMarker)
    {
        var start = new ProcessStartInfo(version.ExePath, Quote(document))
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(version.ExePath)!,
        };
        start.Environment["KILLERPDF_LAUNCHER_PATH"] = launcherMarker;
        return start;
    }

    private static RunResult Measure(VersionSpec version, string document, int run, bool warm, Options options)
    {
        string launcherMarker = PrepareSettings(version, document, run, options);
        var start = StartInfo(version, document, launcherMarker);

        var result = new RunResult(version.Label, Path.GetFileName(document), run, warm);
        var clock = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Process did not start.");
        var sampler = new WorkingSetSampler(process);
        FrameCapture? capture = null;
        try
        {
            IntPtr hwnd = WaitForMainWindow(process, clock);
            result.WindowMs = clock.Elapsed.TotalMilliseconds;
            Native.MoveWindow(hwnd, 0, 0, WindowWidth, WindowHeight, true);
            Native.SetForegroundWindow(hwnd);

            capture = new FrameCapture(hwnd);
            var paint = capture.WaitForContentThenStable(clock, ScenarioTimeoutMs);
            result.FirstPaintMs = paint.FirstContentMs;
            result.PaintStableMs = paint.StableMs;
            capture.Save(Path.Combine(options.OutputDirectory, "frames", $"{version.Label}-{result.Document}-{run}-paint.png"));

            if (options.Scroll)
            {
                Native.SetForegroundWindow(hwnd);
                double t0 = clock.Elapsed.TotalMilliseconds;
                for (int i = 0; i < ScrollPages; i++)
                {
                    Native.Tap(Native.VK_NEXT);
                    Thread.Sleep(80);
                }
                var scroll = capture.WaitForStable(clock, ScenarioTimeoutMs);
                result.ScrollMs = scroll.StableMs - t0;
                capture.Save(Path.Combine(options.OutputDirectory, "frames", $"{version.Label}-{result.Document}-{run}-scroll.png"));
                Native.Tap(Native.VK_HOME);
                capture.WaitForStable(clock, ScenarioTimeoutMs, requireChange: false);
            }

            if (options.Zoom)
            {
                Native.SetForegroundWindow(hwnd);
                Native.Chord(Native.VK_CONTROL, Native.VK_0);
                capture.WaitForStable(clock, ScenarioTimeoutMs, requireChange: false);
                double t0 = clock.Elapsed.TotalMilliseconds;
                ZoomPreset.Select200Percent(hwnd, process.Id);
                var zoom = capture.WaitForStable(clock, ScenarioTimeoutMs);
                result.ZoomMs = zoom.StableMs - t0;
                capture.Save(Path.Combine(options.OutputDirectory, "frames", $"{version.Label}-{result.Document}-{run}-zoom.png"));
            }

            result.PeakWorkingSetMiB = sampler.PeakMiB;
            result.FinalWorkingSetMiB = sampler.CurrentMiB;
        }
        catch (TimeoutException ex)
        {
            result.Error = ex.Message;
            capture?.Save(Path.Combine(options.OutputDirectory, "frames", $"{version.Label}-{result.Document}-{run}-error.png"));
        }
        finally
        {
            sampler.Stop();
            if (!process.HasExited)
            {
                process.CloseMainWindow();
                if (!process.WaitForExit(4000)) process.Kill(true);
            }
        }
        return result;
    }

    private static IntPtr WaitForMainWindow(Process process, Stopwatch clock)
    {
        while (clock.ElapsedMilliseconds < ScenarioTimeoutMs)
        {
            process.Refresh();
            if (process.HasExited) throw new TimeoutException("Process exited before showing a window.");
            if (process.MainWindowHandle != IntPtr.Zero && Native.IsWindowVisible(process.MainWindowHandle))
                return process.MainWindowHandle;
            Thread.Sleep(5);
        }
        throw new TimeoutException("Main window did not appear.");
    }

    private static string Quote(string path) => "\"" + path + "\"";
}

internal sealed record VersionSpec(string Label, string ExePath);

internal sealed class Options
{
    public List<VersionSpec> Versions { get; } = new();
    public List<string> Documents { get; } = new();
    public string OutputDirectory { get; private set; } = string.Empty;
    public int Runs { get; private set; } = 5;
    public int Warmup { get; private set; } = 1;
    public bool Scroll { get; private set; } = true;
    public bool Zoom { get; private set; } = true;
    public bool SelfCheck { get; private set; }
    public bool VerifyCaptureStability { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value.");
            switch (a)
            {
                case "--version":
                {
                    string spec = Next();
                    int eq = spec.IndexOf('=');
                    if (eq <= 0) throw new ArgumentException("--version expects label=path\\to\\KillerPDF.exe");
                    string exe = Path.GetFullPath(spec[(eq + 1)..]);
                    if (!File.Exists(exe)) throw new FileNotFoundException(exe);
                    o.Versions.Add(new VersionSpec(spec[..eq], exe));
                    break;
                }
                case "--doc":
                {
                    string doc = Path.GetFullPath(Next());
                    if (!File.Exists(doc)) throw new FileNotFoundException(doc);
                    o.Documents.Add(doc);
                    break;
                }
                case "--out": o.OutputDirectory = Path.GetFullPath(Next()); break;
                case "--runs": o.Runs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--warmup": o.Warmup = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--no-scroll": o.Scroll = false; break;
                case "--no-zoom": o.Zoom = false; break;
                case "--self-check": o.SelfCheck = true; break;
                case "--verify-capture-stability": o.VerifyCaptureStability = true; break;
                default: throw new ArgumentException($"Unknown argument {a}");
            }
        }
        if (o.SelfCheck || o.VerifyCaptureStability) return o;
        if (o.Versions.Count == 0) throw new ArgumentException("At least one --version label=exe is required.");
        if (o.Documents.Count == 0) throw new ArgumentException("At least one --doc is required.");
        if (o.OutputDirectory.Length == 0) throw new ArgumentException("--out is required.");
        string profile = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (!o.OutputDirectory.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--out must be inside the user profile.");
        return o;
    }
}

internal sealed class RunResult
{
    public RunResult(string version, string document, int run, bool warm)
    {
        Version = version; Document = document; Run = run; Warm = warm;
    }

    public string Version { get; }
    public string Document { get; }
    public int Run { get; }
    public bool Warm { get; }
    public double WindowMs { get; set; }
    public double FirstPaintMs { get; set; }
    public double PaintStableMs { get; set; }
    public double ScrollMs { get; set; }
    public double ZoomMs { get; set; }
    public double PeakWorkingSetMiB { get; set; }
    public double FinalWorkingSetMiB { get; set; }
    public string? Error { get; set; }

    public const string CsvHeader = "version,document,run,warmup,window_ms,first_paint_ms,paint_stable_ms,scroll_ms,zoom_ms,peak_ws_mib,final_ws_mib,error";

    public string ToCsv() => string.Join(',',
        Version, Document, Run, Warm ? 1 : 0,
        F(WindowMs), F(FirstPaintMs), F(PaintStableMs), F(ScrollMs), F(ZoomMs),
        F(PeakWorkingSetMiB), F(FinalWorkingSetMiB), Error is null ? string.Empty : "\"" + Error.Replace('"', '\'') + "\"");

    public string ToConsole() =>
        $"{Version,-10} {Document,-40} run {Run}{(Warm ? " warm" : "")}: window {F(WindowMs)} first {F(FirstPaintMs)} stable {F(PaintStableMs)} scroll {F(ScrollMs)} zoom {F(ZoomMs)} peak {F(PeakWorkingSetMiB)} MiB{(Error is null ? "" : " ERROR " + Error)}";

    private static string F(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
}

internal static class Summary
{
    public static string Build(List<RunResult> results, Options options)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Interactive latency summary");
        sb.AppendLine();
        sb.AppendLine($"Measured {DateTime.Now:yyyy-MM-dd HH:mm}. Window 1600 by 1000 at 0,0. Capture every {FrameCapture.IntervalMs} ms; stable means {FrameCapture.StableMs} ms without a pixel change. Warmup runs are excluded from statistics but listed in results.csv. All samples retained.");
        sb.AppendLine();
        foreach (string doc in results.Select(r => r.Document).Distinct())
        {
            sb.AppendLine($"## {doc}");
            sb.AppendLine();
            sb.AppendLine("| Version | n | First paint mean | First paint median | Painted mean | Painted median | Scroll mean | Scroll median | Zoom mean | Zoom median | Peak WS mean MiB | Errors |");
            sb.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
            foreach (var v in options.Versions)
            {
                var rows = results.Where(r => r.Document == doc && r.Version == v.Label && !r.Warm && r.Error is null).ToList();
                int errors = results.Count(r => r.Document == doc && r.Version == v.Label && !r.Warm && r.Error is not null);
                if (rows.Count == 0) { sb.AppendLine($"| {v.Label} | 0 | | | | | | | | | | {errors} |"); continue; }
                sb.AppendLine($"| {v.Label} | {rows.Count} | {Mean(rows, r => r.FirstPaintMs)} | {Median(rows, r => r.FirstPaintMs)} | {Mean(rows, r => r.PaintStableMs)} | {Median(rows, r => r.PaintStableMs)} | {Mean(rows, r => r.ScrollMs)} | {Median(rows, r => r.ScrollMs)} | {Mean(rows, r => r.ZoomMs)} | {Median(rows, r => r.ZoomMs)} | {Mean(rows, r => r.PeakWorkingSetMiB)} | {errors} |");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Mean(List<RunResult> rows, Func<RunResult, double> f) =>
        rows.Average(f).ToString("0.0", CultureInfo.InvariantCulture);

    private static string Median(List<RunResult> rows, Func<RunResult, double> f)
    {
        var s = rows.Select(f).OrderBy(x => x).ToList();
        double m = s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
        return m.ToString("0.0", CultureInfo.InvariantCulture);
    }
}

internal sealed class WorkingSetSampler
{
    private readonly Process _process;
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _peak;
    private long _current;

    public WorkingSetSampler(Process process)
    {
        _process = process;
        _thread = new Thread(Run) { IsBackground = true, Name = "ws-sampler" };
        _thread.Start();
    }

    public double PeakMiB => _peak / 1048576.0;
    public double CurrentMiB => _current / 1048576.0;

    private void Run()
    {
        while (!_stop)
        {
            try
            {
                _process.Refresh();
                if (_process.HasExited) break;
                long ws = _process.WorkingSet64;
                _current = ws;
                if (ws > _peak) _peak = ws;
            }
            catch { break; }
            Thread.Sleep(20);
        }
    }

    public void Stop() { _stop = true; _thread.Join(500); }
}

internal readonly record struct StableResult(double FirstContentMs, double StableMs);

/// <summary>
/// Captures the window client area and reduces each frame to a 64-bit hash plus a content
/// score (variance over the central region). Stability is the last change time once no change
/// has been seen for the stable window.
/// </summary>
internal sealed class FrameCapture
{
    public const int IntervalMs = 33;
    public const int StableMs = 700;

    private readonly IntPtr _hwnd;
    private Bitmap? _last;
    private ulong _lastHash;

    public FrameCapture(IntPtr hwnd) { _hwnd = hwnd; }

    public StableResult WaitForContentThenStable(Stopwatch clock, int timeoutMs)
    {
        double deadline = clock.Elapsed.TotalMilliseconds + timeoutMs;
        double firstContent = -1;
        double lastChange = clock.Elapsed.TotalMilliseconds;
        while (clock.Elapsed.TotalMilliseconds < deadline)
        {
            var (hash, content) = Grab();
            double now = clock.Elapsed.TotalMilliseconds;
            if (hash != _lastHash) { lastChange = now; _lastHash = hash; }
            if (firstContent < 0 && content) firstContent = now;
            if (content && firstContent >= 0 && now - lastChange >= StableMs)
                return new StableResult(firstContent, lastChange);
            Thread.Sleep(IntervalMs);
        }
        throw new TimeoutException("Page did not reach a stable painted state.");
    }

    public StableResult WaitForStable(Stopwatch clock, int timeoutMs, bool requireChange = true)
    {
        double deadline = clock.Elapsed.TotalMilliseconds + timeoutMs;
        double lastChange = clock.Elapsed.TotalMilliseconds;
        // Require at least one change so a key that has not yet been processed is not read as stable.
        bool changed = false;
        double began = lastChange;
        while (clock.Elapsed.TotalMilliseconds < deadline)
        {
            var (hash, _) = Grab();
            double now = clock.Elapsed.TotalMilliseconds;
            if (hash != _lastHash) { lastChange = now; _lastHash = hash; changed = true; }
            if ((changed || !requireChange) && now - lastChange >= StableMs) return new StableResult(began, lastChange);
            Thread.Sleep(IntervalMs);
        }
        throw new TimeoutException(requireChange
            ? "View did not change and reach a stable state."
            : "View did not reach a stable state.");
    }

    public void Save(string path)
    {
        if (_last is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _last.Save(path, ImageFormat.Png);
    }

    private unsafe (ulong Hash, bool Content) Grab()
    {
        Native.GetClientRect(_hwnd, out var rc);
        var origin = new Native.POINT { X = 0, Y = 0 };
        Native.ClientToScreen(_hwnd, ref origin);
        int w = Math.Max(1, rc.Right - rc.Left), h = Math.Max(1, rc.Bottom - rc.Top);
        if (_last is null || _last.Width != w || _last.Height != h)
        {
            _last?.Dispose();
            _last = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        }
        using (var g = Graphics.FromImage(_last))
            g.CopyFromScreen(origin.X, origin.Y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);

        return Signature(_last);
    }

    internal static int VerifyContentFrame(string path, string expected)
    {
        if (expected is not ("present" or "blank"))
            throw new ArgumentException("Expected content must be present or blank.");
        using var input = new Bitmap(path);
        using var bitmap = new Bitmap(input.Width, input.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap)) graphics.DrawImageUnscaled(input, 0, 0);
        bool content = Signature(bitmap).Content;
        bool matches = content == (expected == "present");
        Console.WriteLine($"{(matches ? "PASS" : "FAIL")} page content: {content}; expected {expected}; {path}");
        return matches ? 0 : 1;
    }

    private static unsafe (ulong Hash, bool Content) Signature(Bitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        var data = bitmap.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            ulong hash = 1469598103934665603UL;
            long sum = 0, sumSq = 0; int n = 0;
            int x0 = w * 2 / 5, x1 = w * 3 / 5, y0 = h * 3 / 10, y1 = h * 7 / 10;
            byte* basePtr = (byte*)data.Scan0;
            for (int y = 0; y < h; y += 2)
            {
                uint* row = (uint*)(basePtr + y * data.Stride);
                for (int x = 0; x < w; x += 2)
                {
                    uint p = row[x] & 0x00FFFFFFu;
                    hash = (hash ^ p) * 1099511628211UL;
                    if (x >= x0 && x < x1 && y >= y0 && y < y1)
                    {
                        int lum = (int)(((p >> 16) & 0xFF) * 299 + ((p >> 8) & 0xFF) * 587 + (p & 0xFF) * 114) / 1000;
                        sum += lum; sumSq += (long)lum * lum; n++;
                    }
                }
            }
            double mean = n > 0 ? (double)sum / n : 0;
            double variance = n > 0 ? (double)sumSq / n - mean * mean : 0;
            // Stay inside the page for the five gate fixtures at the fixed window size.
            // Including its white edge against the dark surround falsely accepts blank placeholders.
            return (hash, variance > 40);
        }
        finally { bitmap.UnlockBits(data); }
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hWnd, int x, int y, int w, int h, bool repaint);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public const byte VK_CONTROL = 0x11, VK_NEXT = 0x22, VK_HOME = 0x24, VK_0 = 0x30, VK_OEM_PLUS = 0xBB;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static void Tap(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        Thread.Sleep(10);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static void Chord(byte modifier, byte vk)
    {
        keybd_event(modifier, 0, 0, UIntPtr.Zero);
        Thread.Sleep(10);
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        Thread.Sleep(10);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(10);
        keybd_event(modifier, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}
