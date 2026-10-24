using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

internal static class RenderRecoverySmoke
{
    internal static int Run(Window owner, string appPath, string folder)
    {
        bool pendingOnly = Environment.GetCommandLineArgs().Contains("--verify-pending-text-recovery", StringComparer.Ordinal);
        if (!pendingOnly) VerifyIncompleteRecovery(owner, folder);
        foreach (bool existing in new[] { false, true })
        {
            string mode = existing ? "existing-page-text" : "new-annotation";
            string source = Path.Combine(folder, mode + ".pdf");
            byte[] fixture = Fixture();
            File.WriteAllBytes(source, fixture);
            Open(owner, source);
            var viewer = (FrameworkElement)owner.FindName("Viewer");
            Canvas page = Children(viewer).OfType<Canvas>().First(canvas => Equals(canvas.Tag, 0) && canvas.Width > 0);
            ((Button)owner.FindName("ToolTextBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            string entry = existing ? "BeginTextEditAtPosition" : "PlaceTextAtPosition";
            viewer.GetType().GetMethod(entry, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(viewer, [new Point(page.Width * .18, page.Height * (existing ? .15 : .5)), 0]);
            Wait(() => Children(viewer).OfType<TextBox>().Any(box => box.AcceptsReturn && Equals(box.Tag, 0)), "page editor");
            TextBox editor = Children(viewer).OfType<TextBox>().Single(box => box.AcceptsReturn && Equals(box.Tag, 0));
            if (existing && !editor.Text.Contains("ORIGINAL BODY TEXT", StringComparison.Ordinal))
                throw new InvalidOperationException("The real page-text editor did not contain the source line.");
            if (!existing) editor.Width = page.Width * .75;
            editor.Focus();
            editor.SelectAll();
            string expected = existing ? "RECOVERED EXISTING TEXT" : "RECOVERED PENDING ANNOTATION";
            editor.SelectedText = expected;
            if (editor.Text != expected) throw new InvalidOperationException("The live editor did not accept the pending edit.");

            string? manifest = (string?)owner.GetType()
                .GetMethod("PrepareRenderFailureRecovery", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(owner, null);
            if (manifest is null) throw new InvalidOperationException(mode + ": recovery skipped the clean tab with pending text.");
            using JsonDocument recovery = JsonDocument.Parse(File.ReadAllBytes(manifest));
            string recovered = recovery.RootElement.EnumerateArray()
                .Single(item => item.GetProperty("OriginalPath").GetString() == source)
                .GetProperty("RecoveredPath").GetString()!;
            string actual = Extract(File.ReadAllBytes(recovered));
            if (!actual.Contains(expected, StringComparison.Ordinal)
                || existing && actual.Contains("ORIGINAL BODY TEXT", StringComparison.Ordinal))
                throw new InvalidOperationException(mode + ": saved recovery omitted or duplicated pending text: " + actual);
            if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(fixture))
                throw new InvalidOperationException("Recovery changed the original file.");
            CloseWithoutSaving(owner);
            Open(owner, recovered);
            try
            {
                Wait(() => string.Join(" ", ReadDocument(owner).Split((char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries)).Contains(expected, StringComparison.Ordinal),
                    "reopened recovery text provider");
            }
            catch (TimeoutException error)
            {
                throw new InvalidOperationException("Reopened recovery provider text: " + ReadDocument(owner), error);
            }
            CloseWithoutSaving(owner);
            Console.WriteLine("PASS pending " + mode + ", recovery output, original preservation and reopened provider text.");
        }
        string result = (pendingOnly ? "PASS " : "PASS filesystem-denied save retains working bytes and edit state with an incomplete manifest; ")
            + "pending new annotation and existing page-text recovery through live editors, public recovery entry, saved PDF text and reopened UIA provider. Offscreen WPF only; no native crash or desktop input. App SHA256="
            + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        return 0;
    }

    private static void VerifyIncompleteRecovery(Window owner, string folder)
    {
        string source = Path.Combine(folder, "incomplete-source.pdf");
        byte[] fixture = Fixture();
        File.WriteAllBytes(source, fixture);
        Open(owner, source);
        var viewer = (FrameworkElement)owner.FindName("Viewer");
        Canvas page = Children(viewer).OfType<Canvas>().First(canvas => Equals(canvas.Tag, 0) && canvas.Width > 0);
        ((Button)owner.FindName("ToolTextBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        viewer.GetType().GetMethod("PlaceTextAtPosition", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewer, [new Point(page.Width * .2, page.Height * .5), 0]);
        Wait(() => Children(viewer).OfType<TextBox>().Any(box => box.AcceptsReturn && Equals(box.Tag, 0)), "failure fixture editor");
        TextBox editor = Children(viewer).OfType<TextBox>().Single(box => box.AcceptsReturn && Equals(box.Tag, 0));
        editor.Text = "COMMITTED BEFORE FAILED RECOVERY";
        ((Button)owner.FindName("ToolSelectBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ((Button)owner.FindName("ToolTextBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        viewer.GetType().GetMethod("PlaceTextAtPosition", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(viewer, [new Point(page.Width * .2, page.Height * .7), 0]);
        editor = Children(viewer).OfType<TextBox>().Single(box => box.AcceptsReturn && Equals(box.Tag, 0));
        editor.Text = "PENDING BEFORE FAILED RECOVERY";

        // Read the isolated app's owned directory only to set up a real filesystem failure.
        // The observable operation remains the same public recovery call used by the app.
        string temp = (string)owner.GetType().Assembly.GetType("KillerPDF.App", true)!
            .GetProperty("TempDir", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var directory = new DirectoryInfo(temp);
        if (!directory.Name.StartsWith("session-", StringComparison.Ordinal)
            || directory.Parent?.Name != "Temp"
            || directory.Parent.Parent?.Name.StartsWith("KillerPDF-startup-smoke-", StringComparison.Ordinal) != true
            || !Path.GetFullPath(temp).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The failure fixture is outside the isolated smoke directory.");
        // Only the access list changes: rewriting the whole descriptor would also write the audit
        // list, which needs a privilege a standard user does not hold, and an unmodified copy
        // persists nothing, so the rule is removed from the same object afterwards.
        DirectorySecurity security = directory.GetAccessControl(AccessControlSections.Access);
        var deny = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny);
        security.AddAccessRule(deny);
        string? manifest;
        try
        {
            directory.SetAccessControl(security);
            manifest = (string?)owner.GetType().GetMethod("PrepareRenderFailureRecovery", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(owner, null);
        }
        finally
        {
            security.RemoveAccessRuleSpecific(deny);
            directory.SetAccessControl(security);
        }
        if (manifest is null) throw new InvalidOperationException("Failed save discarded the document instead of reporting incomplete recovery.");
        using JsonDocument recovery = JsonDocument.Parse(File.ReadAllBytes(manifest));
        JsonElement record = recovery.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("OriginalPath").GetString() == source);
        if (!record.TryGetProperty("RecoveryError", out JsonElement error) || string.IsNullOrWhiteSpace(error.GetString()))
            throw new InvalidOperationException("Failed save was not recorded as incomplete recovery.");
        string working = record.GetProperty("RecoveredPath").GetString()!;
        string state = record.GetProperty("StatePath").GetString()!;
        if (!Extract(File.ReadAllBytes(working)).Contains("ORIGINAL BODY TEXT", StringComparison.Ordinal))
            throw new InvalidOperationException("Failed recovery did not retain readable working PDF bytes.");
        string savedState = File.ReadAllText(state);
        if (!savedState.Contains("COMMITTED BEFORE FAILED RECOVERY", StringComparison.Ordinal)
            || !savedState.Contains("PENDING BEFORE FAILED RECOVERY", StringComparison.Ordinal))
            throw new InvalidOperationException("Failed recovery discarded available annotation or pending editor state.");
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(fixture))
            throw new InvalidOperationException("Failed recovery modified the original document.");
        CloseWithoutSaving(owner);
        Console.WriteLine("PASS denied full save retained working PDF, annotations, pending editor state and incomplete manifest.");
    }

    private static string ReadDocument(Window owner)
    {
        FrameworkElement? surface = Children((DependencyObject)owner.FindName("Viewer"))
            .OfType<FrameworkElement>().FirstOrDefault(element => element.GetType().Name == "DocumentSurface");
        if (surface is null) return string.Empty;
        return (UIElementAutomationPeer.CreatePeerForElement(surface)?.GetPattern(PatternInterface.Text) as ITextProvider)
            ?.DocumentRange.GetText(-1) ?? string.Empty;
    }

    private static void Open(Window owner, string path)
    {
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [path]);
        Wait(() => Children((DependencyObject)owner.FindName("Viewer")).OfType<Canvas>()
            .Any(canvas => Equals(canvas.Tag, 0) && canvas.Width > 0), "page render");
        Pump();
    }

    private static void CloseWithoutSaving(Window owner)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            foreach (Window message in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                string discard = (string)Application.Current.FindResource("Str_Btn_DontSave");
                Button? button = Children(message).OfType<Button>().FirstOrDefault(candidate => Equals(candidate.Content, discard));
                if (button is not null) button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else message.Close();
            }
        };
        timer.Start();
        try { ((Button)owner.FindName("CloseFileBtn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(); }
        finally { timer.Stop(); }
    }

    private static string Extract(byte[] bytes)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(bytes)])!;
        object reader = Activator.CreateInstance(engine.GetType("KillerPdf.Engine.Documents.PdfPageContentReader", true)!, [document])!;
        object content = reader.GetType().GetMethod("Read")!.Invoke(reader, [0, CancellationToken.None])!;
        return (string)content.GetType().GetProperty("Text")!.GetValue(content)!;
    }

    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Children(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void Wait(Func<bool> condition, string stage)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException(stage);
            Pump();
            Thread.Sleep(10);
        }
    }

    private static byte[] Fixture()
    {
        string stream = "BT /F1 14 Tf 40 250 Td (ORIGINAL BODY TEXT) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length " + stream.Length + " >>\nstream\n" + stream + "\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(index + 1).Append(" 0 obj\n").Append(objects[index]).Append("\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
