using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class PageDeletionSmoke
{
    internal static int Run(Window owner, string folder, string scenario = "last")
    {
        try { return RunCore(owner, folder, scenario); }
        catch (Exception error) { Console.Error.WriteLine("Page deletion stage failed: " + error); throw; }
        finally
        {
            var seen = new HashSet<Window>();
            var cleanup = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            cleanup.Tick += (_, _) =>
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
                {
                    if (!seen.Add(dialog)) continue;
                    Button? discard = Descendants<Button>(dialog).FirstOrDefault(button =>
                        Equals(button.Content, owner.FindResource("Str_Btn_DontSave")) || Equals(button.Content, owner.FindResource("Str_Btn_Quit")));
                    if (discard is not null) owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                        (Action)(() => discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                    else dialog.Close();
                }
            };
            cleanup.Start();
            try { if (owner.IsVisible) owner.Close(); Wait(() => !owner.IsVisible, "owned deletion-window cleanup"); }
            finally { cleanup.Stop(); }
        }
    }

    private static int RunCore(Window owner, string folder, string scenario)
    {
        (int[] Removed, int Active, int Target, int SurvivingOriginal, string Labels) expected = scenario switch
        {
            "first" => ([0], 0, 0, 1, "PAGE_2|PAGE_3|PAGE_4|PAGE_5"),
            "middle" => ([2], 2, 2, 3, "PAGE_1|PAGE_2|PAGE_4|PAGE_5"),
            "tail-block" => ([3, 4], 3, 2, 2, "PAGE_1|PAGE_2|PAGE_3"),
            "disjoint" => ([0, 2, 4], 0, 0, 1, "PAGE_2|PAGE_4"),
            "last" => ([4], 4, 3, 3, "PAGE_1|PAGE_2|PAGE_3|PAGE_4"),
            "cancel" => ([4], 4, 4, 4, "PAGE_1|PAGE_2|PAGE_3|PAGE_4|PAGE_5"),
            _ => throw new ArgumentException("Unknown deletion case: " + scenario)
        };
        (byte R, byte G, byte B)[] colors = [(255, 0, 0), (0, 255, 0), (0, 0, 255), (255, 255, 0), (255, 0, 255)];
        string source = Path.Combine(folder, "delete-last-page.pdf");
        byte[] bytes = Fixture();
        File.WriteAllBytes(source, bytes);
        if (Read(source) != (5, "PAGE_1|PAGE_2|PAGE_3|PAGE_4|PAGE_5")) throw new InvalidOperationException("Public five-page fixture control failed.");
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [source]);
        owner.Left = 32; owner.Top = 32; owner.Width = 950; owner.Height = 700; owner.Opacity = 1; owner.Activate();
        var pages = (ListBox)owner.FindName("PageList");
        var jump = (TextBox)owner.FindName("PageJumpBox");
        Wait(() => owner.IsActive && pages.Items.Count == 5, "five-page sidebar");
        ((RadioButton)owner.FindName("ViewSingleRadio")).IsChecked = true;
        bool PageColor(byte red, byte green, byte blue)
        {
            ScrollViewer? viewport = Descendants<ScrollViewer>(owner).SingleOrDefault(view => view.Name == "PagePreviewPanel" && view.IsVisible);
            if (viewport is null) return false;
            foreach (Image image in Descendants<Image>(viewport).Where(image => image.IsVisible))
            {
                if (image.Source is not BitmapSource bitmap || bitmap.PixelWidth < 20 || bitmap.PixelHeight < 20) continue;
                BitmapSource sample = bitmap.Format == PixelFormats.Bgra32 || bitmap.Format == PixelFormats.Pbgra32
                    ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
                byte[] pixel = new byte[4];
                sample.CopyPixels(new Int32Rect(sample.PixelWidth / 2, sample.PixelHeight / 4, 1, 1), pixel, 4, 0);
                if (Math.Abs(pixel[2] - red) < 8 && Math.Abs(pixel[1] - green) < 8 && Math.Abs(pixel[0] - blue) < 8) return true;
            }
            return false;
        }
        Wait(() => pages.SelectedIndex == 0 && PageColor(255, 0, 0), "first-page rendered presence control");
        if (PageColor(255, 0, 255)) throw new InvalidOperationException("Page-color absence control confused the first and final page.");
        pages.SelectedIndex = expected.Active;
        Wait(() => pages.SelectedIndex == expected.Active && jump.Text == (expected.Active + 1).ToString(CultureInfo.InvariantCulture)
            && PageColor(colors[expected.Active].R, colors[expected.Active].G, colors[expected.Active].B), "displayed target page");
        foreach (int index in expected.Removed.Where(index => index != expected.Active)) pages.SelectedItems.Add(pages.Items[index]);
        Wait(() => pages.SelectedItems.Cast<object>().Select(item => pages.Items.IndexOf(item)).Order().SequenceEqual(expected.Removed.Order())
            && jump.Text == (expected.Active + 1).ToString(CultureInfo.InvariantCulture)
            && PageColor(colors[expected.Active].R, colors[expected.Active].G, colors[expected.Active].B)
            && ((Button)owner.FindName("DeletePagesBtn")).IsEnabled, "rendered target, exact selected set and Delete availability");
        Console.WriteLine($"CONTROL case={scenario}, active={expected.Active}, selected={string.Join(',', expected.Removed)}; initial rendered color controls passed.");
        int confirmations = 0;
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                Button? yes = Descendants<Button>(dialog).FirstOrDefault(button => Equals(button.Content,
                    owner.FindResource(scenario == "cancel" ? "Str_Btn_No" : "Str_Btn_Yes")));
                if (yes is null || !seen.Add(dialog)) continue;
                confirmations++;
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                    (Action)(() => yes.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            }
        };
        timer.Start();
        try { Click(owner, "DeletePagesBtn"); }
        finally { timer.Stop(); }
        int remaining = expected.Labels.Split('|').Length;
        Wait(() => pages.Items.Count == remaining && pages.SelectedIndex >= 0, "completed page deletion");
        Console.WriteLine($"CONTROL deletion: confirmations={confirmations}, selectedIndex={pages.SelectedIndex}, pageCounter={jump.Text}, remaining={pages.Items.Count}.");
        if (confirmations != 1) throw new InvalidOperationException("Expected one real Delete confirmation.");
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes)) throw new InvalidOperationException("Delete changed the source before Save.");
        var survivorColor = colors[expected.SurvivingOriginal];
        Console.WriteLine($"CONTROL immediate survivor bitmap={PageColor(survivorColor.R, survivorColor.G, survivorColor.B)}, expected original page={expected.SurvivingOriginal + 1}.");
        Wait(() => pages.SelectedIndex == expected.Target && jump.Text == (expected.Target + 1).ToString(CultureInfo.InvariantCulture)
            && PageColor(survivorColor.R, survivorColor.G, survivorColor.B), "USER-006 rendered nearest survivor in " + scenario);
        if (pages.SelectedIndex != expected.Target || jump.Text != (expected.Target + 1).ToString(CultureInfo.InvariantCulture)
            || !PageColor(survivorColor.R, survivorColor.G, survivorColor.B))
            throw new InvalidOperationException("USER-006: deletion jumped away from the nearest surviving page in " + scenario + ".");
        if (scenario == "cancel")
        {
            if (Dirty(owner) || ((Button)owner.FindName("UndoBtn")).IsEnabled
                || Read(source) != (5, "PAGE_1|PAGE_2|PAGE_3|PAGE_4|PAGE_5"))
                throw new InvalidOperationException("Cancel changed document content or history.");
            const string canceled = "PASS verify-delete-nearest: Cancel preserves all five pages, rendered page5, source bytes and clean history.";
            File.WriteAllText(Path.Combine(folder, "result.txt"), canceled); Console.WriteLine(canceled);
            return 0;
        }
        Save(owner);
        if (Read(source) != (remaining, expected.Labels)) throw new InvalidOperationException("Strict saved deletion did not retain the expected surviving pages.");
        Wait(() => ((Button)owner.FindName("UndoBtn")).IsEnabled, "Delete Undo availability");
        Click(owner, "UndoBtn");
        Wait(() => pages.Items.Count == 5, "Delete Undo page set");
        Save(owner);
        if (Read(source) != (5, "PAGE_1|PAGE_2|PAGE_3|PAGE_4|PAGE_5")) throw new InvalidOperationException("Delete Undo did not restore the original page set.");
        Wait(() => ((Button)owner.FindName("RedoBtn")).IsEnabled, "Delete Redo availability");
        Click(owner, "RedoBtn");
        Wait(() => pages.Items.Count == remaining, "Delete Redo page set");
        Save(owner);
        if (Read(source) != (remaining, expected.Labels)) throw new InvalidOperationException("Delete Redo did not restore the surviving page set.");
        string result = "PASS verify-delete-nearest: " + scenario + " retains the nearest survivor; strict Save, Undo and Redo preserve every expected page label.";
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        Console.WriteLine(result);
        return 0;
    }

    private static (int Count, string Labels) Read(string path)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(path))])!;
        int count = ((System.Collections.IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfPageInformation", true)!
            .GetMethod("Read")!.Invoke(null, [document])!).Cast<object>().Count();
        object reader = Activator.CreateInstance(engine.GetType("KillerPdf.Engine.Documents.PdfPageContentReader", true)!, [document])!;
        var labels = new List<string>();
        for (int page = 0; page < count; page++)
        {
            object content = reader.GetType().GetMethod("Read", [typeof(int), typeof(CancellationToken)])!.Invoke(reader, [page, CancellationToken.None])!;
            labels.Add(((string)content.GetType().GetProperty("Text")!.GetValue(content)!).Trim());
        }
        return (count, string.Join('|', labels));
    }

    private static byte[] Fixture()
    {
        List<string> objects = ["<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [4 0 R 6 0 R 8 0 R 10 0 R 12 0 R] /Count 5 >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"];
        for (int page = 0; page < 5; page++)
        {
            string[] colors = ["1 0 0", "0 1 0", "0 0 1", "1 1 0", "1 0 1"];
            string content = $"{colors[page]} rg 0 0 200 200 re f 0 g BT /F1 20 Tf 20 100 Td (PAGE_{page + 1}) Tj ET\n";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 3 0 R >> >> /Contents {5 + page * 2} 0 R >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}endstream");
        }
        var pdf = new StringBuilder("%PDF-1.7\n");
        List<int> offsets = [0];
        for (int index = 0; index < objects.Count; index++) { offsets.Add(pdf.Length); pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n"); }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
