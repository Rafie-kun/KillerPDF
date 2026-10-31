using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using static DocumentStateSmoke;

internal static class ReplacementFailureSmoke
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static int Run(Window owner, string folder, bool reopen)
        => FormWorkflowFailureSmoke.WithCleanup(owner, () => RunReplacement(owner, folder, reopen));

    internal static int RunFallback(Window owner, string folder)
        => FormWorkflowFailureSmoke.WithCleanup(owner, () => VerifyFallback(owner, folder));

    private static int VerifyFallback(Window owner, string folder)
    {
        const string content = "BT /F 12 Tf 3 Tr 20 100 Td (Name) Tj ET";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        string source = Path.Combine(folder, "fallback.pdf");
        File.WriteAllBytes(source, Encoding.ASCII.GetBytes(pdf.ToString()));
        owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [source]);
        owner.Opacity = 1;
        owner.Activate();
        Wait(() => Descendants<Canvas>(owner).Any(IsPageCanvas), "fallback source rendered");
        object viewer = Get(owner, "ActiveViewer");
        int UndoCount() => ((ICollection)Get(viewer, "_undoStack")).Count;
        int AnnotationCount() => ((IDictionary)Get(viewer, "_annotations")).Values.Cast<ICollection>().Sum(list => list.Count);
        int undo = UndoCount();
        object result = Call(viewer, "ReplaceTextExt", "Name", "Replaced", false, false, null!)!;
        if ((int)result.GetType().GetProperty("Occurrences")!.GetValue(result)! != 1
            || AnnotationCount() != 2 || UndoCount() != undo + 1)
            throw new InvalidOperationException("Unsupported line fallback did not create one reversible replacement.");
        Pump();
        Wait(() => ((Button)owner.FindName("UndoBtn")).IsEnabled, "fallback undo ready");
        Click(owner, "UndoBtn"); Pump();
        if (AnnotationCount() != 0 || UndoCount() != undo)
            throw new InvalidOperationException("One undo did not remove the fallback replacement.");
        Wait(() => ((Button)owner.FindName("RedoBtn")).IsEnabled, "fallback redo ready");
        Click(owner, "RedoBtn"); Pump();
        if (AnnotationCount() != 2 || UndoCount() != undo + 1)
            throw new InvalidOperationException("One redo did not restore the fallback replacement.");
        Console.WriteLine("PASS unsupported line fallback creates one replacement with one reversible undo/redo.");
        return 0;
    }

    private static int RunReplacement(Window owner, string folder, bool reopen)
    {
        string source = Path.Combine(folder, "replacement.pdf");
        byte[] original = TableExportSmoke.Fixture();
        File.WriteAllBytes(source, original);
        owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [source]);
        owner.Opacity = 1;
        owner.Activate();
        Wait(() => Descendants<Canvas>(owner).Any(IsPageCanvas), "replacement source rendered");
        object viewer = Get(owner, "ActiveViewer");
        object host = Get(viewer, "Host");
        object proxy = DispatchProxy.Create(host.GetType().GetInterfaces().Single(type => type.Name == "IViewerHost"), typeof(ReplacementFaultHost));
        var fault = (ReplacementFaultHost)proxy;
        fault.Target = host;
        fault.Reopen = reopen;
        PropertyInfo hostProperty = viewer.GetType().GetProperty("Host", Members)!;
        string working = (string)Get(owner, "_currentFile");
        byte[] workingBytes = File.ReadAllBytes(working);
        object document = Get(owner, "_doc");
        bool dirty = Dirty(owner);
        int undo = ((ICollection)Get(viewer, "_undoStack")).Count;
        int annotations = ((IDictionary)Get(viewer, "_annotations")).Values.Cast<ICollection>().Sum(list => list.Count);
        object? result = null;
        try
        {
            hostProperty.SetValue(viewer, proxy);
            try { result = Call(viewer, "ReplaceTextExt", "Name", "Replaced", false, false, null!); }
            catch (TargetInvocationException error) when (error.InnerException is IOException or InvalidOperationException)
            { Console.WriteLine("CONTROL replacement aborted: " + error.InnerException!.GetType().Name); }
        }
        finally { hostProperty.SetValue(viewer, host); }
        int occurrences = result is null ? 0 : (int)result.GetType().GetProperty("Occurrences")!.GetValue(result)!;
        int after = ((IDictionary)Get(viewer, "_annotations")).Values.Cast<ICollection>().Sum(list => list.Count);
        Console.WriteLine($"CONTROL replacement fault: injected={fault.Injected}, reopen={reopen}, occurrences={occurrences}, annotations={annotations}/{after}.");
        if (!fault.Injected || occurrences != 0 || annotations != after || dirty != Dirty(owner)
            || undo != ((ICollection)Get(viewer, "_undoStack")).Count
            || !ReferenceEquals(document, Get(owner, "_doc")) || working != (string)Get(owner, "_currentFile")
            || !original.AsSpan().SequenceEqual(File.ReadAllBytes(source))
            || !workingBytes.AsSpan().SequenceEqual(File.ReadAllBytes(working)))
            throw new InvalidOperationException("Failed replacement changed live state or reported unpublished changes as success.");
        Console.WriteLine("PASS failed replacement leaves bytes, overlays, dirty state and undo unchanged without a success count.");
        return 0;
    }

    private static object Get(object target, string name) => target.GetType().GetProperty(name, Members)?.GetValue(target)
        ?? target.GetType().GetField(name, Members)?.GetValue(target)
        ?? throw new InvalidOperationException("Missing workflow state: " + name);
    private static object? Call(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, Members)!.Invoke(target, arguments);
}
