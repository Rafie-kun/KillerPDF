using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

internal static class SignedCopySmoke
{
    internal static int Run(Window owner, Assembly app, string appPath, string folder)
    {
        string unsigned = Path.Combine(folder, "measurement-unsigned.pdf");
        string signed = Path.Combine(folder, "measurement-signed.pdf");
        File.WriteAllBytes(unsigned, Fixture());
        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=KillerPDF Signed Copy Smoke", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Sign(app, unsigned, signed, certificate);
        byte[] expected = File.ReadAllBytes(signed);
        VerifySignedMeasurement(expected, 1);

        var failures = new List<Exception>();
        Check("measurement Save As", () =>
        {
            Open(owner, signed);
            string output = Path.Combine(folder, "unchanged-save-as.pdf");
            SaveAs(owner, app, output);
            VerifyExactCopy(output, expected, 1);
            if (!File.ReadAllBytes(signed).AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("Save As changed its source file.");
        });
        Check("measurement Save Copy and co-sign", () =>
        {
            Open(owner, signed);
            string output = Path.Combine(folder, "unchanged-save-copy.pdf");
            SaveAs(owner, app, output, copy: true);
            VerifyExactCopy(output, expected, 1);
            var fileLabel = (TextBlock)app.GetType("KillerPDF.Features.IViewerHost", true)!
                .GetProperty("FileNameLabel")!.GetValue(owner)!;
            if (fileLabel.Text != Path.GetFileName(output))
                throw new InvalidOperationException("Save Copy did not open its new output.");
            if (!File.ReadAllBytes(signed).AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("Save Copy changed its source file.");
            string cosigned = Path.Combine(folder, "measurement-cosigned.pdf");
            Sign(app, output, cosigned, certificate);
            byte[] actual = File.ReadAllBytes(cosigned);
            VerifySignedMeasurement(actual, 2);
            if (!actual.AsSpan().StartsWith(expected))
                throw new InvalidOperationException("Co-signing changed the original signed revision.");
        });
        Check("externally replaced source", () =>
        {
            string plain = Path.Combine(folder, "external-unsigned.pdf");
            string original = Path.Combine(folder, "external-signed.pdf");
            File.WriteAllBytes(plain, Fixture(measurement: false));
            Sign(app, plain, original, certificate);
            byte[] loaded = File.ReadAllBytes(original);
            VerifySignedMeasurement(loaded, 1, expectedMeasurements: 0);
            Open(owner, original);
            byte[] replacement = Fixture(measurement: false, pageWidth: 300);
            File.WriteAllBytes(original, replacement);
            string output = Path.Combine(folder, "external-save-as.pdf");
            SaveAs(owner, app, output);
            VerifyExactCopy(output, loaded, 0);
            if (!File.ReadAllBytes(original).AsSpan().SequenceEqual(replacement))
                throw new InvalidOperationException("Save As overwrote the external replacement.");
        });
        if (failures.Count > 0) throw new AggregateException(failures);
        string result = "PASS approval-signed Save As and Save Copy preserve exact bytes, measurements and CMS integrity; "
            + "co-signing preserves the original signed revision, and external replacement cannot replace the loaded source. "
            + "Offscreen public WPF workflow and ephemeral public signer; native PFX dialog and certificate trust remain separate gates. App SHA256="
            + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        Console.WriteLine(result);
        return 0;

        void Check(string name, Action action)
        {
            try { action(); Console.WriteLine("PASS " + name); }
            catch (Exception error)
            {
                Console.WriteLine("FAIL " + name + ": " + error.Message);
                failures.Add(new InvalidOperationException(name, error));
            }
        }
    }

    private static void Sign(Assembly app, string source, string output, X509Certificate2 certificate)
    {
        Type signer = app.GetType("KillerPDF.Services.Signing.PdfSigner", true)!;
        Type info = signer.GetNestedType("SignInfo", BindingFlags.Public)!;
        object metadata = Activator.CreateInstance(info, ["Synthetic approval", "", "", null, null])!;
        signer.GetMethod("Sign", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(Activator.CreateInstance(signer), [source, output, certificate, metadata]);
    }

    private static void VerifyExactCopy(string path, byte[] expected, int expectedMeasurements)
    {
        byte[] actual = File.ReadAllBytes(path);
        VerifySignedMeasurement(actual, 1, expectedMeasurements);
        if (!actual.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException("Unchanged signed copy changed the complete loaded bytes.");
    }

    private static void VerifySignedMeasurement(byte[] bytes, int expectedSignatures, int expectedMeasurements = 1)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        Type documentType = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!;
        object document = documentType.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!
            .Invoke(null, [new ReadOnlyMemory<byte>(bytes)])!;
        try
        {
            object[] measurements = ((IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfMeasurementAnnotationReader", true)!
                .GetMethod("Read")!.Invoke(null, [document])!).Cast<object>().ToArray();
            if (measurements.Length != expectedMeasurements)
                throw new InvalidOperationException("Signed copy measurement count: expected " + expectedMeasurements + ", got " + measurements.Length + ".");
            object[] signatures = ((IEnumerable)engine.GetType("KillerPdf.Engine.Signing.PdfSignatureReader", true)!
                .GetMethod("Read")!.Invoke(null, [document])!).Cast<object>().ToArray();
            if (signatures.Length != expectedSignatures)
                throw new InvalidOperationException("Signed copy has the wrong signature count.");
            foreach (object signature in signatures)
            {
                object result = engine.GetType("KillerPdf.Engine.Signing.PdfSignatureVerifier", true)!
                    .GetMethod("VerifyIntegrity")!.Invoke(null, [document, signature])!;
                if (!(bool)result.GetType().GetProperty("IsCryptographicallyValid")!.GetValue(result)!
                    || !(bool)result.GetType().GetProperty("IsStructurallyValid")!.GetValue(result)!)
                    throw new InvalidOperationException("Signed copy lost valid CMS integrity.");
            }
        }
        finally { (document as IDisposable)?.Dispose(); }
    }

    private static void Open(Window owner, string path)
    {
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [path]);
        Pump();
    }

    internal static void SaveAs(Window owner, Assembly app, string destination, bool copy = false)
    {
        Exception? failure = null;
        var seen = new HashSet<Window>();
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            try
            {
                if (elapsed.Elapsed.TotalSeconds > 30) throw new TimeoutException("Signed Save As did not finish.");
                foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
                {
                    if (!seen.Add(dialog)) continue;
                    if (dialog.GetType().FullName != "KillerPDF.Controls.FileDialog")
                        throw new InvalidOperationException("Unexpected signed Save As message: "
                            + string.Join(" ", Children(dialog).OfType<TextBlock>().Select(block => block.Text)));
                    Children(dialog).OfType<TextBox>().Single(box => box.Name == "FileNameBox").Text = destination;
                    Click(Children(dialog).OfType<Button>().Single(button => button.Name == "AcceptButton"));
                }
            }
            catch (Exception error)
            {
                failure = error; timer.Stop();
                foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner).ToArray()) dialog.Close();
            }
        };
        timer.Start();
        try
        {
            if (copy)
                app.GetType("KillerPDF.Features.IViewerHost", true)!.GetMethod("SaveAsWithName")!
                    .Invoke(owner, [Path.GetFileName(destination)]);
            else
            {
                Click((Button)owner.FindName("SaveMenuBtn")); Pump();
                string label = (string)Application.Current.FindResource("Str_Menu_SaveAs");
                MenuItem item = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual)
                    .OfType<DependencyObject>().SelectMany(Children).OfType<MenuItem>().Single(candidate => Equals(candidate.Header, label));
                if (ItemsControl.ItemsControlFromItemContainer(item) is ContextMenu menu) menu.IsOpen = false;
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            }
            Pump();
            if (failure is not null) throw failure;
            if (!File.Exists(destination)) throw new InvalidOperationException("Signed Save As did not write its output.");
        }
        finally { timer.Stop(); }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Children(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    internal static byte[] Fixture(bool measurement = true, int pageWidth = 200)
    {
        string annotations = measurement ? " /Annots [4 0 R]" : "";
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth} 200] /Resources << >>{annotations} >>",
            "<< /Type /Annot /Subtype /Line /IT /LineDimension /Rect [30 28 130 45] /L [30 30 130 30] /Contents (100 pt) /C [1 0 0] /BS << /W 1 >> /F 4 >>"];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
