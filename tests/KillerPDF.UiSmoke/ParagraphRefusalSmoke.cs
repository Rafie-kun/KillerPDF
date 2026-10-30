using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class ParagraphRefusalSmoke
{
    internal static int Run(Window owner, string folder)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        Type documentType = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!;
        Type editor = engine.GetType("KillerPdf.Engine.Documents.PdfParagraphEditor", true)!;
        object Open(byte[] bytes) => documentType.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!
            .Invoke(null, [new ReadOnlyMemory<byte>(bytes)])!;
        int Candidates(object document) => ((System.Collections.IEnumerable)editor.GetMethod("FindParagraphs")!
            .Invoke(null, [document, 0])!).Cast<object>().Count();
        const string kind = "rotated";
        byte[] original = ParagraphEditSmoke.Fixture(), unsupported = ParagraphEditSmoke.Fixture(rotated: true);
        if (Candidates(Open(original)) != 2 || Candidates(Open(unsupported)) != 0)
            throw new InvalidOperationException("Independent ordinary/" + kind + " paragraph candidate controls failed.");
        Console.WriteLine("CONTROL ordinary fixture offers two paragraphs; " + kind + " first page offers none.");
        string path = Path.Combine(folder, kind + "-paragraph.pdf");
        File.WriteAllBytes(path, unsupported);
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [path]);
        Wait(() => ((ListBox)owner.FindName("PageList")).Items.Count == 2, kind + " paragraph sidebar");
        string message = "";
        Exception? failure = null;
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (!seen.Add(dialog)) continue;
                try
                {
                    if (dialog.GetType().Name == "ParagraphEditDialog")
                        throw new InvalidOperationException("The " + kind + " paragraph unexpectedly opened an editor.");
                    message = string.Join(" ", Descendants<TextBlock>(dialog).Select(text => text.Text));
                    Console.WriteLine("CONTROL paragraph refusal: " + message);
                    Button okay = Descendants<Button>(dialog).Single(button => button.IsDefault);
                    dialog.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                        (Action)(() => okay.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
                }
                catch (Exception error) { failure = error; dialog.Close(); }
            }
        };
        timer.Start();
        try { ParagraphEditSmoke.RunSearchAction(owner, "Str_Paragraph_Title", "Alt+Shift+P"); }
        finally { timer.Stop(); }
        if (failure is not null) throw failure;
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(unsupported)) throw new InvalidOperationException("Paragraph refusal modified its source.");
        File.WriteAllText(Path.Combine(folder, "refusal.txt"), message);
        if (!message.Contains("rotat", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("UI-004: " + kind + " paragraph refusal discarded the specific reason.");
        string result = "PASS verify-paragraph-refusal: " + kind + " page shows a specific refusal and preserves source bytes.";
        File.WriteAllText(Path.Combine(folder, "result.txt"), result); Console.WriteLine(result);
        return 0;
    }
}
