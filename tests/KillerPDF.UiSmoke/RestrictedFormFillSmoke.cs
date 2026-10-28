using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using static DocumentStateSmoke;

// A published form that opens without a password, forbids editing and allows filling opens read-only.
// As in Acrobat, typing into its field and saving through the real Save button writes the value into
// the protected file, which keeps its encryption and permissions and reopens read-only. A form whose
// author also forbids filling still asks for the owner password and leaves the file untouched.
internal static class RestrictedFormFillSmoke
{
    private const string FieldName = "applicant.name";
    private const string Value = "Pat Example";

    // Teardown answers the unsaved-changes prompt with Don't Save, so a failure reports instead of
    // leaving the window waiting on a modal close prompt.
    internal static int Run(Window owner, string folder)
    {
        try { return RunCore(owner, folder); }
        finally
        {
            var cleanup = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            cleanup.Tick += (_, _) =>
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
                {
                    Button? discard = Descendants<Button>(dialog).FirstOrDefault(button =>
                        Equals(button.Content, owner.FindResource("Str_Btn_DontSave")) || Equals(button.Content, owner.FindResource("Str_Btn_Quit")));
                    if (discard is not null) discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                }
            };
            cleanup.Start();
            try { if (owner.IsVisible) owner.Close(); Wait(() => !owner.IsVisible, "owned restricted form cleanup"); }
            catch (InvalidOperationException) { }
            finally { cleanup.Stop(); }
        }
    }

    private static int RunCore(Window owner, string folder)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        Rect work = SystemParameters.WorkArea;
        owner.Left = work.Left + 8; owner.Top = work.Top + 8;
        owner.Width = Math.Min(950, work.Width - 16); owner.Height = Math.Min(700, work.Height - 16);
        owner.Opacity = 1; owner.Activate();
        ShortcutHintsSmoke.Focus(owner);
        string restrictedNotice = (string)owner.FindResource("Str_Restricted_Notice");

        string fillable = Path.Combine(folder, "restricted-fillable.pdf");
        File.WriteAllBytes(fillable, Encrypt(engine, fill: true));
        Open(owner, fillable);
        Wait(() => Notice(owner, restrictedNotice), "restricted form opens read-only with its notice");
        TextBox field = Field(owner);
        field.Focus();
        field.Text = Value;
        Pump();
        Wait(() => Dirty(owner), "typed value is pending on the restricted form");
        // Save confirms the overwrite once. Any other dialog, such as the owner-password prompt, fails.
        var unexpected = new List<string>();
        int confirmations = 0;
        var saveSeen = new HashSet<Window>();
        var saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        saveTimer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (!saveSeen.Add(dialog)) continue;
                Button? yes = Descendants<Button>(dialog).FirstOrDefault(button => Equals(button.Content, owner.FindResource("Str_Btn_Yes")));
                if (yes is not null) { confirmations++; yes.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); continue; }
                unexpected.Add(string.Join(" ", Descendants<TextBlock>(dialog).Select(block => block.Text)));
                dialog.Close();
            }
        };
        saveTimer.Start();
        try
        {
            ShortcutHintsSmoke.Focus(owner);
            ShortcutHintsSmoke.Key(owner, 0x53, 0x11);   // Ctrl+S
            Wait(() => unexpected.Count > 0 || (!Dirty(owner) && Notice(owner, restrictedNotice) && Fields(owner).Any()),
                "saved restricted form reopens read-only and clean");
        }
        finally { saveTimer.Stop(); }
        if (unexpected.Count > 0)
            throw new InvalidOperationException("Saving a fillable restricted form showed: " + unexpected[0]);
        // Ctrl+S saves in place directly; only the toolbar button asks to overwrite first.
        Console.WriteLine($"CONTROL restricted fill save dialogs: confirmations={confirmations}, unexpected=0.");
        (bool encrypted, bool modify, string? value) = Read(engine, fillable);
        Console.WriteLine($"CONTROL restricted fill save: encrypted={encrypted}, modifyAllowed={modify}, value={value}.");
        if (!encrypted || modify || value != Value)
            throw new InvalidOperationException("Saving a fillable restricted form lost its value, encryption or permissions.");
        if (Field(owner).Text != Value)
            throw new InvalidOperationException("The reopened restricted form does not show the saved value.");

        string locked = Path.Combine(folder, "restricted-locked.pdf");
        File.WriteAllBytes(locked, Encrypt(engine, fill: false));
        byte[] lockedBytes = File.ReadAllBytes(locked);
        Open(owner, locked);
        Wait(() => Notice(owner, restrictedNotice), "locked form opens read-only with its notice");
        TextBox lockedField = Field(owner);
        lockedField.Focus();
        lockedField.Text = Value;
        Pump();
        string prompt = (string)owner.FindResource("Str_Restricted_Prompt");
        string? asked = null;
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (!seen.Add(dialog)) continue;
                Button? yes = Descendants<Button>(dialog).FirstOrDefault(button => Equals(button.Content, owner.FindResource("Str_Btn_Yes")));
                if (yes is not null) { yes.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); continue; }
                // The prompt's message is built from formatted runs, so it is recognized by its Unlock button.
                bool ownerPrompt = Descendants<Button>(dialog).Any(button => Equals(button.Content, owner.FindResource("Str_Restricted_Unlock")));
                asked ??= ownerPrompt ? prompt : string.Join(" ", Descendants<TextBlock>(dialog).Select(block => block.Text));
                dialog.Close();
            }
        };
        timer.Start();
        try
        {
            ShortcutHintsSmoke.Focus(owner);
            ShortcutHintsSmoke.Key(owner, 0x53, 0x11);   // Ctrl+S
            Wait(() => asked is not null, "locked form asks for the owner password");
        }
        finally { timer.Stop(); }
        Pump();
        if (!asked!.Contains(prompt, StringComparison.Ordinal))
            throw new InvalidOperationException("Saving a form that forbids filling showed: " + asked);
        if (!File.ReadAllBytes(locked).AsSpan().SequenceEqual(lockedBytes))
            throw new InvalidOperationException("Saving a form that forbids filling changed the file.");
        Console.WriteLine("CONTROL locked form: Save asked for the owner password; file unchanged.");
        Console.WriteLine("PASS restricted form filling: a form that allows filling saves its value and keeps its encryption and permissions; one that forbids filling still needs the owner password.");
        return 0;
    }

    private static byte[] Encrypt(Assembly engine, bool fill)
    {
        string appearance = "q 1 1 1 rg 0 0 160 24 re f Q\n";
        string[] objects = [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DA (/Helv 12 Tf 0 g) /DR << /Font << /Helv 4 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << >> /Annots [5 0 R] >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Type /Annot /Subtype /Widget /FT /Tx /T ({FieldName}) /V () /Rect [20 126 180 150] /P 3 0 R /F 4 /DA (/Helv 12 Tf 0 g) /AP << /N 6 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 160 24] /Length {appearance.Length} >>\nstream\n{appearance}endstream"];
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

        Type documentType = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!;
        object document = documentType.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!
            .Invoke(null, [new ReadOnlyMemory<byte>(Encoding.Latin1.GetBytes(pdf.ToString()))])!;
        Type optionsType = engine.GetType("KillerPdf.Engine.Security.PdfPasswordEncryptionOptions", true)!;
        object options = Activator.CreateInstance(optionsType)!;
        optionsType.GetProperty("UserPassword")!.SetValue(options, string.Empty);
        optionsType.GetProperty("OwnerPassword")!.SetValue(options, "owner");
        optionsType.GetProperty("AllowDocumentModification")!.SetValue(options, false);
        optionsType.GetProperty("AllowFormFilling")!.SetValue(options, fill);
        optionsType.GetProperty("AllowAnnotationModification")!.SetValue(options, false);
        MethodInfo encrypt = engine.GetType("KillerPdf.Engine.Writing.PdfDocumentEncryptionWriter", true)!
            .GetMethod("EncryptWithPasswords", [documentType, optionsType, typeof(bool)])!;
        return (byte[])encrypt.Invoke(null, [document, options, false])!;
    }

    private static (bool Encrypted, bool Modify, string? Value) Read(Assembly engine, string path)
    {
        Type documentType = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!;
        object document = documentType.GetMethod("Open", [typeof(ReadOnlyMemory<byte>), typeof(string)])!
            .Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(path)), string.Empty])!;
        bool encrypted = (bool)documentType.GetProperty("IsEncrypted")!.GetValue(document)!;
        object permissions = documentType.GetProperty("DeclaredPermissions")!.GetValue(document)!;
        bool modify = (bool)permissions.GetType().GetProperty("AllowDocumentModification")!.GetValue(permissions)!;
        object[] widgets = ((System.Collections.IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfFormWidgetReader", true)!
            .GetMethod("ReadPage")!.Invoke(null, [document, 0])!).Cast<object>().ToArray();
        string? value = (string?)widgets.Single().GetType().GetProperty("Value")!.GetValue(widgets.Single());
        return (encrypted, modify, value);
    }

    // Opening a protected file can show a modal dialog, which would block the open call itself, so a
    // watcher logs and confirms any dialog that appears while the file opens.
    private static void Open(Window owner, string path)
    {
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (!seen.Add(dialog)) continue;
                Console.WriteLine("CONTROL dialog while opening: " + string.Join(" ", Descendants<TextBlock>(dialog).Select(block => block.Text)));
                Button? ok = Descendants<Button>(dialog).FirstOrDefault(button =>
                    Equals(button.Content, owner.FindResource("Str_Btn_OK")) || Equals(button.Content, owner.FindResource("Str_Btn_Yes")));
                if (ok is not null) ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else dialog.Close();
            }
        };
        timer.Start();
        try
        {
            owner.GetType().GetMethod("OpenFromExternal", [typeof(string)])!.Invoke(owner, [path]);
            Wait(() => Descendants<Canvas>(owner).Any(IsPageCanvas), "restricted form rendered");
            Wait(() => Fields(owner).Any(), "restricted form field shown");
        }
        finally { timer.Stop(); }
    }

    private static TextBox Field(Window owner) => Fields(owner).Single();

    private static IEnumerable<TextBox> Fields(Window owner) =>
        Children(owner).OfType<TextBox>().Where(box => box.IsVisible && Equals(box.ToolTip, FieldName));

    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (DependencyObject child in Children(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static bool Notice(Window owner, string text) =>
        Descendants<TextBlock>(owner).Any(block => block.IsVisible && block.Text == text);
}
