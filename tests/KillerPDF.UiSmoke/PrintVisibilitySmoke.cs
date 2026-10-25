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
        VerifyColorProbe();
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
        Wait(() => HasOverlayColor(owner, 32, 223, 64, stroke: true)
            && HasOverlayColor(owner, 255, 128, 0, stroke: false));
        Console.WriteLine("Print visibility: visible measurement and redaction overlay positive controls passed.");
        if (HasOverlayColor(owner, 255, 0, 255, stroke: true))
            throw new InvalidOperationException("A print-only measurement overlay is painted on screen.");
        if (HasOverlayColor(owner, 0, 255, 255, stroke: false))
            throw new InvalidOperationException("A print-only redaction overlay is painted on screen.");

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
                    .Select(image => image.Source).OfType<BitmapSource>().Any(HasPrintAppearances))
                {
                    previewPassed = true;
                    timer.Stop();
                    preview.Close();
                }
                else if (timeout.Elapsed > TimeSpan.FromSeconds(25))
                    throw new TimeoutException("The actual print preview did not show all three print-only appearances.");
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
            Console.WriteLine("PASS print-visibility: live screen widget and overlay filtering, actual print-button preview appearances, unchanged source.");
            Console.WriteLine("App SHA256 " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath))));
            return 0;
        }
        finally { timer.Stop(); }
    }

    internal static int VerifyColorProbe()
    {
        BitmapSource Fixture(bool cyan)
        {
            byte[][] colors = [[255, 0, 0, 255], [255, 0, 255, 255],
                cyan ? [255, 255, 191, 255] : [255, 248, 248, 255], [255, 255, 255, 255]];
            byte[] pixels = new byte[1200 * 4];
            for (int index = 0; index < 1200; index++) colors[index / 300].CopyTo(pixels, index * 4);
            return BitmapSource.Create(1200, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4800);
        }
        if (CountAppearances(Fixture(false)).Cyan > 100)
            throw new InvalidOperationException("Print preview color probe mistakes pale blue for cyan.");
        var positive = CountAppearances(Fixture(true));
        if (positive.Blue <= 100 || positive.Magenta <= 100 || positive.Cyan <= 100)
            throw new InvalidOperationException("Print preview color probe rejects the actual cyan appearance.");
        BitmapSource PageFixture(bool redaction, bool blue = true, bool magenta = true)
        {
            byte[] pixels = Enumerable.Repeat((byte)255, 200 * 200 * 4).ToArray();
            void Fill(int left, int top, int width, int height, byte red, byte green, byte blue)
            {
                for (int y = top; y < top + height; y++)
                for (int x = left; x < left + width; x++)
                {
                    int offset = (y * 200 + x) * 4;
                    pixels[offset] = blue; pixels[offset + 1] = green; pixels[offset + 2] = red;
                }
            }
            if (blue) Fill(70, 70, 60, 60, 0, 0, 255);
            if (magenta) Fill(20, 150, 140, 3, 255, 0, 255);
            if (redaction) Fill(145, 45, 20, 20, 245, 198, 198);
            return BitmapSource.Create(200, 200, 96, 96, PixelFormats.Bgra32, null, pixels, 800);
        }
        if (HasPrintAppearances(PageFixture(false)))
            throw new InvalidOperationException("Print preview probe accepts a page without redaction ink.");
        if (!HasPrintAppearances(PageFixture(true)))
            throw new InvalidOperationException("Print preview probe rejects redaction ink at its known page position.");
        if (HasPrintAppearances(PageFixture(true, blue: false))
            || HasPrintAppearances(PageFixture(true, magenta: false)))
            throw new InvalidOperationException("Print preview probe accepts a missing annotation or measurement appearance.");
        Console.WriteLine("PASS print preview probe: independent ink colors, pale-blue negative control and redaction location.");
        return 0;
    }

    private static bool HasOverlayColor(Window owner, byte red, byte green, byte blue, bool stroke) =>
        Children(owner).OfType<System.Windows.Shapes.Shape>().Any(shape =>
            (stroke ? shape.Stroke : shape.Fill) is SolidColorBrush brush
            && Math.Abs(brush.Color.R - red) <= 1 && Math.Abs(brush.Color.G - green) <= 1
            && Math.Abs(brush.Color.B - blue) <= 1);

    private static bool HasBlueAppearance(BitmapSource bitmap) => CountAppearances(bitmap).Blue > 100;

    private static bool HasPrintAppearances(BitmapSource bitmap)
    {
        var colors = CountAppearances(bitmap);
        return colors.Blue > 100 && colors.Magenta > 100 && HasRedactionRegion(bitmap);
    }

    private static bool HasRedactionRegion(BitmapSource bitmap)
    {
        if (bitmap.PixelWidth < 10 || bitmap.PixelHeight < 10) return false;
        var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var area = new Int32Rect(bitmap.PixelWidth * 71 / 100, bitmap.PixelHeight * 21 / 100,
            bitmap.PixelWidth * 18 / 100, bitmap.PixelHeight * 18 / 100);
        int stride = area.Width * 4;
        byte[] pixels = new byte[stride * area.Height];
        bgra.CopyPixels(area, pixels, stride, 0);
        int red = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
            if (pixels[offset + 2] > 225 && pixels[offset + 1] < 230 && pixels[offset] < 230)
                red++;
        return red > 100;
    }

    private static (int Blue, int Magenta, int Cyan) CountAppearances(BitmapSource bitmap)
    {
        var bgra = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        int stride = checked(bgra.PixelWidth * 4);
        byte[] pixels = new byte[checked(stride * bgra.PixelHeight)];
        bgra.CopyPixels(pixels, stride, 0);
        int blue = 0, magenta = 0, cyan = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if (pixels[offset] == 255 && pixels[offset + 1] == 0 && pixels[offset + 2] == 0 && pixels[offset + 3] == 255)
                blue++;
            if (pixels[offset] > 245 && pixels[offset + 1] < 16 && pixels[offset + 2] > 245)
                magenta++;
            if (pixels[offset] > 245 && pixels[offset + 1] > 245 && pixels[offset + 2] < 224)
                cyan++;
        }
        return (blue, magenta, cyan);
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
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> /Annots [5 0 R 6 0 R 7 0 R 9 0 R 10 0 R 11 0 R 12 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (visible.field) /V (VISIBLE) /Rect [2 2 80 14] /P 3 0 R /F 4 >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (print.only) /V (PRINT ONLY) /Rect [2 84 80 96] /P 3 0 R /F 36 >>",
            "<< /Type /Annot /Subtype /Square /F 36 /Rect [35 35 65 65] /AP << /N 8 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 30 30] /Resources << >> /Length {blue.Length} >>\nstream\n{blue}endstream",
            "<< /Type /Annot /Subtype /Line /IT /LineDimension /L [10 20 80 20] /Rect [10 15 80 25] /F 36 /C [1 0 1] /BS << /W 2 >> /Contents (PRINT MEASUREMENT) >>",
            "<< /Type /Annot /Subtype /Line /IT /LineDimension /L [10 30 80 30] /Rect [10 25 80 35] /F 4 /C [.125 .875 .25] /BS << /W 2 >> /Contents (VISIBLE MEASUREMENT) >>",
            "<< /Type /Annot /Subtype /Redact /Rect [70 60 90 80] /F 36 /IC [0 1 1] >>",
            "<< /Type /Annot /Subtype /Redact /Rect [5 60 20 80] /F 4 /IC [1 .5 0] >>"
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
