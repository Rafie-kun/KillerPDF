using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static DocumentStateSmoke;

internal static class OcrOutputSmoke
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static int Run(Window owner, string folder, bool convert, bool cancel = false) => FormWorkflowFailureSmoke.WithCleanup(owner, () =>
    {
        string source = Path.Combine(folder, "source.pdf"), output = Path.Combine(folder, "output.pdf");
        byte[] original = Fixture(owner.GetType().Assembly, folder);
        File.WriteAllBytes(source, original);
        owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [source]);
        owner.Opacity = 1; owner.Activate();
        Wait(() => Descendants<TextBox>(owner).Any(box => box.Text == "Original"), "OCR source rendered");
        Descendants<TextBox>(owner).Single(box => box.Text == "Original").Text = "CURRENT MARKER";
        object viewer = owner.GetType().GetProperty("ActiveViewer", Members)!.GetValue(owner)!;
        object Get(object target, string name) => target.GetType().GetProperty(name, Members)?.GetValue(target)
            ?? target.GetType().GetField(name, Members)!.GetValue(target)!;
        ((IDictionary)Get(owner, "_pageRotations"))[0] = 90;
        string reference = Path.Combine(folder, "reference.pdf");
        owner.GetType().Assembly.GetType("KillerPDF.Features.IOcrHost", true)!.GetMethod("SaveDocumentTo")!
            .Invoke(owner, [reference]);
        string current = (string)Get(owner, "_currentFile");
        byte[] working = File.ReadAllBytes(current);
        int undo = ((ICollection)Get(viewer, "_undoStack")).Count;
        bool complete = false;
        bool canceled = false;
        Exception? failure = null;
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            if (cancel)
            {
                var active = (CancellationTokenSource?)owner.GetType().GetField("_busyCts", Members)!.GetValue(owner);
                if (!canceled && active is not null) { active.Cancel(); canceled = true; }
                if (canceled && active is null) complete = true;
            }
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (!seen.Add(dialog)) continue;
                string text = string.Join(" ", Descendants<TextBlock>(dialog).Select(block => block.Text));
                Console.WriteLine("CONTROL OCR dialog: " + text);
                if (dialog.GetType().FullName == "KillerPDF.Controls.FileDialog")
                {
                    Descendants<TextBox>(dialog).Single(box => box.Name == "FileNameBox").Text = output;
                    Descendants<Button>(dialog).Single(button => button.Name == "AcceptButton")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else
                {
                    if (text.Contains("failed", StringComparison.OrdinalIgnoreCase))
                        failure = new InvalidOperationException(text);
                    complete |= File.Exists(output) && text.Contains(output, StringComparison.OrdinalIgnoreCase);
                    Button? accept = Descendants<Button>(dialog).FirstOrDefault(button => button.IsDefault);
                    if (accept is not null) accept.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                }
            }
        };
        timer.Start();
        try
        {
            owner.GetType().GetMethod(convert ? "ConvertScanToText" : "MakeSearchablePdf", Members)!.Invoke(owner, null);
            var watch = Stopwatch.StartNew();
            while (!complete && failure is null)
            {
                Pump();
                if (watch.Elapsed > TimeSpan.FromSeconds(120)) throw new TimeoutException("Native OCR output did not complete.");
                Thread.Sleep(10);
            }
            Pump();
            if (failure is not null) throw failure;
        }
        finally { timer.Stop(); }
        if (!Dirty(owner) || !working.AsSpan().SequenceEqual(File.ReadAllBytes(current))
            || !original.AsSpan().SequenceEqual(File.ReadAllBytes(source))
            || ((ICollection)Get(viewer, "_undoStack")).Count != undo
            || !Equals(((IDictionary)Get(viewer, "_formTextValues"))["answer"], "CURRENT MARKER"))
            throw new InvalidOperationException("OCR output changed live document state or history.");
        if (cancel)
        {
            if (!canceled || File.Exists(output)) throw new InvalidOperationException("Canceled OCR published output or missed cancellation.");
            Console.WriteLine("PASS actual OCR cancellation publishes no output and preserves pending values, bytes and history.");
            return 0;
        }
        if (!ReadText(output, 1).Contains("NATIVE CONTROL", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The scanned control page has no recognized text in the output.");
        string executable = Path.ChangeExtension(owner.GetType().Assembly.Location, ".exe");
        foreach (string pdf in new[] { reference, output })
            if (CliOutputSmoke.Command(executable, "--to-image", pdf, folder, "--dpi", "72") != 0)
                throw new InvalidOperationException("OCR output comparison could not render.");
        if (!File.ReadAllBytes(Path.Combine(folder, "reference-page-001.png")).AsSpan()
            .SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "output-page-001.png"))))
            throw new InvalidOperationException("OCR output changed the displayed snapshot pixels or orientation.");
        VerifyNativeRecognition(owner.GetType().Assembly);
        Console.WriteLine("PASS native " + (convert ? "Convert Scan to Text" : "Make Searchable PDF")
            + " keeps pending form text, rotated snapshot pixels, original/working bytes and history.");
        return 0;
    }, dismissDuringRun: false);

    private static byte[] Fixture(Assembly app, string folder)
    {
        string forms = Path.Combine(folder, "form-base.pdf"), scan = Path.Combine(folder, "scan-base.pdf");
        File.WriteAllBytes(forms, FormWorkflowFailureSmoke.Fixture());
        Type integration = app.GetType("KillerPDF.Services.PdfEngineIntegration", true)!;
        Type raster = integration.GetNestedType("RasterPage", BindingFlags.NonPublic)!;
        Array pages = Array.CreateInstance(raster, 1);
        pages.SetValue(raster.GetConstructors().Single(constructor => constructor.GetParameters().Length == 8)
            .Invoke([900, 150, 450d, 75d, new ReadOnlyMemory<byte>(ControlPixels()), default(ReadOnlyMemory<byte>), false, false]), 0);
        File.WriteAllBytes(scan, (byte[])integration.GetMethod("CreateRasterDocument", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [pages])!);
        return (byte[])integration.GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.Name == "MergeFiles" && method.GetParameters().Length == 1)
            .Invoke(null, [new[] { forms, scan }])!;
    }

    private static byte[] ControlPixels()
    {
        const int width = 900, height = 150;
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            drawing.DrawText(new FormattedText("OCR NATIVE CONTROL", System.Globalization.CultureInfo.GetCultureInfo("en-US"),
                FlowDirection.LeftToRight, new Typeface("Arial"), 44, Brushes.Black, 1), new Point(30, 40));
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        byte[] pixels = new byte[width * height * 4];
        image.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    private static void VerifyNativeRecognition(Assembly app)
    {
        Type service = app.GetType("KillerPDF.Services.OcrService", true)!;
        using var ocr = (IDisposable)service.GetConstructors().Single().Invoke([null, "eng", null]);
        object result = service.GetMethod("RecognizeBgra")!.Invoke(ocr,
            [new ReadOnlyMemory<byte>(ControlPixels()), 900, 150, null, CancellationToken.None])!;
        string text = (string)result.GetType().GetProperty("Text")!.GetValue(result)!;
        Console.WriteLine("CONTROL native OCR recognized: " + text);
        if (!text.Contains("NATIVE CONTROL", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The actual OCR model did not recognize the control image.");
    }
}
