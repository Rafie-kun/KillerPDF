using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using static DocumentStateSmoke;

internal static class ReplacementOccurrenceSmoke
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static int Run(Window owner, string folder, bool normalized = false) => FormWorkflowFailureSmoke.WithCleanup(owner, () =>
    {
        string original = normalized ? "O\uFB00ner O\uFB00ner" : "banana";
        string query = normalized ? "Offner" : "an";
        string replacement = normalized ? "Done" : "X";
        string source = Path.Combine(folder, "banana.pdf");
        File.WriteAllBytes(source, CliReplacementSmoke.Fixture("BT /F1 14 Tf 20 250 Td ("
            + (normalized ? "OFner OFner" : "banana") + ") Tj ET", normalized ? "<46> <FB00>" : null));
        Call(owner, "OpenFromExternal", source);
        owner.Opacity = 1; owner.Activate();
        Wait(() => Descendants<Canvas>(owner).Any(IsPageCanvas), "occurrence source rendered");
        Call(owner, "ShowReplaceBar");
        object search = Get(owner, "Search");
        ((TextBox)Get(owner, "_searchBox")).Text = query;
        ((TextBox)Get(owner, "_replaceBox")).Text = replacement;
        Wait(() => Equals(Get(search, "LastQuery"), query) && !(bool)Get(search, "IsSearching"), "occurrence search finished");
        int count = ((ICollection)Get(search, "_matches")).Count;
        Console.WriteLine("CONTROL occurrence count=" + count);
        if (count != 2) throw new InvalidOperationException("Find did not distinguish both text occurrences.");
        Call(owner, "ReplaceCurrentMatch");
        Console.WriteLine("CONTROL replacement serialized=" + ReadText((string)Get(owner, "_currentFile"))
            + "; overlays=" + OverlayText(owner));
        Wait(() => ReadText((string)Get(owner, "_currentFile")) != original, "selected occurrence replaced");
        string text = ReadText((string)Get(owner, "_currentFile"));
        if (text != (normalized ? "Done O\uFB00ner" : "bXana")) throw new InvalidOperationException("Replace Current changed more than its selected occurrence: " + text);
        Pump();
        Wait(() => ((Button)owner.FindName("UndoBtn")).IsEnabled, "occurrence undo ready");
        Click(owner, "UndoBtn"); Pump();
        Wait(() => ReadText((string)Get(owner, "_currentFile")) == original, "occurrence undo restored original");
        Call(search, "Run", query);
        Wait(() => !(bool)Get(search, "IsSearching") && ((ICollection)Get(search, "_matches")).Count == 2, "occurrences refreshed after undo");
        Call(search, "Next");
        Call(owner, "ReplaceCurrentMatch");
        Wait(() => ReadText((string)Get(owner, "_currentFile")) != original, "second occurrence replaced");
        if (ReadText((string)Get(owner, "_currentFile")) != (normalized ? "O\uFB00ner Done" : "banXa"))
            throw new InvalidOperationException("Replace Current did not preserve the earlier occurrence when the second was selected.");
        Pump();
        Wait(() => ((Button)owner.FindName("UndoBtn")).IsEnabled, "second occurrence undo ready");
        Click(owner, "UndoBtn"); Pump();
        Wait(() => ReadText((string)Get(owner, "_currentFile")) == original, "second occurrence undo restored original");
        Console.WriteLine("PASS Find counts each occurrence, Replace Current edits exactly one and Undo restores the word.");
        return 0;
    });

    private static string ReadText(string path)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(path))])!;
        Type readerType = engine.GetType("KillerPdf.Engine.Documents.PdfPageContentReader", true)!;
        ConstructorInfo constructor = readerType.GetConstructors().Single();
        object?[] arguments = constructor.GetParameters().Select(parameter => parameter.DefaultValue).ToArray();
        arguments[0] = document;
        object reader = constructor.Invoke(arguments);
        dynamic page = readerType.GetMethod("Read", [typeof(int), typeof(CancellationToken)])!
            .Invoke(reader, [0, CancellationToken.None])!;
        return string.Join(" ", ((IEnumerable)page.Words).Cast<object>().Select(word => (string)word.GetType().GetProperty("Text")!.GetValue(word)!));
    }

    private static string OverlayText(Window owner) => string.Join(" | ",
        ((IDictionary)Get(Get(owner, "ActiveViewer"), "_annotations")).Values.Cast<IEnumerable>()
        .SelectMany(items => items.Cast<object>()).Select(annotation => annotation.GetType().Name + ":"
            + annotation.GetType().GetProperty("Content", Members)?.GetValue(annotation)));

    private static object Get(object target, string name) => target.GetType().GetProperty(name, Members)?.GetValue(target)
        ?? target.GetType().GetField(name, Members)?.GetValue(target)
        ?? throw new InvalidOperationException("Missing occurrence state: " + name);
    private static object? Call(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, Members)!.Invoke(target, arguments);
}
