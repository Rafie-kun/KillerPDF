using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class CurrentDocumentSearchSmoke
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static int Run(Window owner, string folder) => FormWorkflowFailureSmoke.WithCleanup(owner, () =>
    {
        string source = Path.Combine(folder, "current-text.pdf");
        File.WriteAllBytes(source, CliReplacementSmoke.Fixture("BT /F1 14 Tf 20 250 Td (Original page) Tj ET"));
        Call(owner, "OpenFromExternal", source);
        owner.Opacity = 1; owner.Activate();
        Wait(() => Descendants<Canvas>(owner).Any(IsPageCanvas), "current-search source rendered");
        object viewer = Get(owner, "ActiveViewer");
        object annotation = Activator.CreateInstance(owner.GetType().Assembly.GetType("KillerPDF.TextAnnotation", true)!)!;
        Set(annotation, "PageIndex", 0);
        Set(annotation, "Position", new Point(80, 100));
        Set(annotation, "Content", "Pendingword Pendingword");
        Set(annotation, "Width", 300d);
        Set(annotation, "Height", 60d);
        Call(viewer, "AddAnnotationExt", annotation);
        Call(viewer, "RenderAllAnnotations", 0);
        Call(owner, "ShowReplaceBar");
        object search = Get(owner, "Search");
        ((TextBox)Get(owner, "_searchBox")).Text = "Pendingword";
        ((TextBox)Get(owner, "_replaceBox")).Text = "Changed";
        Wait(() => Equals(Get(search, "LastQuery"), "Pendingword") && !(bool)Get(search, "IsSearching"), "current-search completed");
        int count = ((ICollection)Get(search, "_matches")).Count;
        if (count != 2) throw new InvalidOperationException($"Find returned {count} instead of both newly placed text occurrences.");
        Call(search, "Next");
        Call(owner, "ReplaceCurrentMatch");
        string Text() => string.Join("|", ((IDictionary)Get(viewer, "_annotations")).Values.Cast<IEnumerable>()
            .SelectMany(items => items.Cast<object>()).Select(item => item.GetType().GetProperty("Content")?.GetValue(item)));
        Wait(() => Text().Contains("Pendingword Changed", StringComparison.Ordinal), "selected overlay occurrence replaced");
        Click(owner, "UndoBtn"); Pump();
        if (!Text().Contains("Pendingword Pendingword", StringComparison.Ordinal)) throw new InvalidOperationException("Overlay replace undo did not restore both occurrences.");
        Console.WriteLine("PASS current overlay Find, selected occurrence replacement and Undo.");
        string formSource = Path.Combine(folder, "current-form.pdf");
        File.WriteAllBytes(formSource, FormWorkflowFailureSmoke.Fixture());
        Call(owner, "OpenFromExternal", formSource);
        Wait(() => Descendants<TextBox>(owner).Any(box => box.Text == "Original"), "current form rendered");
        Descendants<TextBox>(owner).Single(box => box.Text == "Original").Text = "Pendingform Pendingform";
        ((TextBox)Get(owner, "_searchBox")).Text = "Pendingform";
        ((TextBox)Get(owner, "_replaceBox")).Text = "Changed";
        Wait(() => Equals(Get(search, "LastQuery"), "Pendingform") && !(bool)Get(search, "IsSearching"), "pending-form search completed");
        count = ((ICollection)Get(search, "_matches")).Count;
        if (count != 2) throw new InvalidOperationException($"Find returned {count} instead of both pending form occurrences.");
        Call(search, "Next");
        Call(owner, "ReplaceCurrentMatch");
        Wait(() => Descendants<TextBox>(owner).Any(box => box.Text == "Pendingform Changed"), "selected form occurrence replaced");
        Wait(() => ((Button)owner.FindName("UndoBtn")).IsEnabled, "form replacement undo available");
        Click(owner, "UndoBtn"); Pump();
        Wait(() => Descendants<TextBox>(owner).Any(box => box.Text == "Pendingform Pendingform"), "form replacement undo");
        Console.WriteLine("PASS pending form Find, selected occurrence replacement and Undo.");
        Call(owner, "ReplaceAllMatches");
        Wait(() => Descendants<TextBox>(owner).Any(box => box.Text == "Changed Changed"), "all pending form occurrences replaced");
        Wait(() => ((Button)owner.FindName("UndoBtn")).IsEnabled, "form Replace All undo available");
        Click(owner, "UndoBtn"); Pump();
        Wait(() => Descendants<TextBox>(owner).Any(box => box.Text == "Pendingform Pendingform"), "form Replace All undo");
        Console.WriteLine("PASS pending form Replace All and Undo.");
        owner.GetType().GetMethod("SaveInPlace", Members, null, Type.EmptyTypes, null)!.Invoke(owner, null);
        Call(search, "Run", "Pendingform");
        Wait(() => !(bool)Get(search, "IsSearching"), "current search refreshed after Save");
        if (((ICollection)Get(search, "_matches")).Count != 2)
            throw new InvalidOperationException("Saving without reopening changed the current form search results.");
        viewer = Get(owner, "ActiveViewer");
        ReviewAndMark(owner, viewer, "Pendingform");
        if (((ICollection)Get(viewer, "_redactMarks")).Count != 1)
            throw new InvalidOperationException("Pending form redaction did not mark its widget exactly once.");
        string working = (string)Get(owner, "_currentFile");
        byte[] before = File.ReadAllBytes(working);
        Descendants<TextBox>(owner).Single(box => box.Text == "Pendingform Pendingform").Text = "Changed since review";
        Call(viewer, "ApplyRedaction");
        if (!File.ReadAllBytes(working).AsSpan().SequenceEqual(before) || ((ICollection)Get(viewer, "_redactMarks")).Count != 1)
            throw new InvalidOperationException("Stale redaction review changed the document or consumed its marks.");
        Descendants<TextBox>(owner).Single(box => box.Text == "Changed since review").Text = "Pendingform Pendingform";
        foreach (bool reopen in new[] { false, true })
        {
            object host = Get(viewer, "Host");
            object proxy = DispatchProxy.Create(host.GetType().GetInterfaces().Single(type => type.Name == "IViewerHost"), typeof(ReplacementFaultHost));
            var fault = (ReplacementFaultHost)proxy;
            fault.Target = host; fault.Reopen = reopen;
            PropertyInfo property = viewer.GetType().GetProperty("Host", Members)!;
            int undoCount = ((ICollection)Get(viewer, "_undoStack")).Count;
            try { property.SetValue(viewer, proxy); Call(viewer, "ApplyRedaction"); }
            finally { property.SetValue(viewer, host); }
            if (!fault.Injected || !File.ReadAllBytes(working).AsSpan().SequenceEqual(before)
                || ((ICollection)Get(viewer, "_redactMarks")).Count != 1
                || ((ICollection)Get(viewer, "_undoStack")).Count != undoCount
                || !Equals(((IDictionary)Get(viewer, "_formTextValues"))["answer"], "Pendingform Pendingform"))
                throw new InvalidOperationException("Failed redaction publication changed live form state or history.");
        }
        Console.WriteLine("PASS Save without reopening, stale-source refusal and write/reopen failure preservation.");
        var finish = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        finish.Tick += (_, _) =>
        {
            foreach (Window window in Application.Current.Windows.Cast<Window>().Where(window => window.GetType().Name == "RedactionFinishDialog").ToArray())
                window.Close();
        };
        finish.Start();
        try { Call(viewer, "ApplyRedaction"); }
        finally { finish.Stop(); }
        if (((IDictionary)Get(viewer, "_formTextValues")).Contains("answer"))
            throw new InvalidOperationException("Applied redaction retained the removed field's pending value.");
        Wait(() => ((Button)owner.FindName("UndoBtn")).IsEnabled, "redaction undo available");
        Click(owner, "UndoBtn"); Pump();
        Wait(() => Descendants<TextBox>(owner).Any(box => box.Text == "Pendingform Pendingform"), "redaction undo restored pending field");
        Wait(() => ((Button)owner.FindName("RedoBtn")).IsEnabled, "redaction redo available");
        Click(owner, "RedoBtn"); Pump();
        if (((IDictionary)Get(viewer, "_formTextValues")).Contains("answer"))
            throw new InvalidOperationException("Redaction redo restored retired pending text.");
        Console.WriteLine("PASS live redaction dialog, pending-field retirement, Undo and Redo.");
        ReplacementFailureSmoke.VerifyFallback(owner, folder);
        viewer = Get(owner, "ActiveViewer");
        foreach (string query in new[] { "Replaced", "Name" })
        {
            ((TextBox)Get(owner, "_searchBox")).Text = query;
            Call(search, "Run", query);
            Wait(() => !(bool)Get(search, "IsSearching"), "cover-replacement search completed");
            int expected = query == "Replaced" ? 1 : 0;
            if (((ICollection)Get(search, "_matches")).Count != expected)
                throw new InvalidOperationException("Cover-replacement search exposed hidden original text or missed the current replacement.");
        }
        owner.GetType().GetMethod("SaveInPlace", Members, null, Type.EmptyTypes, null)!.Invoke(owner, null);
        ((TextBox)Get(owner, "_searchBox")).Text = "Replaced";
        Call(search, "Run", "Replaced");
        Wait(() => !(bool)Get(search, "IsSearching"), "cover search after Save");
        if (((ICollection)Get(search, "_matches")).Count != 1) throw new InvalidOperationException("Saved cover replacement lost its search result.");
        Call(viewer, "MarkSearchResultsForRedaction");
        if (((ICollection)Get(viewer, "_redactMarks")).Count != 1) throw new InvalidOperationException("Search results did not mark the cover replacement.");
        finish.Start();
        try { Call(viewer, "ApplyRedaction"); }
        finally { finish.Stop(); }
        if (((IDictionary)Get(viewer, "_annotations")).Values.Cast<ICollection>().Sum(list => list.Count) != 0)
            throw new InvalidOperationException("Applied replacement redaction retained the text or its cover.");
        owner.GetType().GetMethod("SaveInPlace", Members, null, Type.EmptyTypes, null)!.Invoke(owner, null);
        string saved = Path.Combine(folder, "fallback.pdf");
        File.WriteAllBytes(Path.Combine(folder, "cover-redacted-reopen.pdf"), File.ReadAllBytes(saved));
        Call(owner, "OpenFromExternal", Path.Combine(folder, "cover-redacted-reopen.pdf"));
        Wait(() => Descendants<Canvas>(owner).Any(IsPageCanvas), "saved redaction reopened");
        foreach (string query in new[] { "Name", "Replaced" })
        {
            Call(search, "Run", query);
            Wait(() => !(bool)Get(search, "IsSearching"), "reopened redaction searched");
            if (((ICollection)Get(search, "_matches")).Count != 0) throw new InvalidOperationException("Saved redaction retained covered original or replacement text.");
        }
        Console.WriteLine("PASS current cover replacement, hidden-original exclusion, Save, search-result redaction and reopen.");
        return 0;
    }, dismissDuringRun: false);

    internal static void ReviewAndMark(Window owner, object viewer, string query, int expectedRows = 2)
    {
        Exception? failure = null;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            var dialog = Application.Current.Windows.Cast<Window>().FirstOrDefault(window => window.GetType().Name == "RedactionFindDialog");
            if (dialog is null) return;
            timer.Stop();
            try
            {
                ((TextBox)Get(dialog, "_query")).Text = query;
                Call(dialog, "RunSearch");
                int rows = ((ICollection)Get(dialog, "_rows")).Count;
                if (rows != expectedRows) throw new InvalidOperationException($"Current redaction dialog returned {rows} rows instead of {expectedRows} occurrences.");
                ((Button)Get(dialog, "_mark")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch (Exception error) { failure = error; dialog.Close(); }
        };
        timer.Start();
        try { Call(viewer, "FindTextForRedaction"); }
        finally { timer.Stop(); }
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static object Get(object target, string name) => target.GetType().GetProperty(name, Members)?.GetValue(target)
        ?? target.GetType().GetField(name, Members)?.GetValue(target)
        ?? throw new InvalidOperationException("Missing current-search state: " + name);
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name, Members)!.SetValue(target, value);
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Members)!.Invoke(target, args);
}
