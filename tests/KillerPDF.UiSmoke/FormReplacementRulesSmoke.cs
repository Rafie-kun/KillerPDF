using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using static DocumentStateSmoke;

internal static class FormReplacementRulesSmoke
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static int Run(Window owner, string folder, bool calculation) => FormWorkflowFailureSmoke.WithCleanup(owner, () =>
    {
        string source = Path.Combine(folder, "replacement-rules.pdf");
        File.WriteAllBytes(source, Fixture(calculation));
        Call(owner, "OpenFromExternal", source);
        owner.Opacity = 1; owner.Activate();
        Wait(() => Descendants<TextBox>(owner).Any(box => box.Text == (calculation ? "10" : "John")), "constrained form rendered");
        Call(owner, "ShowReplaceBar");
        TextBox searchBox = (TextBox)Get(owner, "_searchBox");
        if (calculation)
        {
            TextBox quantity = Descendants<TextBox>(owner).Single(box => box.Text == "10");
            quantity.Focus(); Keyboard.Focus(quantity); quantity.Text = "11";
            searchBox.Focus(); Keyboard.Focus(searchBox); Pump();
            Wait(() => Descendants<TextBox>(owner).Any(box => box.Text == "22"), "ordinary edit recalculates the total");
            quantity.Focus(); Keyboard.Focus(quantity); quantity.Text = "10";
            searchBox.Focus(); Keyboard.Focus(searchBox); Pump();
            Wait(() => Descendants<TextBox>(owner).Any(box => box.Text == "20"), "calculation control restored");
        }
        string find = calculation ? "10" : "John";
        searchBox.Text = find;
        ((TextBox)Get(owner, "_replaceBox")).Text = calculation ? "20" : "Christopher";
        object search = Get(owner, "Search");
        Call(search, "Run", find);
        Wait(() => !(bool)Get(search, "IsSearching"), "replacement rule search completed");
        object viewer = Get(owner, "ActiveViewer");
        string working = (string)Get(owner, "_currentFile");
        byte[] before = File.ReadAllBytes(working);
        int undo = ((ICollection)Get(viewer, "_undoStack")).Count;
        string Pending() => System.Text.Json.JsonSerializer.Serialize(Get(viewer, "_formTextValues"));
        string pending = Pending();
        Call(owner, "ReplaceAllMatches");
        if (calculation)
        {
            Wait(() => Descendants<TextBox>(owner).Any(box => box.IsReadOnly && box.Text == "40"), "replacement recalculated dependent total");
            Wait(() => ((Button)owner.FindName("UndoBtn")).IsEnabled, "calculated replacement Undo ready");
            Click(owner, "UndoBtn"); Pump();
            Wait(() => Descendants<TextBox>(owner).Any(box => box.Text == "10")
                && Descendants<TextBox>(owner).Any(box => box.IsReadOnly && box.Text == "20"), "Undo restored input and calculated total together");
            Call(search, "Run", find);
            Wait(() => !(bool)Get(search, "IsSearching"), "calculated form search refreshed after Undo");
            Call(owner, "ReplaceCurrentMatch");
            Wait(() => Descendants<TextBox>(owner).Any(box => box.IsReadOnly && box.Text == "40"), "selected replacement recalculated total");
            owner.GetType().GetMethod("SaveInPlace", Members, null, Type.EmptyTypes, null)!.Invoke(owner, null);
            string reopened = Path.Combine(folder, "calculated-saved-reopen.pdf");
            File.WriteAllBytes(reopened, File.ReadAllBytes(source));
            Call(owner, "OpenFromExternal", reopened);
            Wait(() => Descendants<TextBox>(owner).Any(box => box.IsReadOnly && box.Text == "40")
                && Descendants<TextBox>(owner).Any(box => !box.IsReadOnly && box.Text == "20"), "saved calculation reopened with current input and total");
            Console.WriteLine("PASS calculated field replacement and dependent value Undo.");
            Console.WriteLine("PASS selected calculated replacement, Save and reopen.");
        }
        else
        {
            void Unchanged()
            {
                if (!File.ReadAllBytes(working).AsSpan().SequenceEqual(before) || Pending() != pending
                    || ((ICollection)Get(viewer, "_undoStack")).Count != undo)
                    throw new InvalidOperationException("An overlength replacement changed the base text, pending values or history.");
            }
            Unchanged();
            Call(owner, "ReplaceCurrentMatch");
            Unchanged();
            Console.WriteLine("PASS field length refusal preserves whole-batch and selected-occurrence state.");
        }
        return 0;
    });

    private static byte[] Fixture(bool calculation)
    {
        string content = calculation ? "" : "BT /F1 14 Tf 20 50 Td (John) Tj ET";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R] " + (calculation ? "/CO [5 0 R] " : "")
                + "/DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 7 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Annots [4 0 R 5 0 R] /Resources << /Font << /F1 7 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (" + (calculation ? "quantity" : "short") + ") /V (" + (calculation ? "10" : "John")
                + ") " + (calculation ? "" : "/MaxLen 5 ") + "/Rect [20 250 280 275] /P 3 0 R >>",
            calculation
                ? "<< /Type /Annot /Subtype /Widget /FT /Tx /Ff 1 /T (total) /V (20) /Rect [20 200 280 225] /P 3 0 R /AA << /C << /S /JavaScript /JS (event.value = this.getField\\(\"quantity\"\\).value * 2;) >> >> >>"
                : "<< /Type /Annot /Subtype /Widget /FT /Tx /T (other) /V (John) /Rect [20 200 280 225] /P 3 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
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
        ?? throw new InvalidOperationException("Missing form rule state: " + name);
    private static object? Call(object target, string name, params object[] args) => target.GetType().GetMethod(name, Members)!.Invoke(target, args);
}
