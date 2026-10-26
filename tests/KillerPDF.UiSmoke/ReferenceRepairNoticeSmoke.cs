using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

internal static class ReferenceRepairNoticeSmoke
{
    private const string Notice = "Saved with repairs to broken references. Missing content was not recovered.";
    private const string LiveText = "Keep encrypted text";
    private const string Password = "synthetic-open";

    internal static int Run(Window owner, Assembly app, string appPath, string folder)
    {
        Type locale = app.GetType("KillerPDF.Services.Locale", true)!;
        app.GetType("KillerPDF.Services.LocaleManager", true)!.GetMethod("Apply")!
            .Invoke(null, [Enum.Parse(locale, "EnUS")]);
        if (!Equals(Application.Current.FindResource("Str_Save_ReferenceRepairs"), Notice))
            throw new InvalidOperationException("The approved English repair notice resource is unavailable.");
        VerifyDetector(owner);
        var failures = new List<Exception>();
        foreach (bool repaired in new[] { false, true })
        {
            string name = repaired ? "repaired" : "pristine";
            try
            {
                RunCase(owner, app, folder, name, repaired);
                Console.WriteLine("PASS " + name + " protection and repair notice verdict");
            }
            catch (Exception error)
            {
                failures.Add(new InvalidOperationException(name + ": " + error.Message, error));
                Console.WriteLine("FAIL " + name + ": " + error.Message);
            }
        }
        if (failures.Count > 0) throw new AggregateException(failures);
        string result = "PASS real Protect button: pristine success omits repair notice; repaired success includes it. "
            + "Both encrypted outputs reopen with retained live text and unchanged source bytes. "
            + "Notice detector passed target-present and target-removed controls. App SHA256="
            + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        Console.WriteLine(result);
        return 0;
    }

    private static bool HasNotice(DependencyObject root) => Children(root).OfType<TextBlock>()
        .Any(block => block.IsVisible && block.ActualWidth > 0 && block.ActualHeight > 0
            && block.Text.Contains(Notice, StringComparison.Ordinal));

    private static void VerifyDetector(Window owner)
    {
        var target = new TextBlock { Text = Notice, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Protected copy saved to:\nsynthetic-protected.pdf" });
        panel.Children.Add(target);
        var control = new Window { Owner = owner, Content = panel, Width = 400, Height = 180,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
            ShowInTaskbar = false, ShowActivated = false };
        try
        {
            control.Show(); Pump(); control.UpdateLayout();
            if (!HasNotice(control)) throw new InvalidOperationException("Notice detector missed its present control.");
            target.Text = ""; control.UpdateLayout(); Pump();
            if (HasNotice(control)) throw new InvalidOperationException("Notice detector accepted neighboring success text after target removal.");
        }
        finally { control.Close(); }
        Console.WriteLine("PASS rendered notice detector present/removed controls");
    }

    private static void RunCase(Window owner, Assembly app, string folder, string name, bool repaired)
    {
        string source = Path.Combine(folder, name + "-source.pdf");
        string output = Path.Combine(folder, name + "-protected.pdf");
        byte[] original = Fixture(repaired);
        File.WriteAllBytes(source, original);
        var dialog = (Window)Activator.CreateInstance(app.GetType("KillerPDF.ProtectDocumentDialog", true)!,
            [owner, source, source])!;
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = -10000; dialog.Top = -10000;
        dialog.ShowInTaskbar = false; dialog.ShowActivated = false;
        bool clicked = false;
        bool? noticeSeen = null;
        string? messageText = null;
        Exception? failure = null;
        var handled = new HashSet<Window>();
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        void Fail(Exception error)
        {
            failure ??= error;
            timer.Stop();
            foreach (Window message in Application.Current.Windows.Cast<Window>()
                .Where(window => window.Owner == dialog).ToArray()) message.Close();
            if (dialog.IsVisible) dialog.Close();
        }
        timer.Tick += (_, _) =>
        {
            try
            {
                if (watch.Elapsed.TotalSeconds > 20) throw new TimeoutException("Protect workflow exceeded 20 seconds.");
                if (!clicked)
                {
                    if (!dialog.IsVisible || !dialog.IsLoaded) return;
                    PasswordBox[] passwords = Children(dialog).OfType<PasswordBox>().ToArray();
                    if (passwords.Length != 3 || passwords.Any(box => !box.IsVisible || !box.IsEnabled))
                        throw new InvalidOperationException("Expected three visible enabled password controls.");
                    passwords[0].Password = Password; passwords[1].Password = Password;
                    passwords[2].Password = "synthetic-owner";
                    TextBox[] outputFields = Children(dialog).OfType<TextBox>()
                        .Where(box => box.IsVisible && box.IsEnabled && !box.IsReadOnly
                            && box.Text.Equals(Path.Combine(Path.GetDirectoryName(source)!,
                                Path.GetFileNameWithoutExtension(source) + "-protected.pdf"), StringComparison.Ordinal))
                        .ToArray();
                    if (outputFields.Length != 1)
                        throw new InvalidOperationException("Expected one visible output-path field; found "
                            + outputFields.Length.ToString(CultureInfo.InvariantCulture) + ".");
                    outputFields[0].Text = output;
                    Button protect = Children(dialog).OfType<Button>().Single(button =>
                        Equals(button.Content, Application.Current.FindResource("Str_Protect_Protect")));
                    protect.BringIntoView(); dialog.UpdateLayout();
                    if (!protect.IsVisible || !protect.IsEnabled) throw new InvalidOperationException("Protect button is unavailable.");
                    clicked = true;
                    dialog.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        if (failure is not null || !dialog.IsVisible) return;
                        try { Click(protect); }
                        catch (Exception error) { Fail(error); }
                    }));
                    return;
                }
                foreach (Window message in Application.Current.Windows.Cast<Window>()
                    .Where(window => window.Owner == dialog && window.IsVisible).ToArray())
                {
                    if (!handled.Add(message)) continue;
                    message.UpdateLayout();
                    messageText = string.Join("\n", Children(message).OfType<TextBlock>()
                        .Where(block => block.IsVisible).Select(block => block.Text));
                    noticeSeen = HasNotice(message);
                    File.WriteAllText(Path.Combine(folder, name + "-modal.txt"), messageText);
                    Click(Children(message).OfType<Button>().Single(button => button.IsDefault));
                    if (!messageText.Contains((string)Application.Current.FindResource("Str_Protect_SavedTo"), StringComparison.Ordinal)
                        || !messageText.Contains(output, StringComparison.Ordinal))
                        throw new InvalidOperationException("Protect displayed a failure or unexpected modal: " + messageText);
                }
            }
            catch (Exception error)
            {
                Fail(error);
            }
        };
        timer.Start();
        bool? accepted;
        try { accepted = dialog.ShowDialog(); }
        finally { timer.Stop(); if (dialog.IsVisible) dialog.Close(); }
        if (failure is not null) throw new InvalidOperationException("Protect fixture did not complete.", failure);
        if (accepted != true || noticeSeen is null || messageText is null)
            throw new InvalidOperationException("Protect did not finish with an observed successful save modal.");
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original))
            throw new InvalidOperationException("Protect changed the source bytes.");
        if (!File.Exists(output) || new FileInfo(output).Length == 0)
            throw new InvalidOperationException("Protect did not save its encrypted output.");
        VerifyEncrypted(output);
        if (noticeSeen != repaired)
            throw new InvalidOperationException(repaired ? "Successful repaired copy omitted the repair notice."
                : "Successful pristine copy incorrectly displayed the repair notice.");
    }

    private static void VerifyEncrypted(string path)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        Type documentType = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!;
        byte[] bytes = File.ReadAllBytes(path);
        MethodInfo open = documentType.GetMethod("Open", [typeof(ReadOnlyMemory<byte>), typeof(string)])!;
        try
        {
            _ = open.Invoke(null, [new ReadOnlyMemory<byte>(bytes), "wrong-password"]);
            throw new InvalidOperationException("Encrypted output accepted an incorrect password.");
        }
        catch (TargetInvocationException error) when (error.InnerException is CryptographicException) { }
        object document = open.Invoke(null, [new ReadOnlyMemory<byte>(bytes), Password])!;
        try
        {
            if (!(bool)documentType.GetProperty("IsEncrypted")!.GetValue(document)!
                || !(bool)documentType.GetProperty("IsDecrypted")!.GetValue(document)!)
                throw new InvalidOperationException("Saved copy did not reopen as authenticated encrypted content.");
            Type readerType = engine.GetType("KillerPdf.Engine.Documents.PdfPageContentReader", true)!;
            object reader = Activator.CreateInstance(readerType, [document])!;
            object page = readerType.GetMethod("Read", [typeof(int), typeof(CancellationToken)])!
                .Invoke(reader, [0, CancellationToken.None])!;
            if (!Equals(page.GetType().GetProperty("Text")!.GetValue(page), LiveText))
                throw new InvalidOperationException("Encrypted output lost known live text.");
        }
        finally { (document as IDisposable)?.Dispose(); }
    }

    private static byte[] Fixture(bool dangling)
    {
        const string content = "BT /F1 12 Tf 20 80 Td (Keep encrypted text) Tj ET\n";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R" + (dangling ? " /Optional 999 0 R" : "") + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {content.Length} >>\nstream\n{content}endstream"];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (int i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (DependencyObject nested in Children(child)) yield return nested;
        }
    }
}
