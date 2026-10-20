using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

internal static class WatchedFolderClearDataSmoke
{
    private const BindingFlags InternalStatic = BindingFlags.Static | BindingFlags.NonPublic;

    internal static void Run(Assembly assembly, string folder)
    {
        if (!Environment.GetCommandLineArgs().Contains("--startup-smoke") || Application.Current is null)
            throw new InvalidOperationException("An initialized, isolated WPF smoke host is required.");
        Type paths = assembly.GetType("KillerPDF.Services.AppDataPaths", true)!;
        string? isolatedRoot = (string?)paths.GetProperty("StartupSmokeRoot", InternalStatic)!.GetValue(null);
        if (string.IsNullOrWhiteSpace(isolatedRoot)) throw new InvalidOperationException("Startup smoke data isolation is missing.");
        folder = Path.GetFullPath(folder);
        string evidenceRoot = Path.GetDirectoryName(folder)!;
        if (Directory.Exists(folder) || !Within(isolatedRoot, evidenceRoot) || Within(folder, isolatedRoot))
            throw new InvalidOperationException("Use a fresh evidence folder beside the isolated app data folder.");
        string userRoot = (string)paths.GetProperty("UserRoot", InternalStatic)!.GetValue(null)!;
        if (!string.Equals(Path.GetFullPath(userRoot), Path.GetFullPath(isolatedRoot), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Clear data would access a non-isolated user data folder.");
        Type appType = assembly.GetType("KillerPDF.App", true)!;
        string appTemporary = (string)appType.GetProperty("TempDir", InternalStatic)!.GetValue(null)!;
        if (!Within(appTemporary, evidenceRoot))
            throw new InvalidOperationException("Set TEMP and TMP before App initialization so its session cache stays inside the smoke evidence folder.");
        Directory.CreateDirectory(folder);
        string temporary = Path.Combine(folder, "temp");
        string input = Path.Combine(folder, "input"), output = Path.Combine(folder, "output");
        Directory.CreateDirectory(temporary); Directory.CreateDirectory(input); Directory.CreateDirectory(output);
        byte[] source = Fixture("CLEAR DATA SOURCE"), existing = Fixture("EXISTING OUTPUT MUST REMAIN");
        string sourceFile = Path.Combine(input, "sample.pdf"), outputFile = Path.Combine(output, "sample.pdf");
        string existingFile = Path.Combine(output, "existing.pdf");
        File.WriteAllBytes(sourceFile, source); File.WriteAllBytes(existingFile, existing);
        string? oldTemp = Environment.GetEnvironmentVariable("TEMP"), oldTmp = Environment.GetEnvironmentVariable("TMP");
        object? service = null, fresh = null;
        var issues = new List<string>();
        try
        {
            Environment.SetEnvironmentVariable("TEMP", temporary);
            Environment.SetEnvironmentVariable("TMP", temporary);
            if (!Within(Path.GetTempPath(), folder)) throw new InvalidOperationException("Legacy temporary-file cleanup is not isolated.");
            MethodInfo getService = appType.GetMethod("GetFolderAutomation", InternalStatic)!;
            service = getService.Invoke(null, null)!;
            Type serviceType = service.GetType();
            string macro = JsonSerializer.Serialize(new { version = 1, name = "Clear data smoke", steps = new[] { new { operation = "save" } } });
            object options = Activator.CreateInstance(assembly.GetType("KillerPDF.Services.WatchedFolderOptions", true)!,
                [input, output, "*.pdf", macro, true])!;
            serviceType.GetMethod("Configure")!.Invoke(service, [options]);
            serviceType.GetMethod("Resume")!.Invoke(service, null);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Task observed = (Task)serviceType.GetMethod("ReconcileAsync")!.Invoke(service, [cancellation.Token])!;
            Wait(observed, TimeSpan.FromSeconds(10));
            object before = Snapshot(service);
            File.WriteAllText(Path.Combine(folder, "before-clear.json"), JsonSerializer.Serialize(before, before.GetType()));
            string journal = Path.Combine(isolatedRoot, "automatic-folders", "queue.json");
            if (!File.Exists(journal) || Value<bool>(before, "IsPaused"))
                throw new InvalidOperationException("The clear-data fixture did not start a durable automatic-folder setup.");
            object? result = appType.GetMethod("ClearAllData", InternalStatic)!.Invoke(null, null);
            if (result is false) issues.Add("ClearAllData did not report successful cleanup.");
            object after = Snapshot(service);
            File.WriteAllText(Path.Combine(folder, "after-clear.json"), JsonSerializer.Serialize(after, after.GetType()));
            if (!Value<bool>(after, "IsPaused")) issues.Add("ClearAllData left the original folder service running.");
            var quiet = Stopwatch.StartNew();
            while (quiet.Elapsed < TimeSpan.FromSeconds(3)) Pump();
            if (File.Exists(journal)) issues.Add("The automatic-folder journal remained or was recreated after clear.");
            if (File.Exists(outputFile)) issues.Add("The cleared automatic-folder setup published a PDF.");
            if (!File.ReadAllBytes(sourceFile).AsSpan().SequenceEqual(source)) issues.Add("Clear data changed the source PDF.");
            if (!File.ReadAllBytes(existingFile).AsSpan().SequenceEqual(existing)) issues.Add("Clear data changed an existing output PDF.");
            if (Value<bool>(after, "IsPaused"))
            {
                try
                {
                    serviceType.GetMethod("Resume")!.Invoke(service, null);
                    issues.Add("The cleared service can still be resumed instead of remaining disposed.");
                    serviceType.GetMethod("Pause")!.Invoke(service, null);
                }
                catch (TargetInvocationException error) when (error.InnerException is ObjectDisposedException) { }
            }
            fresh = getService.Invoke(null, null)!;
            object empty = Snapshot(fresh);
            File.WriteAllText(Path.Combine(folder, "fresh-service.json"), JsonSerializer.Serialize(empty, empty.GetType()));
            if (ReferenceEquals(service, fresh) || Value<object?>(empty, "Options") is not null
                || !Value<bool>(empty, "IsPaused") || Value<string?>(empty, "ErrorCode") is not null)
                issues.Add("Opening automatic folders after clear did not create a fresh paused service with no saved setup.");
            if (issues.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, issues));
            const string passed = "PASS clear data stops and disposes automatic folders, removes saved setup without journal recreation or later output, preserves source and existing PDFs, and opens a fresh paused service.";
            File.WriteAllText(Path.Combine(folder, "result.txt"), passed); Console.WriteLine(passed);
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(folder, "failure.txt"), error.ToString());
            throw;
        }
        finally
        {
            if (fresh is IDisposable newService) newService.Dispose();
            if (service is IDisposable original && !ReferenceEquals(service, fresh)) original.Dispose();
            Environment.SetEnvironmentVariable("TEMP", oldTemp);
            Environment.SetEnvironmentVariable("TMP", oldTmp);
        }
    }

    private static object Snapshot(object service) => service.GetType().GetMethod("GetSnapshot")!.Invoke(service, null)!;
    private static T Value<T>(object instance, string name) => (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;
    private static bool Within(string path, string parent) => (Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar)
        .StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void Wait(Task task, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.Elapsed < limit) Pump();
        if (!task.IsCompleted) throw new TimeoutException("The clear-data fixture could not finish its first folder scan.");
        task.GetAwaiter().GetResult();
    }
    private static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        Thread.Sleep(50);
    }
    private static byte[] Fixture(string label)
    {
        string content = $"BT /F1 16 Tf 24 160 Td ({label}) Tj ET\n";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 600 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {content.Length} >>\nstream\n{content}endstream"];
        using var stream = new MemoryStream();
        void Write(string text) => stream.Write(Encoding.ASCII.GetBytes(text));
        Write("%PDF-1.7\n");
        var offsets = new List<long> { 0 };
        for (int index = 0; index < objects.Length; index++)
        { offsets.Add(stream.Position); Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        long xref = stream.Position;
        Write($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (long offset in offsets.Skip(1)) Write(offset.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return stream.ToArray();
    }
}
