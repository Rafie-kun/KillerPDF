using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using static DocumentStateSmoke;

internal static class PreviewFailureSmoke
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    internal static int Run(Window owner, string appPath, string folder) => FormWorkflowFailureSmoke.WithCleanup(owner, () =>
    {
        Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(appPath)!, "KillerPDF.PreviewHandler.dll"));
        Type paneType = assembly.GetType("KillerPDF.PreviewHandler.PreviewPane", true)!;
        Type rectType = assembly.GetType("KillerPDF.PreviewHandler.NativeRect", true)!;
        object rect = Activator.CreateInstance(rectType)!;
        rectType.GetField("Right")!.SetValue(rect, 640);
        rectType.GetField("Bottom")!.SetValue(rect, 700);
        foreach (string state in new[] { "complete", "incomplete", "failed" })
        {
            byte[] bytes = Fixture(state);
            File.WriteAllBytes(Path.Combine(folder, state + ".pdf"), bytes);
            using var pane = (IDisposable)Activator.CreateInstance(paneType, Members, null,
                [new WindowInteropHelper(owner).Handle, rect, bytes, "preview.pdf", (uint?)0xffffff, (uint?)0], null)!;
            var slots = ((IEnumerable)paneType.GetField("_slots", Members)!.GetValue(pane)!).Cast<object>().ToArray();
            if (slots.Length != 3) throw new InvalidOperationException("Expected three preview pages.");
            Border Frame(int index) => (Border)slots[index].GetType().GetProperty("Frame", Members)!.GetValue(slots[index])!;
            bool HasPixels(int index) => Descendants<Image>(Frame(index)).Any(image => image.Source is not null);
            Wait(() => HasPixels(0) && HasPixels(2), "valid neighboring preview pages");
            if (state == "complete")
            {
                Wait(() => HasPixels(1), "complete middle preview page");
                if (Descendants<Button>(Frame(1)).Any(button => button.IsVisible)) throw new InvalidOperationException("Complete page incorrectly shows a failure action.");
                Console.WriteLine("PASS complete preview presence control.");
                continue;
            }
            Wait(() => Descendants<TextBlock>(Frame(1)).Any(text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text)),
                "visible incomplete-page warning");
            var notice = Descendants<TextBlock>(Frame(1)).First(text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text));
            if (string.IsNullOrWhiteSpace(AutomationProperties.GetName(notice)))
                throw new InvalidOperationException("Preview failure has no accessible name.");
            Button retry = Descendants<Button>(Frame(1)).Single(button => button.Tag as string == "retry");
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => retry.IsEnabled && notice.IsVisible, "explicit retry completes with a visible warning");
            if (!HasPixels(0) || !HasPixels(2)) throw new InvalidOperationException("Retry lost valid neighboring pages.");
            var nativeSource = (HwndSource)paneType.GetField("_source", Members)!.GetValue(pane)!;
            var visual = (FrameworkElement)nativeSource.RootVisual;
            var capture = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),
                (int)Math.Ceiling(visual.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            capture.Render(visual);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(capture));
            using (var stream = File.Create(Path.Combine(folder, state + "-warning.png"))) encoder.Save(stream);
            Console.WriteLine($"PASS {state} preview warning, accessible name, explicit retry and valid neighbors.");
        }
        return 0;
    });

    private static byte[] Fixture(string state)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R 6 0 R 8 0 R] /Count 3 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };
        for (int page = 0; page < 3; page++)
        {
            string forms = state == "failed" && page == 1 ? "/XObject << /F 10 0 R >>" : "";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << /Font << /F1 3 0 R >> {forms} >> /Contents {5 + page * 2} 0 R >>");
            string content = "BT /F1 14 Tf 20 50 Td (Visible neighbor) Tj ET";
            if (state == "incomplete" && page == 1)
                content = "1 0 0 rg 0 0 20 20 re f\n" + new string(')', 10_001)
                    + "\n0 0 1 rg 100 50 20 20 re f";
            if (state == "failed" && page == 1) content = "/F Do";
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        if (state == "failed")
            for (int level = 0; level <= 25; level++)
            {
                string content = level == 25 ? "q Q" : "/F Do /F Do";
                string resources = level == 25 ? "" : $"/XObject << /F {11 + level} 0 R >>";
                objects.Add($"<< /Type /XObject /Subtype /Form /BBox [0 0 200 100] /Resources << {resources} >> /Length {content.Length} >>\nstream\n{content}\nendstream");
            }
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++) { offsets.Add(text.Length); text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = text.Length;
        text.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
