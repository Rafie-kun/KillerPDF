using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using static DocumentStateSmoke;

internal static class ReplacementOccurrenceSmoke
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    internal static int Run(Window owner, string folder) => FormWorkflowFailureSmoke.WithCleanup(owner, () =>
    {
        string source = Path.Combine(folder, "banana.pdf");
        File.WriteAllBytes(source, CliReplacementSmoke.Fixture("BT /F1 14 Tf 20 250 Td (banana) Tj ET"));
        Call(owner, "OpenFromExternal", source);
        owner.Opacity = 1; owner.Activate();
        Wait(() => Descendants<Canvas>(owner).Any(IsPageCanvas), "occurrence source rendered");
        Call(owner, "ShowReplaceBar");
        object search = Get(owner, "Search");
        ((TextBox)Get(owner, "_searchBox")).Text = "an";
        ((TextBox)Get(owner, "_replaceBox")).Text = "X";
        Wait(() => Equals(Get(search, "LastQuery"), "an") && !(bool)Get(search, "IsSearching"), "occurrence search finished");
        int count = ((ICollection)Get(search, "_matches")).Count;
        Console.WriteLine("CONTROL occurrence count=" + count);
        if (count != 2) throw new InvalidOperationException("Find did not distinguish both occurrences in banana.");
        Call(owner, "ReplaceCurrentMatch");
        Wait(() => ReadText((string)Get(owner, "_currentFile")) != "banana", "selected occurrence replaced");
        string text = ReadText((string)Get(owner, "_currentFile"));
        if (text != "bXana") throw new InvalidOperationException("Replace Current changed more than its selected occurrence: " + text);
        Pump();
        Wait(() => ((Button)owner.FindName("UndoBtn")).IsEnabled, "occurrence undo ready");
        Click(owner, "UndoBtn"); Pump();
        Wait(() => ReadText((string)Get(owner, "_currentFile")) == "banana", "occurrence undo restored original");
        Call(search, "Run", "an");
        Wait(() => !(bool)Get(search, "IsSearching") && ((ICollection)Get(search, "_matches")).Count == 2, "occurrences refreshed after undo");
        Call(search, "Next");
        Call(owner, "ReplaceCurrentMatch");
        Wait(() => ReadText((string)Get(owner, "_currentFile")) != "banana", "second occurrence replaced");
        if (ReadText((string)Get(owner, "_currentFile")) != "banXa")
            throw new InvalidOperationException("Replace Current did not preserve the earlier occurrence when the second was selected.");
        Pump();
        Wait(() => ((Button)owner.FindName("UndoBtn")).IsEnabled, "second occurrence undo ready");
        Click(owner, "UndoBtn"); Pump();
        Wait(() => ReadText((string)Get(owner, "_currentFile")) == "banana", "second occurrence undo restored original");
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

    private static object Get(object target, string name) => target.GetType().GetProperty(name, Members)?.GetValue(target)
        ?? target.GetType().GetField(name, Members)?.GetValue(target)
        ?? throw new InvalidOperationException("Missing occurrence state: " + name);
    private static object? Call(object target, string name, params object[] arguments) =>
        target.GetType().GetMethod(name, Members)!.Invoke(target, arguments);
}
