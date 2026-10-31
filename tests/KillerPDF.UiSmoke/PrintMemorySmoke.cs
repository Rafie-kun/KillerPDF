using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;

internal static class PrintMemorySmoke
{
    internal static int Run(Window owner, string appPath, string folder, int count, int copies = 1, bool subset = false) => FormWorkflowFailureSmoke.WithCleanup(owner, () =>
    {
        if (count is not (10 or 100 or 500)) throw new ArgumentOutOfRangeException(nameof(count));
        string source = Path.Combine(folder, "print-input.pdf");
        File.WriteAllBytes(source, Fixture(count));
        var start = new ProcessStartInfo(Path.ChangeExtension(appPath, ".exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "--print", source, "--printer", "KillerPDFRepair-20261030" }) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("--copies");
        start.ArgumentList.Add(copies.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (subset)
        {
            start.ArgumentList.Add("--pages");
            start.ArgumentList.Add("2,4,6");
        }
        start.Environment["TEMP"] = folder;
        start.Environment["TMP"] = folder;
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        var watch = Stopwatch.StartNew();
        long peak = 0;
        string? stopped = null;
        // An experiment guard protects guest headroom; it is not a product acceptance budget.
        const long guestGuard = 15L * 1024 * 1024 * 1024;
        using (var samples = new StreamWriter(Path.Combine(folder, "private-bytes.csv")))
        {
            samples.WriteLine("ElapsedMilliseconds,PrivateBytes,WorkingSetBytes");
            while (!process.WaitForExit(100))
            {
                process.Refresh();
                long bytes = process.PrivateMemorySize64;
                peak = Math.Max(peak, bytes);
                samples.WriteLine($"{watch.ElapsedMilliseconds},{bytes},{process.WorkingSet64}");
                if (bytes > guestGuard || watch.Elapsed > TimeSpan.FromSeconds(count == 500 ? 300 : 150))
                {
                    stopped = bytes > guestGuard ? "guest-memory-guard" : "time-guard";
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit();
                    break;
                }
            }
        }
        string transcript = output.Result + error.Result;
        File.WriteAllText(Path.Combine(folder, "cli-output.txt"), transcript);
        int selected = subset ? 3 : count;
        var result = new { SelectedPages = selected, Copies = copies, PeakSampledPrivateBytes = peak, ElapsedMilliseconds = watch.ElapsedMilliseconds,
            ExitCode = process.ExitCode, Stopped = stopped, GuestGuardBytes = guestGuard,
            ReportedComplete = stopped is null && process.ExitCode == 0 && transcript.Contains($"Sent {selected} pages x{copies}", StringComparison.Ordinal),
            RetainedBitmapCountBasis = "Record the tested implementation separately. This probe measures process private bytes, not heap object counts." };
        File.WriteAllText(Path.Combine(folder, "measurement.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(result));
        if (stopped is not null) return 3;
        if (!result.ReportedComplete) throw new InvalidOperationException("Print measurement did not complete: " + transcript);
        Console.WriteLine($"PASS {count}-page virtual print completed.");
        return 0;
    });

    private static byte[] Fixture(int count)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, count).Select(index => $"{4 + index * 2} 0 R"))}] /Count {count} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        for (int page = 0; page < count; page++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + page * 2} 0 R >>");
            string content = $"BT /F1 18 Tf 72 700 Td (Print page {page + 1}) Tj ET";
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string item in objects) { offsets.Add(text.Length); text.Append($"{offsets.Count} 0 obj\n{item}\nendobj\n"); }
        int xref = text.Length;
        text.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
