using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using static DocumentStateSmoke;

internal static class LayerStateSmoke
{
    internal static int Run(Window owner, string folder)
    {
        string source = Path.Combine(folder, "layer-state.pdf");
        File.WriteAllBytes(source, Fixture());
        State original = Read(source);
        if (!original.Left || original.Right || original.LeftBase || !original.RightBase)
            throw new InvalidOperationException("Effective layer-visibility fixture controls did not differ from base state.");
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [source]);
        owner.Left = 32; owner.Top = 32; owner.Width = 950; owner.Height = 700; owner.Opacity = 1;
        Wait(() => ((Button)owner.FindName("SidebarLayersTab")).IsEnabled, "layer sidebar readiness");
        Click(owner, "SidebarLayersTab");
        CheckBox? Find(string name) => Descendants<TreeViewItem>(owner)
            .Where(row => Descendants<TextBlock>(row).Any(text => text.Text == name))
            .SelectMany(Descendants<CheckBox>).SingleOrDefault();
        Wait(() => Find("Left") is not null && Find("Right") is not null, "layer sidebar rows");
        bool initialDisplayCorrect = Find("Left")!.IsChecked == true && Find("Right")!.IsChecked == false;
        CheckBox right = Find("Right")!;
        bool requested = right.IsChecked != true;
        right.IsChecked = requested;
        right.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Save(owner);
        State changed = Read(source);
        Console.WriteLine($"CONTROL layers: initialDisplayCorrect={initialDisplayCorrect}, Left={changed.Left}, Right={changed.Right}, requestedRight={requested}, LeftBase={changed.LeftBase}.");
        if (!initialDisplayCorrect || !changed.Left || changed.Right != requested || changed.LeftBase)
            throw new InvalidOperationException("WIRE-002: sidebar visibility or a sibling layer changed outside the requested toggle.");
        Click(owner, "UndoBtn");
        Save(owner);
        if (Read(source) != original) throw new InvalidOperationException("Layer Undo did not restore base and effective visibility.");
        Click(owner, "RedoBtn");
        Save(owner);
        if (Read(source) != changed) throw new InvalidOperationException("Layer Redo did not restore only the requested edit.");
        const string result = "PASS verify-layer-isolation: sidebar effective visibility, targeted sibling preservation and saved Undo/Redo.";
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        Console.WriteLine(result);
        return 0;
    }

    private sealed record State(bool Left, bool Right, bool LeftBase, bool RightBase);

    private static State Read(string path)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(path))])!;
        object info = engine.GetType("KillerPdf.Engine.Documents.PdfOptionalContentReader", true)!.GetMethod("Read")!.Invoke(null, [document])!;
        var visible = ((System.Collections.IEnumerable)info.GetType().GetProperty("ViewVisibleGroupObjectNumbers")!.GetValue(info)!).Cast<int>().ToHashSet();
        object[] groups = ((System.Collections.IEnumerable)info.GetType().GetProperty("Groups")!.GetValue(info)!).Cast<object>().ToArray();
        (bool Visible, bool Base) Group(string name)
        {
            object group = groups.Single(item => (string)item.GetType().GetProperty("Name")!.GetValue(item)! == name);
            int number = (int)group.GetType().GetProperty("ObjectNumber")!.GetValue(group)!;
            return (visible.Contains(number), (bool)group.GetType().GetProperty("IsInitiallyVisible")!.GetValue(group)!);
        }
        var left = Group("Left");
        var right = Group("Right");
        return new(left.Visible, right.Visible, left.Base, right.Base);
    }

    private static byte[] Fixture()
    {
        const string content = "/OC /Left BDC 1 0 0 rg 0 0 100 200 re f EMC /OC /Right BDC 0 0 1 rg 100 0 100 200 re f EMC";
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R /OCProperties << /OCGs [5 0 R 6 0 R] /D << /BaseState /OFF /ON [6 0 R] /OFF [5 0 R] /Order [5 0 R 6 0 R] /AS [<< /Event /View /Category [/View] /OCGs [5 0 R 6 0 R] >>] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Properties << /Left 5 0 R /Right 6 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /OCG /Name (Left) /Usage << /View << /ViewState /ON >> >> >>",
            "<< /Type /OCG /Name (Right) /Usage << /View << /ViewState /OFF >> >> >>"];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int> { 0 };
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length); pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) pdf.Append(offset.ToString("D10")).Append(" 00000 n \n");
        pdf.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
