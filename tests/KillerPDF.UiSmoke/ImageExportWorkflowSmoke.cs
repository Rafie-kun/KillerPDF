using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class ImageExportWorkflowSmoke
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    internal static int Run(Window owner, string folder)
        => FormWorkflowFailureSmoke.WithCleanup(owner, () => VerifyConsent(owner, folder), dismissDuringRun: false);

    private static int VerifyConsent(Window owner, string folder)
    {
        string source = Path.Combine(folder, "source.pdf");
        byte[] original = TableExportSmoke.Fixture();
        File.WriteAllBytes(source, original);
        owner.GetType().GetMethod("OpenFromExternal", Members)!.Invoke(owner, [source]);
        owner.Opacity = 1;
        owner.Activate();
        Wait(() => Descendants<Canvas>(owner).Any(IsPageCanvas), "image export source rendered");
        string chosen = Path.Combine(folder, "export.png"), actual = Path.Combine(folder, "export-page-001.png");
        byte[] sentinel = Encoding.ASCII.GetBytes("existing output");
        File.WriteAllBytes(actual, sentinel);
        Export(owner, chosen, approve: false, expectConfirmation: true);
        if (!sentinel.AsSpan().SequenceEqual(File.ReadAllBytes(actual)))
            throw new InvalidOperationException("Declining the actual output overwrite changed the file.");
        Export(owner, chosen, approve: true, expectConfirmation: true);
        if (!File.ReadAllBytes(actual).AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || !original.AsSpan().SequenceEqual(File.ReadAllBytes(source)))
            throw new InvalidOperationException("Approved export failed to publish a complete image or changed its input.");
        VerifyCancellation(owner.GetType().Assembly, folder);
        Console.WriteLine("PASS live image export asks for the actual numbered output, preserves refusal and publishes approval; cancellation counts only complete images.");
        return 0;
    }

    internal static void Export(Window owner, string chosen, bool approve, bool expectConfirmation)
    {
        int confirmations = 0;
        Exception? failure = null;
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
        void Queue(Button button) => owner.Dispatcher.BeginInvoke(DispatcherPriority.Input,
            (Action)(() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
        timer.Tick += (_, _) =>
        {
            try
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
                {
                    if (!seen.Add(dialog)) continue;
                    if (dialog.GetType().Name == "ExportImagesDialog")
                    {
                        ((TextBox)dialog.GetType().GetField("_dpi", Members)!.GetValue(dialog)!).Text = "72";
                        Queue(Descendants<Button>(dialog).Single(button => button.IsDefault));
                    }
                    else if (dialog.GetType().Name == "FileDialog")
                    {
                        ((TextBox)dialog.FindName("FileNameBox")).Text = chosen;
                        Queue((Button)dialog.FindName("AcceptButton"));
                    }
                    else
                    {
                        string text = string.Join("\n", Descendants<TextBlock>(dialog).Select(block => block.Text));
                        string actualName = Path.GetFileNameWithoutExtension(chosen) + "-page-001.png";
                        if (!text.Contains(actualName, StringComparison.Ordinal))
                            throw new InvalidOperationException("Unexpected export dialog: " + text);
                        confirmations++;
                        Queue(Descendants<Button>(dialog).Single(button => Equals(button.Content,
                            owner.FindResource(approve ? "Str_Btn_Yes" : "Str_Btn_No"))));
                    }
                }
            }
            catch (Exception error)
            {
                failure = error;
                foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray()) dialog.Close();
            }
        };
        timer.Start();
        try
        {
            var export = (Task)owner.GetType().GetMethod("ExportImagesFlow", Members)!.Invoke(owner, [""])!;
            Wait(() => export.IsCompleted || failure is not null, "image export completed");
            if (failure is not null) throw failure;
            export.GetAwaiter().GetResult();
        }
        finally { timer.Stop(); }
        if (confirmations != (expectConfirmation ? 1 : 0))
            throw new InvalidOperationException("Image export did not obtain exactly the required actual-output consent.");
    }

    private static void VerifyCancellation(Assembly app, string folder)
    {
        Type builderType = Assembly.Load("KillerPdf.Engine").GetType("KillerPdf.Engine.Authoring.PdfDocumentBuilder", true)!;
        ConstructorInfo constructor = builderType.GetConstructors().Single();
        dynamic builder = constructor.Invoke(constructor.GetParameters().Select(parameter => parameter.DefaultValue).ToArray());
        string source = Path.Combine(folder, "cancel.pdf");
        byte[] original = builder.AddBlankPage(200d, 300d).AddBlankPage(200d, 300d).Build();
        File.WriteAllBytes(source, original);
        using var cancel = new CancellationTokenSource();
        Action<int, int> progress = (_, _) => cancel.Cancel();
        int written = (int)app.GetType("KillerPDF.Services.PdfRasterize", true)!.GetMethod("ExportPageImages", Members)!
            .Invoke(null, [source, new[] { 0, 1 }, new int[2], 72d, false, folder, "canceled", 3, progress, cancel.Token, null, null])!;
        if (written != 1 || !File.Exists(Path.Combine(folder, "canceled-page-001.png"))
            || File.Exists(Path.Combine(folder, "canceled-page-002.png"))
            || !original.AsSpan().SequenceEqual(File.ReadAllBytes(source)))
            throw new InvalidOperationException("Canceled export misreported or damaged its partial batch.");
    }
}
