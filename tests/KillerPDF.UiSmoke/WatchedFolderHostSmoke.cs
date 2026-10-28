using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class WatchedFolderHostSmoke
{
    private const BindingFlags InternalStatic = BindingFlags.Static | BindingFlags.NonPublic;
    private const string OcrLabel = "AUTOMATIC FOLDER OCR";

    internal static void Run(Assembly assembly, string folder)
    {
        if (!Environment.GetCommandLineArgs().Contains("--startup-smoke") || Application.Current is null)
            throw new InvalidOperationException("An initialized, isolated WPF smoke host is required.");
        Type paths = assembly.GetType("KillerPDF.Services.AppDataPaths", true)!;
        string? isolatedRoot = (string?)paths.GetProperty("StartupSmokeRoot", InternalStatic)!.GetValue(null);
        if (string.IsNullOrWhiteSpace(isolatedRoot)) throw new InvalidOperationException("Startup smoke data isolation is missing.");
        string models = (string)assembly.GetType("KillerPDF.Services.OcrNativeBootstrap", true)!
            .GetProperty("TessDataDir", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        if (!Within(models, isolatedRoot)) throw new InvalidOperationException("OCR models are outside the isolated smoke data directory.");
        if (Directory.Exists(folder)) throw new IOException("Use a fresh host-smoke directory.");
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, "temp");
        Directory.CreateDirectory(temporary);
        string? oldTemp = Environment.GetEnvironmentVariable("TEMP"), oldTmp = Environment.GetEnvironmentVariable("TMP");
        var report = new List<string>();
        try
        {
            Environment.SetEnvironmentVariable("TEMP", temporary);
            Environment.SetEnvironmentVariable("TMP", temporary);
            if (!Within(Path.GetTempPath(), folder)) throw new InvalidOperationException("Temporary PDF isolation was not applied.");
            Assembly engine = Assembly.Load("KillerPdf.Engine");
            const string originalLabel = "HOST WATERMARK SOURCE";
            const string watermarkLabel = "WATCHED COPY VERIFIED";
            using (var run = new Scenario(assembly, Path.Combine(folder, "watermark"), TextPdf(originalLabel),
                Macro("Watermark smoke", "watermark", new()
                { ["text"] = watermarkLabel, ["fontSize"] = "28", ["opacity"] = "0.8", ["rotation"] = "0" })))
            {
                object job = run.ReconcileUntilTerminal();
                run.AssertCompleted(job);
                string text = ReadText(engine, File.ReadAllBytes(run.OutputFile));
                RequireText(text, originalLabel, "Watermark output lost the original text.");
                RequireText(text, watermarkLabel, "Watermark output is missing the expected watermark text.");
                File.WriteAllText(Path.Combine(run.Folder, "extracted.txt"), text);
                report.Add("PASS real watermark dispatcher saves a readable PDF containing the source and watermark text; original bytes are unchanged.");
            }

            byte[] scan = ImagePdf();
            if (!string.IsNullOrWhiteSpace(ReadText(engine, scan)))
                throw new InvalidOperationException("The OCR fixture must contain an image without an existing text layer.");
            InstallEnglishFixture(models, folder);
            using (var run = new Scenario(assembly, Path.Combine(folder, "english-ocr"), scan,
                Macro("English OCR smoke", "ocr", new() { ["language"] = "eng", ["formAware"] = "false" })))
            {
                object job = run.ReconcileUntilTerminal();
                run.AssertCompleted(job);
                string text = ReadText(engine, File.ReadAllBytes(run.OutputFile));
                File.WriteAllText(Path.Combine(run.Folder, "extracted.txt"), text);
                RequireText(text, OcrLabel, "Installed English OCR did not add the expected searchable text.");
                report.Add("PASS real English OCR with an isolated copy of an installed model turns an image-only PDF into a readable PDF with the expected searchable text; original bytes are unchanged.");
            }

            Dictionary<string, string> modelHashes = HashFiles(models);
            if (modelHashes.Count == 0) throw new InvalidOperationException("The isolated English model fixture is missing.");
            using (var run = new Scenario(assembly, Path.Combine(folder, "missing-language"), scan,
                Macro("Missing language smoke", "ocr", new() { ["language"] = "missing_smoke", ["formAware"] = "false" })))
            {
                object job = run.ReconcileUntilTerminal();
                if (Value<object>(job, "Status").ToString() != "Failed" || string.IsNullOrWhiteSpace(Value<string?>(job, "ErrorCode")))
                    throw new InvalidOperationException("The unavailable OCR language did not produce a failed public job with a reason.");
                if (!Value<bool>(run.Snapshot(), "IsPaused")) throw new InvalidOperationException("The missing-language failure did not honor pause on error.");
                if (Directory.EnumerateFileSystemEntries(run.OutputDirectory).Any())
                    throw new InvalidOperationException("The unavailable OCR language created an output or temporary output.");
                Dictionary<string, string> after = HashFiles(models);
                if (modelHashes.Count != after.Count || modelHashes.Any(pair => !after.TryGetValue(pair.Key, out string? hash) || hash != pair.Value))
                    throw new InvalidOperationException("The unavailable language changed the isolated model directory.");
                VerifyFailureRow(assembly, engine, run);
                report.Add("PASS unavailable OCR language appears as a localized failed job, pauses processing, writes no output and leaves all model-file hashes unchanged.");
            }
            File.WriteAllLines(Path.Combine(folder, "result.txt"), report);
            Console.WriteLine(string.Join(Environment.NewLine, report));
        }
        catch (Exception error)
        {
            File.WriteAllLines(Path.Combine(folder, "partial-results.txt"), report);
            File.WriteAllText(Path.Combine(folder, "failure.txt"), error.ToString());
            throw;
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEMP", oldTemp);
            Environment.SetEnvironmentVariable("TMP", oldTmp);
        }
    }

    private static void InstallEnglishFixture(string models, string folder)
    {
        string? supplied = Environment.GetEnvironmentVariable("KILLERPDF_SMOKE_ENGLISH_MODEL");
        if (string.IsNullOrWhiteSpace(supplied))
            throw new InvalidOperationException("Set KILLERPDF_SMOKE_ENGLISH_MODEL to an existing installed eng.traineddata fixture. This smoke does not download models.");
        string source = Path.GetFullPath(supplied);
        if (!string.Equals(Path.GetFileName(source), "eng.traineddata", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The English model fixture must be named eng.traineddata.");
        Directory.CreateDirectory(models);
        string destination = Path.Combine(models, "eng.traineddata");
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is <= 0 or > 128 * 1024 * 1024)
            throw new InvalidOperationException("The installed English model fixture has an unexpected size.");
        string hash = Convert.ToHexString(SHA256.HashData(input));
        input.Position = 0;
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
        using var copied = File.OpenRead(destination);
        if (Convert.ToHexString(SHA256.HashData(copied)) != hash)
            throw new InvalidOperationException("The isolated English model copy failed its hash check.");
        File.WriteAllText(Path.Combine(folder, "english-model-fixture.txt"), "Copied installed English model without downloading.\nSource: "
            + source + "\nSHA256: " + hash);
    }

    private sealed class Scenario : IDisposable
    {
        private readonly Type _serviceType;
        private readonly byte[] _source;
        private Task _active = Task.CompletedTask;
        internal object Service { get; }
        internal string Folder { get; }
        internal string MacroJson { get; }
        internal string InputFile { get; }
        internal string OutputFile { get; }
        internal string OutputDirectory { get; }

        internal Scenario(Assembly assembly, string folder, byte[] source, string macroJson)
        {
            Folder = folder; MacroJson = macroJson; _source = source;
            string input = Path.Combine(folder, "input"), storage = Path.Combine(folder, "queue");
            OutputDirectory = Path.Combine(folder, "output");
            Directory.CreateDirectory(input); Directory.CreateDirectory(OutputDirectory);
            InputFile = Path.Combine(input, "sample.pdf"); OutputFile = Path.Combine(OutputDirectory, "sample.pdf");
            File.WriteAllBytes(InputFile, source);
            object dispatcher = assembly.GetType("KillerPDF.Services.WatchedFolderHost", true)!
                .GetMethod("CreateDispatcher", InternalStatic)!.Invoke(null, [storage])!;
            _serviceType = assembly.GetType("KillerPDF.Services.WatchedFolderService", true)!;
            Service = Activator.CreateInstance(_serviceType, [storage, dispatcher, null, null])!;
            object options = Activator.CreateInstance(assembly.GetType("KillerPDF.Services.WatchedFolderOptions", true)!,
                [input, OutputDirectory, "*.pdf", macroJson, true])!;
            try
            {
                _serviceType.GetMethod("Configure")!.Invoke(Service, [options]);
                _serviceType.GetMethod("Resume")!.Invoke(Service, null);
            }
            catch { ((IDisposable)Service).Dispose(); throw; }
        }

        internal object Snapshot() => _serviceType.GetMethod("GetSnapshot")!.Invoke(Service, null)!;

        internal object ReconcileUntilTerminal()
        {
            var clock = Stopwatch.StartNew();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while (clock.Elapsed < TimeSpan.FromSeconds(90))
            {
                _active = (Task)_serviceType.GetMethod("ReconcileAsync")!.Invoke(Service, [cancellation.Token])!;
                while (!_active.IsCompleted && clock.Elapsed < TimeSpan.FromSeconds(95)) PumpDelay();
                if (!_active.IsCompleted)
                {
                    cancellation.Cancel();
                    throw new TimeoutException("The real host operation did not stop within the 95-second smoke limit. Native OCR or rendering may be blocked.");
                }
                _active.GetAwaiter().GetResult();
                object snapshot = Snapshot();
                File.WriteAllText(Path.Combine(Folder, "snapshot.json"), JsonSerializer.Serialize(snapshot, snapshot.GetType()));
                object[] jobs = Value<IEnumerable>(snapshot, "Jobs").Cast<object>().ToArray();
                if (jobs.Length > 1) throw new InvalidOperationException("One source PDF produced multiple jobs.");
                if (jobs.Length == 1 && Value<object>(jobs[0], "Status").ToString() is "Completed" or "Failed")
                {
                    if (!File.ReadAllBytes(InputFile).AsSpan().SequenceEqual(_source))
                        throw new InvalidOperationException("The host changed the original input PDF.");
                    return jobs[0];
                }
                if (Value<string?>(snapshot, "ErrorCode") is string error)
                    throw new InvalidOperationException("The host queue could not proceed: " + error);
                PumpDelay();
            }
            cancellation.Cancel();
            throw new TimeoutException("The real host operation did not reach a terminal public job within 90 seconds.");
        }

        internal void AssertCompleted(object job)
        {
            if (Value<object>(job, "Status").ToString() != "Completed")
                throw new InvalidOperationException("The real host operation failed: " + Value<string?>(job, "ErrorCode"));
            if (!File.Exists(OutputFile) || Directory.GetFiles(OutputDirectory, "*.pdf").Length != 1
                || !string.Equals(Value<string>(job, "OutputPath"), OutputFile, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The real host did not publish exactly the expected PDF copy.");
        }

        public void Dispose()
        {
            if (_active.IsCompleted) ((IDisposable)Service).Dispose();
            else
                _ = _active.ContinueWith(_ => ((IDisposable)Service).Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static void VerifyFailureRow(Assembly assembly, Assembly engine, Scenario run)
    {
        Type macroType = engine.GetType("KillerPdf.Engine.Documents.PdfMacro", true)!;
        object macro = macroType.GetMethod("FromJson")!.Invoke(null, [run.MacroJson])!;
        Array macros = Array.CreateInstance(macroType, 1); macros.SetValue(macro, 0);
        Window owner = Application.Current.MainWindow ?? Application.Current.Windows.Cast<Window>().First();
        var dialog = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.WatchedFolderDialog", true)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [owner, run.Service, macros, 1d], null)!;
        try
        {
            dialog.WindowStartupLocation = WindowStartupLocation.Manual;
            dialog.Left = -10000; dialog.Top = -10000; dialog.ShowInTaskbar = false; dialog.ShowActivated = false;
            dialog.Show();
            var shown = Stopwatch.StartNew();
            while (!dialog.IsLoaded || dialog.Opacity < .99)
            {
                if (shown.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The failure results dialog did not become visible.");
                PumpDelay();
            }
            ListBox results = Descendants<ListBox>(dialog).Single(item => AutomationProperties.GetAutomationId(item) == "WatchResults");
            results.BringIntoView(); PumpDelay();
            string failed = (string)Application.Current.FindResource("Str_Watch_Failed");
            string visible = string.Join(Environment.NewLine, results.Items.Cast<object>().Select(item => item.ToString()));
            if (!results.IsVisible || !visible.Contains(Path.GetFileName(run.InputFile), StringComparison.Ordinal)
                || !visible.Contains(failed, StringComparison.Ordinal) || visible.Contains("Str_", StringComparison.Ordinal))
                throw new InvalidOperationException("The unavailable-language failure is not visible as a localized file result.");
            File.WriteAllText(Path.Combine(run.Folder, "visible-result.txt"), visible);
            dialog.UpdateLayout();
            var image = new RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth), (int)Math.Ceiling(dialog.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            image.Render(dialog);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image));
            using var file = File.Create(Path.Combine(run.Folder, "failed-language.png")); png.Save(file);
        }
        finally { dialog.Close(); }
    }

    private static string Macro(string name, string operation, Dictionary<string, string> settings) =>
        JsonSerializer.Serialize(new { version = 1, name, steps = new object[] { new { operation, settings }, new { operation = "save" } } });

    private static string ReadText(Assembly engine, byte[] bytes)
    {
        Type documentType = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!;
        object document = documentType.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(bytes)])!;
        try
        {
            Type readerType = engine.GetType("KillerPdf.Engine.Documents.PdfPageContentReader", true)!;
            object reader = Activator.CreateInstance(readerType, [document])!;
            if (Value<int>(reader, "PageCount") != 1) throw new InvalidOperationException("The output is not a readable one-page PDF.");
            object page = readerType.GetMethod("Read", [typeof(int), typeof(CancellationToken)])!.Invoke(reader, [0, CancellationToken.None])!;
            return Value<string>(page, "Text");
        }
        finally { (document as IDisposable)?.Dispose(); }
    }

    private static void RequireText(string actual, string expected, string failure)
    {
        string normalized = Regex.Replace(actual, @"\s+", " ").Trim();
        if (!normalized.Contains(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(failure + " Extracted text: " + normalized);
    }
    private static T Value<T>(object instance, string name) => (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;
    private static bool Within(string path, string parent) => (Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar)
        .StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static Dictionary<string, string> HashFiles(string folder) => Directory.Exists(folder)
        ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories).ToDictionary(file => Path.GetRelativePath(folder, file),
            file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), StringComparer.OrdinalIgnoreCase)
        : new(StringComparer.OrdinalIgnoreCase);
    private static void PumpDelay()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Thread.Sleep(50);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T item) yield return item;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static byte[] TextPdf(string label)
    {
        byte[] content = Encoding.ASCII.GetBytes($"BT /F1 16 Tf 24 160 Td ({label}) Tj ET\n");
        return Pdf([
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"), Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>"),
            Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"), StreamObject("", content)]);
    }
    private static byte[] ImagePdf()
    {
        const int width = 1200, height = 400;
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var text = new FormattedText(OcrLabel, CultureInfo.GetCultureInfo("en-US"), FlowDirection.LeftToRight,
                new Typeface("Arial"), 64, Brushes.Black, 1);
            drawing.DrawText(text, new Point(70, 160));
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
        byte[] bgra = new byte[width * height * 4], rgb = new byte[width * height * 3];
        image.CopyPixels(bgra, width * 4, 0);
        for (int pixel = 0; pixel < width * height; pixel++)
        { rgb[pixel * 3] = bgra[pixel * 4 + 2]; rgb[pixel * 3 + 1] = bgra[pixel * 4 + 1]; rgb[pixel * 3 + 2] = bgra[pixel * 4]; }
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(rgb);
        return Pdf([
            Ascii("<< /Type /Catalog /Pages 2 0 R >>"), Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 200] /Resources << /XObject << /Im1 4 0 R >> >> /Contents 5 0 R >>"),
            StreamObject($"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode", compressed.ToArray()),
            StreamObject("", Ascii("q 600 0 0 200 0 0 cm /Im1 Do Q\n"))]);
    }
    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);
    private static byte[] StreamObject(string dictionary, byte[] content)
    {
        using var stream = new MemoryStream();
        stream.Write(Ascii($"<< {dictionary} /Length {content.Length} >>\nstream\n"));
        stream.Write(content); stream.Write(Ascii("\nendstream")); return stream.ToArray();
    }
    private static byte[] Pdf(byte[][] objects)
    {
        using var stream = new MemoryStream(); stream.Write(Ascii("%PDF-1.7\n"));
        var offsets = new List<long> { 0 };
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(stream.Position); stream.Write(Ascii($"{index + 1} 0 obj\n"));
            stream.Write(objects[index]); stream.Write(Ascii("\nendobj\n"));
        }
        long xref = stream.Position;
        stream.Write(Ascii($"xref\n0 {offsets.Count}\n0000000000 65535 f \n"));
        foreach (long offset in offsets.Skip(1)) stream.Write(Ascii(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n"));
        stream.Write(Ascii($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        return stream.ToArray();
    }
}
