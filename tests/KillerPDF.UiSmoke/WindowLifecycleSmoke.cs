using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Threading;

internal static class WindowLifecycleSmoke
{
    internal static int Run(Window first, Assembly assembly, string appPath, string folder)
    {
        // This fixture closes clean documents without the optional open-tab confirmation.
        assembly.GetType("KillerPDF.App", true)!.GetMethod("SetSetting",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, ["RememberChoiceLocked", "1"]);
        Console.WriteLine("Lifecycle: create synthetic source PDFs");
        string firstSource = Path.Combine(folder, "first.pdf");
        string secondSource = Path.Combine(folder, "survivor.pdf");
        byte[] firstBytes = SignedCopySmoke.Fixture(measurement: false);
        byte[] secondBytes = SignedCopySmoke.Fixture(measurement: false, pageWidth: 300);
        File.WriteAllBytes(firstSource, firstBytes);
        File.WriteAllBytes(secondSource, secondBytes);
        Console.WriteLine("Lifecycle: construct second document window");
        var survivor = (Window)Activator.CreateInstance(assembly.GetType("KillerPDF.Shell.MainWindow", true)!)!;
        survivor.WindowStartupLocation = WindowStartupLocation.Manual;
        survivor.Left = -10000; survivor.Top = -10000;
        survivor.ShowInTaskbar = false; survivor.ShowActivated = false;
        try
        {
            Console.WriteLine("Lifecycle: show second document window");
            survivor.Show();
            MethodInfo open = first.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!;
            Console.WriteLine("Lifecycle: open first synthetic PDF");
            open.Invoke(first, [firstSource]);
            Console.WriteLine("Lifecycle: open survivor synthetic PDF");
            open.Invoke(survivor, [secondSource]);
            Pump();
            bool firstClosed = false;
            first.Closed += (_, _) => firstClosed = true;
            Console.WriteLine("Lifecycle: close first document window");
            first.Close();
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (!firstClosed && DateTime.UtcNow < deadline)
            {
                Pump();
                Thread.Sleep(15);
            }
            if (!firstClosed) throw new TimeoutException("The first document window did not finish closing.");
            if (!ReferenceEquals(Application.Current.MainWindow, survivor))
                throw new InvalidOperationException("The application still targets a closed document window instead of the survivor.");

            string saved = Path.Combine(folder, "survivor-saved.pdf");
            Console.WriteLine("Lifecycle: invoke survivor Save As");
            SignedCopySmoke.SaveAs(survivor, assembly, saved);
            Assembly engine = Assembly.Load("KillerPdf.Engine");
            object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
                .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!
                .Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(saved))])!;
            try
            {
                object[] pages = ((IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfPageInformation", true)!
                    .GetMethod("Read")!.Invoke(null, [document])!).Cast<object>().ToArray();
                if (pages.Length != 1
                    || (double)pages[0].GetType().GetProperty("Width")!.GetValue(pages[0])! != 300
                    || (double)pages[0].GetType().GetProperty("Height")!.GetValue(pages[0])! != 200)
                    throw new InvalidOperationException("Closing another window did not preserve the survivor's saved page identity and dimensions.");
            }
            finally { (document as IDisposable)?.Dispose(); }
            if (!File.ReadAllBytes(firstSource).AsSpan().SequenceEqual(firstBytes)
                || !File.ReadAllBytes(secondSource).AsSpan().SequenceEqual(secondBytes))
                throw new InvalidOperationException("The window-lifecycle workflow changed a source PDF.");
            string result = "PASS closing one of two document windows preserves the survivor's real Save As workflow, page identity and dimensions, and both input files. App SHA256="
                + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
            File.WriteAllText(Path.Combine(folder, "result.txt"), result);
            Console.WriteLine(result);
            return 0;
        }
        finally { survivor.Close(); }
    }

    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
}
