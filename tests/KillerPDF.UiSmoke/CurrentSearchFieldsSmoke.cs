using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using static DocumentStateSmoke;

internal static class CurrentSearchFieldsSmoke
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static int Run(Window owner, string folder) => FormWorkflowFailureSmoke.WithCleanup(owner, () =>
    {
        string source = Path.Combine(folder, "shared-fields.pdf");
        File.WriteAllBytes(source, Fixture());
        Call(owner, "OpenFromExternal", source);
        owner.Opacity = 1; owner.Activate();
        Wait(() => Descendants<TextBox>(owner).Count(box => box.Text == "Original") == 2, "both logical-field widgets rendered");
        Descendants<TextBox>(owner).First(box => box.Text == "Original").Text = "Sharedword Sharedword";
        Call(owner, "ShowReplaceBar");
        object search = Get(owner, "Search");
        object viewer = Get(owner, "ActiveViewer");
        void Find(string query, int expected)
        {
            ((TextBox)Get(owner, "_searchBox")).Text = query;
            Call(search, "Run", query);
            Wait(() => !(bool)Get(search, "IsSearching"), "field source search completed");
            int actual = ((ICollection)Get(search, "_matches")).Count;
            if (actual != expected) throw new InvalidOperationException($"Expected {expected} field occurrences, found {actual} for {query}.");
        }
        Find("Sharedword", 4);
        ((TextBox)Get(owner, "_replaceBox")).Text = "Changed";
        Call(owner, "ReplaceAllMatches");
        Wait(() => Descendants<TextBox>(owner).Count(box => box.Text == "Changed Changed") == 2, "logical-field replacement mirrored once");
        Wait(() => ((Button)owner.FindName("UndoBtn")).IsEnabled, "shared field Undo ready");
        Click(owner, "UndoBtn"); Pump();
        Wait(() => Descendants<TextBox>(owner).Count(box => box.Text == "Sharedword Sharedword") == 2, "shared field Undo restored both widgets");
        Console.WriteLine("PASS multiple widgets share one Replace All value and Undo.");

        ComboBox choice = Descendants<ComboBox>(owner).Single(box => box.Tag as string == "FormFieldOverlay");
        choice.SelectedValue = "b";
        Find("Beta option", 1);
        Call(owner, "ReplaceAllMatches");
        if (!Equals(((IDictionary)Get(viewer, "_formChoiceValues"))["choice"], "b")
            || ((IDictionary)Get(viewer, "_formTextValues")).Contains("choice"))
            throw new InvalidOperationException("Find/Replace changed a choice export mapping.");
        Find("Immutable", 1);
        Call(owner, "ReplaceAllMatches");
        if (((IDictionary)Get(viewer, "_formTextValues")).Contains("readonly"))
            throw new InvalidOperationException("Replace All changed a read-only field.");
        Console.WriteLine("PASS current choice display text, preserved export mapping and read-only refusal.");

        Descendants<PasswordBox>(owner).Single().Password = "PendingSecret";
        Find("PendingSecret", 0);
        CurrentDocumentSearchSmoke.ReviewAndMark(owner, viewer, "PendingSecret", expectedRows: 1);
        if (((ICollection)Get(viewer, "_redactMarks")).Count != 1)
            throw new InvalidOperationException("Pending password text was missed by redaction review.");
        Console.WriteLine("PASS masked password omitted from ordinary Find and included in explicit redaction review.");
        return 0;
    }, dismissDuringRun: false);

    private static byte[] Fixture()
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 7 0 R 8 0 R 9 0 R] /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 10 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Annots [5 0 R 6 0 R 7 0 R 8 0 R 9 0 R] >>",
            "<< /FT /Tx /T (shared) /V (Original) /Kids [5 0 R 6 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /Rect [20 350 280 375] /P 3 0 R >>",
            "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /Rect [20 300 280 325] /P 3 0 R >>",
            "<< /Type /Annot /Subtype /Widget /FT /Ch /Ff 131072 /T (choice) /Opt [[(a) (Alpha option)] [(b) (Beta option)]] /V (a) /Rect [20 240 280 265] /P 3 0 R >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /Ff 8192 /T (password) /V (Hiddenoriginal) /Rect [20 180 280 205] /P 3 0 R >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /Ff 1 /T (readonly) /V (Immutable) /Rect [20 120 280 145] /P 3 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        ];
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string item in objects) { offsets.Add(text.Length); text.Append($"{offsets.Count} 0 obj\n{item}\nendobj\n"); }
        int xref = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }

    private static object Get(object target, string name) => target.GetType().GetProperty(name, Members)?.GetValue(target)
        ?? target.GetType().GetField(name, Members)?.GetValue(target)
        ?? throw new InvalidOperationException("Missing field search state: " + name);
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Members)!.Invoke(target, args);
}
