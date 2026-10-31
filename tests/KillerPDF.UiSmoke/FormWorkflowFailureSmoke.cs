using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class FormWorkflowFailureSmoke
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] Pending = ["_formTextValues", "_formChoiceValues", "_formMultiChoiceValues",
        "_formCheckValues", "_formRadioValues", "_formFontSizes"];

    internal static int Run(Window owner, string folder, bool deletion = false)
        => WithCleanup(owner, () => deletion ? RunDeletion(owner, folder) : RunCore(owner, folder));

    internal static int WithCleanup(Window owner, Func<int> run, bool dismissDuringRun = true)
    {
        var dialogs = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
        dialogs.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                Console.WriteLine("CONTROL workflow dialog: " + string.Join(" ", Descendants<TextBlock>(dialog).Select(text => text.Text)));
                Button? dismiss = Descendants<Button>(dialog).FirstOrDefault(button =>
                    Equals(button.Content, owner.FindResource("Str_Btn_DontSave")))
                    ?? Descendants<Button>(dialog).FirstOrDefault(button => button.IsDefault);
                if (dismiss is not null) dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else dialog.Close();
            }
        };
        if (dismissDuringRun) dialogs.Start();
        try { return run(); }
        finally
        {
            dialogs.Start();
            try { owner.Close(); }
            finally { dialogs.Stop(); }
        }
    }

    private static int RunCore(Window owner, string folder)
    {
        string source = Path.Combine(folder, "workflow.pdf");
        byte[] original = Fixture();
        File.WriteAllBytes(source, original);
        owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [source]);
        owner.Opacity = 1;
        owner.Activate();
        Wait(() => Descendants<TextBox>(owner).Any(box => box.Text == "Original"), "workflow form rendered");
        object viewer = Get(owner, "ActiveViewer");
        ((IDictionary)Get(viewer, Pending[0]))["answer"] = "Sensitive pending value";
        ((IDictionary)Get(viewer, Pending[1]))["choice"] = "B";
        ((IDictionary)Get(viewer, Pending[2]))["multi"] = new List<string> { "A", "B" };
        ((IDictionary)Get(viewer, Pending[3]))["check"] = true;
        ((IDictionary)Get(viewer, Pending[4]))["radio"] = "B";
        ((IDictionary)Get(viewer, Pending[5]))["answer"] = 18d;
        string snapshot = (string)Call(owner, "CreateExchangeSnapshot")!;
        string[] before = Pending.Select(name => State(Get(viewer, name))).ToArray();
        object document = Get(owner, "_doc");
        string current = (string)Get(owner, "_currentFile");
        byte[] working = File.ReadAllBytes(current);
        bool dirty = Dirty(owner);
        int undo = ((ICollection)Get(viewer, "_undoStack")).Count;
        int redo = ((ICollection)Get(viewer, "_redoStack")).Count;
        Action<string> corruptCandidate = path => File.WriteAllText(path, "not a PDF");
        bool failed = false;
        try { Call(owner, "ApplyFormWorkflow", corruptCandidate, snapshot); }
        catch (TargetInvocationException error) when (error.InnerException is not OutOfMemoryException)
        {
            failed = true;
            Console.WriteLine("CONTROL candidate transform completed; reopen failed: " + error.InnerException!.GetType().Name);
        }
        if (!failed) throw new InvalidOperationException("The candidate reopen fault was not reached.");
        var lost = Pending.Where((name, index) => before[index] != State(Get(viewer, name))).ToArray();
        Console.WriteLine("CONTROL changed pending dictionaries: " + string.Join(", ", lost));
        if (lost.Length != 0 || !ReferenceEquals(document, Get(owner, "_doc"))
            || current != (string)Get(owner, "_currentFile") || dirty != Dirty(owner)
            || undo != ((ICollection)Get(viewer, "_undoStack")).Count
            || redo != ((ICollection)Get(viewer, "_redoStack")).Count
            || !working.AsSpan().SequenceEqual(File.ReadAllBytes(current))
            || !original.AsSpan().SequenceEqual(File.ReadAllBytes(source)))
            throw new InvalidOperationException("Failed form workflow changed live values, document bytes, dirty state or history.");
        Console.WriteLine("PASS failed form candidate preserves all six dictionaries, live document, original/working bytes, dirty state and undo/redo.");
        Console.WriteLine("CONTROL successful workflow retry begins.");
        Call(owner, "ApplyFormWorkflow", (Action<string>)(_ => { }), snapshot);
        Console.WriteLine("CONTROL successful workflow published.");
        if (Pending.Any(name => ((IDictionary)Get(viewer, name)).Count != 0))
            throw new InvalidOperationException("Successful workflow retained values already published in the document.");
        Pump();
        Wait(() => ((Button)owner.FindName("UndoBtn")).IsEnabled, "published workflow undo enabled");
        Click(owner, "UndoBtn");
        Pump();
        Wait(() => Descendants<TextBox>(owner).Any(box => box.Text == "Sensitive pending value"), "undo restores entered form text");
        Wait(() => ((Button)owner.FindName("RedoBtn")).IsEnabled, "workflow redo enabled");
        Click(owner, "RedoBtn");
        Pump();
        Wait(() => Descendants<TextBox>(owner).Any(box => box.Text == "Sensitive pending value"), "redo retains entered form text");
        Console.WriteLine("PASS successful form workflow publishes once, clears pending state and preserves entered text through undo/redo.");
        return 0;
    }

    private static string State(object value) => JsonSerializer.Serialize(value, value.GetType());


    private static int RunDeletion(Window owner, string folder)
    {
        const string name = "answer_001";
        const string sensitive = "Deleted sensitive value";
        string source = Path.Combine(folder, "delete-recreate.pdf");
        File.WriteAllBytes(source, Fixture(name));
        owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [source]);
        owner.Opacity = 1;
        owner.Activate();
        Wait(() => Descendants<TextBox>(owner).Any(box => Equals(box.ToolTip, name)), "field to delete rendered");
        object viewer = Get(owner, "ActiveViewer");
        TextBox field = Descendants<TextBox>(owner).Single(box => Equals(box.ToolTip, name));
        field.Focus();
        field.Text = sensitive;
        Pump();
        ((IDictionary)Get(viewer, "_formFontSizes"))[name] = 18d;
        field.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent });
        field.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent });
        Call(viewer, "DeleteSelectedFormField");
        Pump();
        if (Descendants<TextBox>(owner).Any(box => Equals(box.ToolTip, name)))
            throw new InvalidOperationException("The selected field was not deleted.");
        string[] retained = Pending.Where(key => ((IDictionary)Get(viewer, key)).Contains(name)).ToArray();
        Console.WriteLine("CONTROL deleted field pending entries: " + string.Join(", ", retained));
        if (retained.Length != 0) throw new InvalidOperationException("Deleting the field retained its sensitive pending state.");
        Type integration = owner.GetType().Assembly.GetType("KillerPDF.Services.PdfEngineIntegration", true)!;
        string? created = null;
        Action<string> add = path => created = (string)integration.GetMethod("AddTextField", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [path, 0, 20d, 20d, 180d, 25d, null])!;
        MethodInfo reload = owner.GetType().GetMethod("SaveTempAndReload", Members)!;
        object?[] arguments = reload.GetParameters().Select(parameter => parameter.DefaultValue).ToArray();
        arguments[0] = true;
        arguments[1] = true;
        arguments[2] = add;
        arguments[4] = 0;
        arguments[6] = true;
        reload.Invoke(owner, arguments);
        if (created != name) throw new InvalidOperationException("Recreation did not exercise reuse of the deleted field name.");
        Pump();
        Wait(() => Descendants<TextBox>(owner).Any(box => Equals(box.ToolTip, "Answer 1")), "recreated field rendered");
        if (Descendants<TextBox>(owner).Single(box => Equals(box.ToolTip, "Answer 1")).Text != "")
            throw new InvalidOperationException("A recreated field resurrected deleted text.");
        Wait(() => ((Button)owner.FindName("UndoBtn")).IsEnabled, "creation undo ready");
        Click(owner, "UndoBtn"); Pump();
        Click(owner, "UndoBtn"); Pump();
        Wait(() => Descendants<TextBox>(owner).Any(box => Equals(box.ToolTip, name) && box.Text == sensitive), "undo deletion restores entered text");
        if (!((IDictionary)Get(viewer, "_formFontSizes")).Contains(name))
            throw new InvalidOperationException("Undo deletion lost pending formatting.");
        Click(owner, "RedoBtn"); Pump();
        Click(owner, "RedoBtn"); Pump();
        Wait(() => Descendants<TextBox>(owner).Any(box => Equals(box.ToolTip, "Answer 1") && box.Text == ""), "redo creation remains empty");
        if (Pending.Any(key => ((IDictionary)Get(viewer, key)).Contains(name)))
            throw new InvalidOperationException("Redo rebound retired state to the recreated field.");
        Console.WriteLine("PASS delete/recreate retires sensitive state and preserves value/formatting through undo/redo.");
        return 0;
    }
    private static object Get(object target, string name) => target.GetType().GetProperty(name, Members)?.GetValue(target)
        ?? target.GetType().GetField(name, Members)?.GetValue(target)
        ?? throw new InvalidOperationException("Missing workflow state: " + name);
    private static object? Call(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, Members)!.Invoke(target, arguments);

    internal static byte[] Fixture(string fieldName = "answer", bool password = false)
    {
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R 6 0 R 7 0 R 8 0 R] /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 12 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Annots [4 0 R 5 0 R 6 0 R 7 0 R 9 0 R 10 0 R] >>",
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({fieldName}) /Ff {(password ? 8192 : 0)} /V (Original) /Rect [20 240 260 265] /P 3 0 R >>",
            "<< /Type /Annot /Subtype /Widget /FT /Ch /T (choice) /Opt [(A) (B)] /V (A) /Rect [20 200 160 225] /P 3 0 R >>",
            "<< /Type /Annot /Subtype /Widget /FT /Ch /T (multi) /Ff 2097152 /Opt [(A) (B)] /V [(A)] /Rect [20 140 160 190] /P 3 0 R >>",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (check) /V /Off /AS /Off /AP << /N << /Off 11 0 R /Yes 11 0 R >> >> /Rect [20 100 40 120] /P 3 0 R >>",
            "<< /FT /Btn /Ff 32768 /T (radio) /V /A /Kids [9 0 R 10 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 8 0 R /AS /A /AP << /N << /Off 11 0 R /A 11 0 R >> >> /Rect [20 60 40 80] /P 3 0 R >>",
            "<< /Type /Annot /Subtype /Widget /Parent 8 0 R /AS /Off /AP << /N << /Off 11 0 R /B 11 0 R >> >> /Rect [60 60 80 80] /P 3 0 R >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 20 20] /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
