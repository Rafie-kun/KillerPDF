using System.Security.Cryptography;
using System.Text.Json;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;

if (args.Length != 3) throw new ArgumentException("Usage: EngineRecoveryRoundTrip CORPUS RECOVERY_SUMMARY_JSON OUTPUT_JSON");
string root = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[2]);
if (File.Exists(output)) throw new IOException("Use a new evidence output file.");
using JsonDocument summary = JsonDocument.Parse(File.ReadAllText(args[1]));
var records = new List<object>();
int failures = 0;
foreach (JsonElement file in summary.RootElement.GetProperty("recoveredFiles").EnumerateArray())
{
    string relative = file.GetString()!;
    byte[] source = File.ReadAllBytes(Path.Combine(root, relative));
    string hash = Convert.ToHexString(SHA256.HashData(source));
    try
    {
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(source);
        var page = PdfPageInformation.Read(document).Single();
        if (document.RecoveryDiagnostics.Count == 0 || page.Width != 612 || page.Height != 792)
            throw new InvalidOperationException("The expected intact page was not recovered.");
        string text = new PdfPageContentReader(document).Read(0).Text;
        if (!text.Contains("Hello PDF-world!", StringComparison.Ordinal))
            throw new InvalidOperationException("The source text was not preserved.");
        byte[] pixels = Pixels(document);
        if (pixels.All(value => value == 255)) throw new InvalidOperationException("The rendered page is blank.");
        Check(PdfDocument.Open(PdfDocumentWriter.Write(document)), "full rewrite");
        var incremental = new PdfIncrementalUpdateBuilder(document);
        incremental.AddObject(new KillerPdf.Engine.Objects.PdfInteger(42));
        byte[] saved = incremental.Build();
        if (!saved.AsSpan(0, source.Length).SequenceEqual(source))
            throw new InvalidOperationException("Incremental saving changed original source bytes.");
        Check(PdfDocument.OpenWithCompatibilityRecovery(saved), "incremental save");
        var editor = new PdfIncrementalPageEditor(document);
        editor.RotateClockwise(0).RotateClockwise(0).RotateClockwise(0).RotateClockwise(0);
        Check(PdfDocument.OpenWithCompatibilityRecovery(editor.Build()), "page editing");
        if (!SHA256.HashData(File.ReadAllBytes(Path.Combine(root, relative))).SequenceEqual(SHA256.HashData(source)))
            throw new InvalidOperationException("The original corpus file changed.");
        records.Add(new { file = relative, hash, status = "passed", text,
            width = page.Width, height = page.Height, pixelsSha256 = Convert.ToHexString(SHA256.HashData(pixels)),
            fullRewrite = true, incrementalSave = true, pageEdit = true, sourcePreserved = true });

        void Check(PdfDocument reopened, string operation)
        {
            if (reopened.RecoveryDiagnostics.Count != 0 || !Pixels(reopened).SequenceEqual(pixels)
                || new PdfPageContentReader(reopened).Read(0).Text != text)
                throw new InvalidOperationException(operation + " changed content or lost the repair.");
        }
    }
    catch (Exception error)
    {
        failures++;
        records.Add(new { file = relative, hash, status = "failed", error = error.ToString() });
    }
}
File.WriteAllText(output, JsonSerializer.Serialize(new { inputFiles = records.Count, failures, records },
    new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Recovery writer checks: {records.Count - failures} passed, {failures} failed.");
return records.Count > 0 && failures == 0 ? 0 : 1;

static byte[] Pixels(PdfDocument document) => new PdfPageRenderer(document).Render(0,
    new PdfRenderOptions(395, 512) { CacheResult = false }).Pixels.ToArray();
