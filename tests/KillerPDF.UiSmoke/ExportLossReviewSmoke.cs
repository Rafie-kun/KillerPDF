using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class ExportLossReviewSmoke
{
    internal static int Run(Window owner, string folder, string scenario = "--verify-export-loss-review")
    {
        bool mixed = scenario == "--verify-export-loss-mixed", proceed = scenario == "--verify-export-loss-continue",
            lossless = scenario == "--verify-export-lossless";
        string source = Path.Combine(folder, "loss-source.pdf"), destination = Path.Combine(folder, lossless ? "export.json" : "export.txt");
        byte[] bytes = mixed || lossless ? Fixture(mixed) : TableExportSmoke.Fixture();
        File.WriteAllBytes(source, bytes);
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(bytes)])!;
        object report = engine.GetType("KillerPdf.Engine.Documents.PdfStructuredExport", true)!.GetMethod("InspectLosses")!
            .Invoke(null, [document, Enum.Parse(engine.GetType("KillerPdf.Engine.Documents.PdfStructuredExportFormat", true)!, lossless ? "Json" : "PlainText"),
                null, CancellationToken.None, Enum.Parse(engine.GetType("KillerPdf.Engine.Documents.PdfOfficeExportMode", true)!, "Editable")])!;
        object[] findings = ((System.Collections.IEnumerable)report.GetType().GetProperty("Findings")!.GetValue(report)!).Cast<object>().ToArray();
        string Code(object item) => (string)item.GetType().GetProperty("Code")!.GetValue(item)!;
        if (lossless ? findings.Length != 0 : !findings.Any(item => Code(item) == "VectorContentNotExported"))
            throw new InvalidOperationException("Public export-loss fixture control failed.");
        if (mixed && (!findings.Any(item => Code(item) == "ImageContentNotExported") || !findings.Any(item => Code(item) == "LinkContentNotExported")))
            throw new InvalidOperationException("Mixed fixture must independently report image, link and vector losses.");
        string[] expectedDetails = findings.Select(item => Code(item) == "ImageContentNotExported"
            ? "Image data is omitted or replaced with placeholders in this export."
            : (string)item.GetType().GetProperty("Message")!.GetValue(item)!).ToArray();
        Console.WriteLine("CONTROL public engine export findings: " + string.Join(",", findings.Select(Code)) + "; lossless=" + lossless);
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [source]);
        Wait(() => ((Button)owner.FindName("SaveMenuBtn")).IsEnabled, "loss review source readiness");
        int phase = 0;
        Exception? failure = null;
        bool done = false, detailsPresent = false, reviewShown = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        void Queue(Button button) => owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,
            (Action)(() =>
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (button.IsCancel) Console.WriteLine("CONTROL invoked the actual loss-review Cancel button.");
                else if (phase == 5) Console.WriteLine("CONTROL invoked the actual loss-review Continue button.");
            }));
        timer.Tick += (_, _) =>
        {
            try
            {
                Window[] windows = Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray();
                if (phase == 0) { phase = 1; ((Button)owner.FindName("SaveMenuBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); return; }
                if (phase == 1)
                {
                    MenuItem? export = PresentationSource.CurrentSources.Cast<PresentationSource>()
                        .Select(item => item.RootVisual).OfType<DependencyObject>().SelectMany(Descendants<MenuItem>)
                        .FirstOrDefault(item => Equals(item.Header, owner.FindResource("Str_Menu_ExportDocument")));
                    if (export is null) return;
                    if (ItemsControl.ItemsControlFromItemContainer(export) is ContextMenu menu) menu.IsOpen = false;
                    phase = 2;
                    owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() => export.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent))));
                    return;
                }
                Window? review = windows.FirstOrDefault(window => AutomationProperties.GetAutomationId(window) == "ExportDocumentDialog");
                Window? picker = windows.FirstOrDefault(window => window.GetType().Name == "FileDialog");
                if (phase == 2 && review is not null)
                {
                    Descendants<ComboBox>(review).Single(item => AutomationProperties.GetAutomationId(item) == "ExportDocumentFormat").SelectedIndex = lossless ? 6 : 5;
                    phase = 3;
                    Queue(Descendants<Button>(review).Single(item => AutomationProperties.GetAutomationId(item) == "ExportDocumentSave"));
                }
                else if (phase == 3 && picker is not null)
                {
                    ((TextBox)picker.FindName("FileNameBox")).Text = destination;
                    phase = 4; Queue((Button)picker.FindName("AcceptButton"));
                }
                else if (phase == 4)
                {
                    if (lossless && windows.Length == 0 && owner.IsEnabled && File.Exists(destination)) { done = true; return; }
                    string warning = string.Format((string)(owner.TryFindResource("Str_ExportLoss_Summary")
                        ?? owner.FindResource("Str_ExportDoc_LossWarning")), findings.Length);
                    foreach (Window message in windows.Where(window => window != review && window != picker))
                    {
                        string text = string.Join("\n", Descendants<TextBlock>(message).Select(item => item.Text)
                            .Concat(Descendants<TextBox>(message).Where(item => item.IsReadOnly).Select(item => item.Text)));
                        if (!text.Contains(warning, StringComparison.Ordinal)) continue;
                        reviewShown = true;
                        File.WriteAllText(Path.Combine(folder, "loss-review-text.txt"), text);
                        detailsPresent = expectedDetails.All(detail => text.Contains(detail, StringComparison.Ordinal))
                            && text.Contains("Page 1", StringComparison.Ordinal);
                        Console.WriteLine("CONTROL loss review contains page and actual omitted-content detail: " + detailsPresent);
                        phase = 5; Queue(Descendants<Button>(message).Single(button => proceed ? button.IsDefault : button.IsCancel));
                        break;
                    }
                }
                else if (phase == 5 && windows.Length == 0 && owner.IsEnabled && (!proceed || File.Exists(destination))) done = true;
            }
            catch (Exception error)
            {
                failure = error; done = true;
                foreach (Window window in Application.Current.Windows.Cast<Window>().Where(window => window != owner).Reverse().ToArray()) window.Close();
            }
        };
        timer.Start();
        try { Wait(() => done, "export loss workflow"); }
        finally { timer.Stop(); }
        if (failure is not null) throw failure;
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes)) throw new InvalidOperationException("Export review changed the source.");
        if (lossless || proceed)
        {
            if (!File.Exists(destination)) throw new InvalidOperationException("Accepted export did not produce output.");
            string output = File.ReadAllText(destination);
            if (lossless) { using var json = JsonDocument.Parse(output); if (reviewShown || !output.Contains("LOSSLESS_EXPORT", StringComparison.Ordinal)) throw new InvalidOperationException("Lossless JSON export showed a loss review or omitted text."); }
            else if (!output.Contains("0012", StringComparison.Ordinal)) throw new InvalidOperationException("Accepted plain-text export omitted readable source text.");
        }
        else if (File.Exists(destination)) throw new InvalidOperationException("Canceling export loss review wrote output.");
        if (!lossless && !detailsPresent) throw new InvalidOperationException("UI-003: export loss review shows a count without the omitted content and its page.");
        string result = "PASS verify-export-loss-review: " + scenario + " retained source, accurate loss details and the requested Cancel/Continue or lossless output.";
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        Console.WriteLine(result);
        return 0;
    }

    private static byte[] Fixture(bool mixed)
    {
        string content = (mixed ? "0 0 20 20 re f q 20 0 0 20 50 50 cm /Im1 Do Q\n" : "")
            + "BT /F1 12 Tf 20 100 Td (" + (mixed ? "MIXED_EXPORT" : "LOSSLESS_EXPORT") + ") Tj ET\n";
        List<string> objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> "
                + (mixed ? "/XObject << /Im1 6 0 R >> " : "") + ">> /Contents 5 0 R " + (mixed ? "/Annots [7 0 R] " : "") + ">>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream"];
        if (mixed)
        {
            const string image = "FF0000>\n";
            objects.Add($"<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCIIHexDecode /Length {image.Length} >>\nstream\n{image}endstream");
            objects.Add("<< /Type /Annot /Subtype /Link /Rect [20 20 80 35] /A << /S /URI /URI (https://example.invalid/) >> >>");
        }
        var pdf = new StringBuilder("%PDF-1.7\n");
        List<int> offsets = [0];
        for (int index = 0; index < objects.Count; index++) { offsets.Add(pdf.Length); pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
