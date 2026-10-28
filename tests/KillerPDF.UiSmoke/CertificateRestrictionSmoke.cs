using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using static DocumentStateSmoke;

// A PDF encrypted for certificate recipients carries the permissions its author sealed for each one.
// A recipient that may not edit gets the file read-only, with a notice that offers no password unlock,
// and Save refuses with the certificate message while the file stays byte for byte as it was. A
// recipient that may edit gets the usual editable copy. The throwaway recipient certificate is added
// to the agent's personal store for the run and removed with its key afterwards.
internal static class CertificateRestrictionSmoke
{
    internal static int Run(Window owner, string folder)
    {
        Assembly app = owner.GetType().Assembly;
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        Rect work = SystemParameters.WorkArea;
        owner.Left = work.Left + 8; owner.Top = work.Top + 8;
        owner.Width = Math.Min(950, work.Width - 16); owner.Height = Math.Min(700, work.Height - 16);
        owner.Opacity = 1; owner.Activate();
        ShortcutHintsSmoke.Focus(owner);
        // A build without the notice must fail on behavior, not on a missing string, so English stands in.
        string certificateNotice = owner.TryFindResource("Str_Restricted_CertificateNotice") as string
            ?? "The certificate this PDF is encrypted for does not permit editing, so it is open read-only.";
        string unlockLabel = (string)owner.FindResource("Str_Restricted_EnterPassword");

        using RSA key = RSA.Create(2048);
        var request = new CertificateRequest("CN=KillerPDF smoke recipient " + Guid.NewGuid().ToString("N"),
            key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyEncipherment, false));
        using X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        using X509Certificate2 installed = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(installed);
        var dialogs = new List<string>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        var seen = new HashSet<Window>();
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (!seen.Add(dialog)) continue;
                dialogs.Add(string.Join(" ", Descendants<TextBlock>(dialog).Select(block => block.Text)));
                Button? close = Descendants<Button>(dialog).FirstOrDefault(button =>
                    Equals(button.Content, owner.FindResource("Str_Btn_OK")) || Equals(button.Content, owner.FindResource("Str_Btn_DontSave")));
                if (close is not null) close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else dialog.Close();
            }
        };
        timer.Start();
        try
        {
            string restricted = Protect(app, engine, folder, installed, mayEdit: false);
            string editable = Protect(app, engine, folder, installed, mayEdit: true);
            byte[] restrictedBytes = File.ReadAllBytes(restricted);

            Open(owner, restricted);
            Wait(() => Notice(owner, certificateNotice), "certificate restriction notice");
            if (Descendants<Button>(owner).Any(button => button.IsVisible && Equals(button.Content, unlockLabel)))
                throw new InvalidOperationException("The certificate notice offers a password unlock that cannot work.");
            Console.WriteLine("CONTROL certificate notice: shown without the owner-password button.");
            int before = dialogs.Count;
            ShortcutHintsSmoke.Focus(owner);
            ShortcutHintsSmoke.Key(owner, 0x53, 0x11);   // Ctrl+S
            Wait(() => dialogs.Count > before, "Save answers on a restricted certificate document");
            if (!dialogs[^1].Contains(certificateNotice, StringComparison.Ordinal))
                throw new InvalidOperationException("Save on a restricted certificate document showed: " + dialogs[^1]);
            Pump();
            if (!File.ReadAllBytes(restricted).AsSpan().SequenceEqual(restrictedBytes))
                throw new InvalidOperationException("Save rewrote the restricted certificate document.");
            Console.WriteLine("CONTROL certificate Save: refused with the certificate message; file unchanged.");

            Open(owner, editable);
            Wait(() => Descendants<Canvas>(owner).Any(IsPageCanvas) && !Notice(owner, certificateNotice)
                && !Notice(owner, (string)owner.FindResource("Str_Restricted_Notice")), "editable certificate document without a notice");
            Console.WriteLine("PASS certificate restriction: a recipient that may not edit opens read-only with an honest notice and no save; one that may edit opens editable.");
            return 0;
        }
        finally
        {
            timer.Stop();
            store.Remove(installed);
            using (RSA? persisted = installed.GetRSAPrivateKey())
                if (persisted is RSACng cng) cng.Key.Delete();
            if (store.Certificates.Find(X509FindType.FindByThumbprint, installed.Thumbprint, false).Count != 0)
                throw new InvalidOperationException("The smoke recipient certificate was not removed.");
            try { if (owner.IsVisible) owner.Close(); } catch (InvalidOperationException) { }
        }
    }

    private static string Protect(Assembly app, Assembly engine, string folder, X509Certificate2 recipient, bool mayEdit)
    {
        string source = Path.Combine(folder, "certificate-source.pdf");
        string output = Path.Combine(folder, mayEdit ? "certificate-editable.pdf" : "certificate-restricted.pdf");
        File.WriteAllBytes(source, TableExportSmoke.Fixture());
        Type optionsType = engine.GetType("KillerPdf.Engine.Security.PdfCertificateEncryptionOptions", true)!;
        object options = Activator.CreateInstance(optionsType)!;
        optionsType.GetProperty("Recipients")!.SetValue(options, new List<X509Certificate2> { recipient });
        optionsType.GetProperty("AllowDocumentModification")!.SetValue(options, mayEdit);
        app.GetType("KillerPDF.Services.PdfEngineIntegration", true)!
            .GetMethod("ProtectForRecipients", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [source, output, options, false]);
        return output;
    }

    private static void Open(Window owner, string path)
    {
        owner.GetType().GetMethod("OpenFromExternal", [typeof(string)])!.Invoke(owner, [path]);
        Wait(() => Descendants<Canvas>(owner).Any(IsPageCanvas), "certificate document rendered");
    }

    private static bool Notice(Window owner, string text) =>
        Descendants<TextBlock>(owner).Any(block => block.IsVisible && block.Text == text);
}
