using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

internal static class CliOutputSmoke
{
    internal static int Run(string appPath, string folder, bool merge)
    {
        Type builderType = Assembly.Load("KillerPdf.Engine")
            .GetType("KillerPdf.Engine.Authoring.PdfDocumentBuilder", true)!;
        ConstructorInfo constructor = builderType.GetConstructors().Single();
        dynamic builder = constructor.Invoke(constructor.GetParameters().Select(parameter => parameter.DefaultValue).ToArray());
        string source = Path.Combine(folder, "template.pdf");
        byte[] bytes = merge
            ? builder.AddBlankPage(200d, 300d).AddTextField(0, "Name", 20d, 20d, 120d, 24d).Build()
            : builder.AddBlankPage(200d, 300d).AddBlankPage(200d, 300d).Build();
        File.WriteAllBytes(source, bytes);
        string exe = Path.ChangeExtension(appPath, ".exe");
        if (merge)
        {
            string data = Path.Combine(folder, "records.csv");
            File.WriteAllText(data, "Name\nAda\n");
            int refused = Command(exe, "--data-merge", source, data, folder, "--name", "template.pdf", "--overwrite");
            if (refused == 0 || !bytes.AsSpan().SequenceEqual(File.ReadAllBytes(source)))
                throw new InvalidOperationException("Data merge overwrote its own template with --overwrite.");
            if (Command(exe, "--data-merge", source, data, folder, "--name", "letter.pdf") != 0
                || !File.Exists(Path.Combine(folder, "letter.pdf")))
                throw new InvalidOperationException("A separate data-merge output was refused.");
            Console.WriteLine("PASS CLI merge protects its input template while separate output succeeds.");
            return 0;
        }
        string output = Path.Combine(folder, "template-page-001.png");
        string second = Path.Combine(folder, "template-page-002.png");
        byte[] sentinel = Encoding.ASCII.GetBytes("existing image must survive");
        File.WriteAllBytes(output, sentinel);
        int denied = Command(exe, "--to-image", source, folder, "--dpi", "72");
        if (denied == 0 || !sentinel.AsSpan().SequenceEqual(File.ReadAllBytes(output)) || File.Exists(second))
            throw new InvalidOperationException("Image export overwrote an unapproved output or published before collision preflight.");
        using (var locked = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (Command(exe, "--to-image", source, folder, "--dpi", "72", "--overwrite") == 0)
                throw new InvalidOperationException("Image export reported success while the existing output was locked.");
        }
        if (!sentinel.AsSpan().SequenceEqual(File.ReadAllBytes(output)))
            throw new InvalidOperationException("A failed image replacement damaged the existing output.");
        if (Command(exe, "--to-image", source, folder, "--dpi", "72", "--overwrite") != 0
            || !File.ReadAllBytes(output).AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !File.Exists(second) || !bytes.AsSpan().SequenceEqual(File.ReadAllBytes(source)))
            throw new InvalidOperationException("Approved image export did not publish both complete images while preserving the input.");
        Console.WriteLine("PASS CLI images preflight actual outputs, preserve existing bytes on denial/failure and publish approved complete PNGs.");
        return 0;
    }

    internal static int RunPlates(string appPath, string folder)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        Type builderType = engine.GetType("KillerPdf.Engine.Authoring.PdfDocumentBuilder", true)!;
        ConstructorInfo constructor = builderType.GetConstructors().Single();
        dynamic builder = constructor.Invoke(constructor.GetParameters().Select(parameter => parameter.DefaultValue).ToArray());
        dynamic content = Activator.CreateInstance(engine.GetType("KillerPdf.Engine.Authoring.PdfContentStreamBuilder", true)!)!;
        object color = Activator.CreateInstance(engine.GetType("KillerPdf.Engine.Authoring.PdfCmykColor", true)!, 0d, 1d, 0d, 0d)!;
        Type spotType = engine.GetType("KillerPdf.Engine.Authoring.PdfSpotColor", true)!;
        int x = 0;
        foreach (string name in new[] { "Brand Red", "Brand/Red", "brand red" })
        {
            dynamic ink = Activator.CreateInstance(spotType, name, color)!;
            content.SetFillSpotColor(ink, 1d).Rectangle((double)x, 0d, 30d, 30d).Fill();
            x += 35;
        }
        string source = Path.Combine(folder, "inks.pdf");
        byte[] bytes = builder.AddPage(150d, 100d, content).Build();
        File.WriteAllBytes(source, bytes);
        string output = Path.Combine(folder, "plates");
        if (Command(Path.ChangeExtension(appPath, ".exe"), "--separations", source, output, "--dpi", "72") != 0)
            throw new InvalidOperationException("Separation export failed.");
        string[] spots = Directory.GetFiles(output, "*.png")
            .Where(path => Path.GetFileName(path).Contains("Brand-Red", StringComparison.OrdinalIgnoreCase)).ToArray();
        Console.WriteLine("CONTROL distinct spot outputs: " + string.Join(", ", spots.Select(Path.GetFileName)));
        if (spots.Length != 3 || spots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
            throw new InvalidOperationException("Distinct spot names collapsed to the same output file.");
        var before = spots.ToDictionary(path => path, File.ReadAllBytes);
        if (Command(Path.ChangeExtension(appPath, ".exe"), "--separations", source, output, "--dpi", "72") == 0
            || before.Any(pair => !pair.Value.AsSpan().SequenceEqual(File.ReadAllBytes(pair.Key))))
            throw new InvalidOperationException("Separation export replaced existing outputs without --overwrite.");
        Console.WriteLine("PASS punctuation and case-distinct inks receive unique paths and existing outputs require approval.");
        return 0;
    }

    internal static int Command(string exe, params string[] arguments)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { process.Kill(true); throw new TimeoutException("CLI output check timed out."); }
        Console.WriteLine("CONTROL " + arguments[0] + " exit=" + process.ExitCode + ": " + output.Result + error.Result);
        return process.ExitCode;
    }
}
