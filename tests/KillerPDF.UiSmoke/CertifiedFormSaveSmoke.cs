using System.Collections;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class CertifiedFormSaveSmoke
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    internal static int Run(Window owner, string appPath, string folder) => FormWorkflowFailureSmoke.WithCleanup(owner, () =>
    {
        string fixtures = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(appPath)!, "..", "certified-fixtures"));
        foreach (string input in Directory.EnumerateFiles(fixtures, "certified-p*.pdf")
            .Where(path => !path.EndsWith("-filled.pdf", StringComparison.Ordinal)).Order())
        {
            int permission = Path.GetFileName(input).StartsWith("certified-p2-", StringComparison.Ordinal) ? 2 : 3;
            byte[] original = File.ReadAllBytes(input);
            string copy = Path.Combine(folder, Path.GetFileName(input));
            File.WriteAllBytes(copy, original);
            owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [copy]);
            owner.Opacity = 1; owner.Activate();
            Wait(() => Descendants<TextBox>(owner).Any(box => box.Text == "Original"), "genuine certified form rendered");
            Descendants<TextBox>(owner).Single(box => box.Text == "Original").Text = "Permitted update";
            bool removalPrompt = false;
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
                {
                    string text = string.Join(" ", Descendants<TextBlock>(dialog).Select(block => block.Text));
                    Console.WriteLine("CONTROL certified save dialog: " + text);
                    removalPrompt |= text.Contains("signature", StringComparison.OrdinalIgnoreCase);
                    dialog.Close();
                }
            };
            timer.Start();
            try { owner.GetType().GetMethod("SaveInPlace", Members, null, Type.EmptyTypes, null)!.Invoke(owner, null); }
            finally { timer.Stop(); }
            if (removalPrompt) throw new InvalidOperationException("A permitted certified form save asked to remove its signature.");
            byte[] saved = File.ReadAllBytes(copy);
            if (saved.Length <= original.Length || !saved.AsSpan().StartsWith(original))
                throw new InvalidOperationException("The certified save did not append the permitted change to the signed original.");
            Verify(saved);
            Descendants<TextBox>(owner).Single(box => box.Text == "Permitted update").Text = "Second update";
            timer.Start();
            try { owner.GetType().GetMethod("SaveInPlace", Members, null, Type.EmptyTypes, null)!.Invoke(owner, null); }
            finally { timer.Stop(); }
            if (removalPrompt) throw new InvalidOperationException("A repeated certified form save asked to remove its signature.");
            saved = File.ReadAllBytes(copy);
            if (!saved.AsSpan().StartsWith(original)) throw new InvalidOperationException("Repeated Save rewrote the signed revision.");
            Verify(saved);
            Descendants<TextBox>(owner).Single(box => box.Text == "Second update").Text = "Saved as update";
            string savedAs = Path.Combine(folder, "save-as-" + Path.GetFileName(input));
            SignedCopySmoke.SaveAs(owner, owner.GetType().Assembly, savedAs);
            saved = File.ReadAllBytes(savedAs);
            if (!saved.AsSpan().StartsWith(original)) throw new InvalidOperationException("Save As rewrote the signed revision.");
            Verify(saved);
            string reopened = Path.Combine(folder, "reopen-" + Path.GetFileName(input));
            File.WriteAllBytes(reopened, saved);
            owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [reopened]);
            Wait(() => Descendants<TextBox>(owner).Any(box => box.Text == "Saved as update"), "certified value saved and reopened");
            Console.WriteLine($"PASS P={permission} GUI repeated Save and Save As/reopen preserve genuine certification and signed prefix.");
        }
        return 0;
    }, dismissDuringRun: false);

    private static void Verify(byte[] source)
    {
        Assembly engine = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "KillerPdf.Engine");
        Type documentType = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!;
        object document = documentType.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(source)])!;
        object signature = ((IEnumerable)engine.GetType("KillerPdf.Engine.Signing.PdfSignatureReader", true)!
            .GetMethod("Read")!.Invoke(null, [document])!).Cast<object>()
            .Single(signature => (bool)signature.GetType().GetProperty("IsSigned")!.GetValue(signature)!);
        object result = engine.GetType("KillerPdf.Engine.Signing.PdfSignatureVerifier", true)!
            .GetMethod("VerifyIntegrity")!.Invoke(null, [document, signature])!;
        if (!(bool)result.GetType().GetProperty("IsStructurallyValid")!.GetValue(result)!
            || !(bool)result.GetType().GetProperty("IsCryptographicallyValid")!.GetValue(result)!)
            throw new InvalidOperationException("Saved certification is invalid.");
    }
}
