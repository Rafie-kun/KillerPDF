using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class PrintVisibilitySmoke
{
    internal static int Run(Window owner, string appPath, string folder)
    {
        string path = Path.Combine(folder, "print-only.pdf");
        byte[] original = Fixture();
        File.WriteAllBytes(path, original);
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(owner, [path]);
        Wait(() => Children(owner).OfType<TextBox>().Any(box => Equals(box.ToolTip, "visible.field")));
        if (Children(owner).OfType<TextBox>().Any(box => Equals(box.ToolTip, "print.only")))
            throw new InvalidOperationException("A print-only widget has an interactive screen control.");
        Wait(() => Children(owner).OfType<Image>().Select(image => image.Source)
            .OfType<BitmapSource>().Any(bitmap => bitmap.PixelWidth >= 100));
        if (Children(owner).OfType<Image>().Select(image => image.Source)
            .OfType<BitmapSource>().Any(HasBlueAppearance))
            throw new InvalidOperationException("A print-only blue appearance is painted in the screen viewer.");

        Exception? failure = null;
        bool previewPassed = false;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        var timeout = Stopwatch.StartNew();
        timer.Tick += (_, _) =>
        {
            Window? preview = Application.Current.Windows.Cast<Window>()
                .SingleOrDefault(window => window.GetType().FullName == "KillerPDF.PrintPreviewWindow");
            try
            {
                if (preview is not null && Children(preview).OfType<Image>()
                    .Select(image => image.Source).OfType<BitmapSource>().Any(HasBlueAppearance))
                {
                    previewPassed = true;
                    timer.Stop();
                    preview.Close();
                }
                else if (timeout.Elapsed > TimeSpan.FromSeconds(25))
                    throw new TimeoutException("The actual print preview did not show the print-only blue appearance.");
            }
            catch (Exception error)
            {
                failure = error;
                timer.Stop();
                preview?.Close();
            }
        };
        timer.Start();
        try
        {
            ((Button)owner.FindName("PrintBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(() => previewPassed || failure is not null);
            if (failure is not null) throw failure;
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(original))
                throw new InvalidOperationException("Print preview changed its source fixture.");
            Console.WriteLine("PASS print-visibility: live screen widget filtering, actual print-button preview appearance, unchanged source.");
            Console.WriteLine("App SHA256 " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath))));
            return 0;
        }
        finally { timer.Stop(); }
    }

    private static bool HasBlueAppearance(BitmapSource bitmap)
    {
        var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int stride = checked(bgra.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * bgra.PixelHeight)];
        bgra.CopyPixels(pixels, stride, 0);
        int blue = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
            if (pixels[offset] == 255 && pixels[offset + 1] == 0 && pixels[offset + 2] == 0 && pixels[offset + 3] == 255)
                blue++;
        return blue > 100;
    }

    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Children(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void Wait(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Print visibility smoke did not complete.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(20);
        }
    }

    private static byte[] Fixture()
    {
        const string blue = "0 0 1 rg 0 0 30 30 re f\n";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R 6 0 R] /DA (/Helv 8 Tf 0 g) /DR << /Font << /Helv 4 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [5 0 R 6 0 R 7 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (visible.field) /V (VISIBLE) /Rect [2 2 80 14] /P 3 0 R /F 4 >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (print.only) /V (PRINT ONLY) /Rect [2 84 80 96] /P 3 0 R /F 36 >>",
            "<< /Type /Annot /Subtype /Square /F 36 /Rect [35 35 65 65] /AP << /N 8 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 30 30] /Resources << >> /Length {blue.Length} >>\nstream\n{blue}endstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
