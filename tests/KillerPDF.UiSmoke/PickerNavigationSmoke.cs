using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class PickerNavigationSmoke
{
    internal static int Run(Window owner, string folder)
    {
        Rect work = SystemParameters.WorkArea;
        owner.WindowState = WindowState.Normal;
        owner.Left = work.Left + 8; owner.Top = work.Top + 8;
        owner.Width = Math.Min(950, work.Width - 16); owner.Height = Math.Min(700, work.Height - 16);
        owner.Opacity = 1; owner.Activate();
        ShortcutHintsSmoke.Focus(owner);
        string good = Path.Combine(folder, "picker-good"); Directory.CreateDirectory(good);
        string source = Path.Combine(good, "synthetic.pdf");
        byte[] original = TableExportSmoke.Fixture(); File.WriteAllBytes(source, original);
        string missing = Path.Combine(good, "missing-folder");
        Type type = owner.GetType().Assembly.GetType("KillerPDF.Controls.FileDialog", true)!;
        Type modeType = owner.GetType().Assembly.GetType("KillerPDF.Controls.FileDialogMode", true)!;
        var picker = (Window)Activator.CreateInstance(type, [Enum.Parse(modeType, "Open")])!;
        type.GetProperty("InitialDirectory")!.SetValue(picker, good);
        type.GetProperty("Filter")!.SetValue(picker, "PDF|*.pdf");
        picker.Owner = owner;
        Task Listing() => (Task)type.GetProperty("DirectoryListingTask")!.GetValue(picker)!;
        bool Flag(string name) => (bool)type.GetProperty(name)!.GetValue(picker)!;
        int phase = 0;
        bool ticking = false;
        Task? startingTask = null;
        Exception? failure = null;
        long started = Environment.TickCount64;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            if (ticking || phase >= 2 || failure is not null || !picker.IsVisible) return;
            ticking = true;
            try
            {
                if (Environment.TickCount64 - started > 15000) throw new TimeoutException("Picker navigation recovery timed out at phase " + phase);
                var path = (TextBox)picker.FindName("PathBox");
                var accept = (Button)picker.FindName("AcceptButton");
                if (phase == 0)
                {
                    if (Flag("IsDirectoryListingBusy") || !Flag("IsDirectoryListingAvailable") || !Listing().IsCompleted
                        || ((FrameworkElement)picker.FindName("RootFade")).Opacity < .99) return;
                    if (path.Text != good || ((ListBox)picker.FindName("FileList")).Items.Count < 1 || !accept.IsEnabled)
                        throw new InvalidOperationException("The picker did not establish its valid-directory presence control.");
                    Console.WriteLine("CONTROL actual picker listed the synthetic directory and enabled Accept.");
                    startingTask = Listing(); phase = 1;
                    path.Focus(); path.SelectAll(); path.Text = missing;
                    ShortcutHintsSmoke.Key(picker, 0x0D);
                    return;
                }
                if (ReferenceEquals(Listing(), startingTask) || Flag("IsDirectoryListingBusy") || !Listing().IsCompleted) return;
                if (Listing().IsFaulted) throw Listing().Exception!;
                Console.WriteLine($"CONTROL picker after missing typed path: available={Flag("IsDirectoryListingAvailable")}, path={path.Text}, accept={accept.IsEnabled}.");
                if (!Flag("IsDirectoryListingAvailable") || path.Text != good || !accept.IsEnabled
                    || ((ListBox)picker.FindName("FileList")).Items.Count < 1)
                    throw new InvalidOperationException("A missing typed folder did not restore the last usable picker directory.");
                phase = 2; picker.Close();
            }
            catch (Exception error) { failure = error; timer.Stop(); picker.Close(); }
            finally { ticking = false; }
        };
        timer.Start();
        try { type.GetMethod("ShowDialog", [typeof(Window)])!.Invoke(picker, [owner]); }
        finally { timer.Stop(); if (picker.IsVisible) picker.Close(); }
        if (failure is not null) throw new InvalidOperationException("Picker public navigation recovery failed.", failure);
        if (phase != 2 || !File.ReadAllBytes(source).AsSpan().SequenceEqual(original) || Dirty(owner))
            throw new InvalidOperationException("Picker recovery did not complete or changed the source document.");
        Console.WriteLine("PASS picker navigation recovery: missing typed folder returns to the last usable directory with rows and Accept restored; source bytes unchanged.");
        return 0;
    }
}
