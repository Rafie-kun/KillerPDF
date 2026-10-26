using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class DocumentStateSmoke
{
    private const string Marker = "Z";

    internal static int Run(Window owner, Assembly assembly, string appPath, string folder, string scenario)
    {
        if (scenario == "--verify-stamp-isolation") return VerifyStampIsolation(owner, appPath, folder);
        if (scenario == "--verify-stamp-history") return VerifyStampIsolation(owner, appPath, folder, history: true);
        if (scenario == "--verify-stamp-tabs") return VerifyStampIsolation(owner, appPath, folder, tabs: true);
        if (scenario == "--verify-stamp-panes") return VerifyStampIsolation(owner, appPath, folder, panes: true);
        if (scenario == "--verify-stamp-tearout") return VerifyStampIsolation(owner, appPath, folder, tearOut: true);
        if (scenario == "--verify-stamp-saveall") return VerifyStampIsolation(owner, appPath, folder, saveAll: true);
        string source = Path.Combine(folder, "state-source.pdf");
        bool closeInOtherPane = scenario == "--verify-active-editor-close-b";
        string otherSource = Path.Combine(folder, "other-pane.pdf");
        byte[] original = scenario is "--verify-form-history" or "--verify-merge-pending"
            ? FormFieldEditingSmoke.Fixture("ORIGINAL") : TableExportSmoke.Fixture();
        File.WriteAllBytes(source, original);
        if (closeInOtherPane) File.WriteAllBytes(otherSource, original);
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!
            .Invoke(owner, [closeInOtherPane ? otherSource : source]);
        owner.WindowState = WindowState.Normal;
        owner.Left = 32; owner.Top = 32; owner.Width = 950; owner.Height = 700;
        owner.Opacity = 1; owner.ShowActivated = true;
        owner.Activate();
        Wait(() => owner.IsActive && Descendants<Canvas>(owner).Any(IsPageCanvas), "active rendered page");
        DependencyObject editingScope = owner;
        if (closeInOtherPane)
        {
            assembly.GetType("KillerPDF.App", true)!.GetMethod("SetSetting", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, ["RememberChoiceLocked", "0"]);
            Click(owner, "SplitPaneRailBtn");
            var other = (FrameworkElement)owner.FindName("ViewerB");
            Wait(() => other.IsVisible && other.ActualWidth >= 320, "active editor second pane");
            other.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [source]);
            Wait(() => ((TextBlock)owner.FindName("FileNameLabel")).Text == Path.GetFileName(source)
                && Descendants<Canvas>(other).Any(IsPageCanvas), "second-pane editor source");
            editingScope = other;
            Console.WriteLine("CONTROL active editor uses pane B with the combined quit preference unlocked.");
        }
        if (Dirty(owner)) throw new InvalidOperationException("The starting fixture must be clean.");

        Click(owner, "ToolTextBtn");
        Canvas canvas = Descendants<Canvas>(editingScope).Where(IsPageCanvas)
            .OrderByDescending(item => item.ActualWidth * item.ActualHeight).First();
        Point point = canvas.PointToScreen(new Point(canvas.ActualWidth * 0.72, canvas.ActualHeight * 0.72));
        if (point.X < 0 || point.Y < 0 || point.X >= SystemParameters.PrimaryScreenWidth
            || point.Y >= SystemParameters.PrimaryScreenHeight)
            throw new InvalidOperationException("The owned page target is outside the visible desktop.");
        if (!SetCursorPos((int)point.X, (int)point.Y)) throw new InvalidOperationException("Cannot position the test pointer.");
        MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
        MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
        Wait(() => Keyboard.FocusedElement is TextBox { AcceptsReturn: true }, "active text editor");
        var editor = (TextBox)Keyboard.FocusedElement;
        editor.Text = Marker;
        editor.CaretIndex = editor.Text.Length;
        Console.WriteLine("CONTROL real Text tool created a focused editable text box on a clean PDF.");

        try
        {
            if (scenario is "--verify-active-editor-close" or "--verify-active-editor-close-b")
                VerifyClose(owner, source, original, editor);
            else if (scenario == "--verify-annotation-history")
                VerifyAnnotationHistory(owner, source, editor, folder);
            else if (scenario == "--verify-form-history")
                VerifyFormHistory(owner, source, editor);
            else if (scenario == "--verify-failed-save-history")
                VerifyFailedSaveHistory(owner, source, original, editor);
            else if (scenario == "--verify-page-edit-failure")
                VerifyPageEditFailure(owner, source, original, editor, folder);
            else if (scenario == "--verify-layout-pending")
                VerifyLayoutPending(owner, source, original, editor, folder);
            else if (scenario == "--verify-merge-pending")
                VerifyLayoutPending(owner, source, original, editor, folder, merge: true);
            else if (scenario == "--verify-layouts-pending")
                foreach (string key in new[] { "Str_Production_NUp", "Str_Production_Booklet", "Str_Production_Repeat",
                    "Str_Production_CutStack", "Str_Production_Manual", "Str_Production_Poster" })
                    VerifyLayoutPending(owner, source, original, editor, folder, layoutKey: key);
            else
                VerifyHistory(owner, source, editor);

            if (closeInOtherPane && !File.ReadAllBytes(otherSource).AsSpan().SequenceEqual(original))
                throw new InvalidOperationException("Closing pane B changed the other pane's source.");
            string result = "PASS " + scenario.TrimStart('-') + ": public text/save/undo/close commands. App SHA256="
                + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
            File.WriteAllText(Path.Combine(folder, "result.txt"), result);
            Console.WriteLine(result);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("Document-state stage failed: " + error);
            throw;
        }
        finally
        {
            var cleanup = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            cleanup.Tick += (_, _) =>
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                    .Where(window => window != owner && window.IsVisible).ToArray())
                {
                    Button? discard = Descendants<Button>(dialog).FirstOrDefault(button =>
                        Equals(button.Content, Application.Current.FindResource("Str_Btn_DontSave"))
                        || Equals(button.Content, Application.Current.FindResource("Str_Btn_Quit")));
                    if (discard is not null) discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                }
            };
            cleanup.Start();
            try
            {
                if (owner.IsVisible) owner.Close();
                Wait(() => !owner.IsVisible, "owned window cleanup");
            }
            finally { cleanup.Stop(); }
        }
    }

    private static int VerifyStampIsolation(Window owner, string appPath, string folder, bool history = false, bool tabs = false, bool panes = false, bool tearOut = false, bool saveAll = false)
    {
        owner.WindowState = WindowState.Normal;
        owner.Left = 32; owner.Top = 32; owner.Width = 950; owner.Height = 700;
        owner.Opacity = 1; owner.ShowActivated = true;
        owner.Activate();
        string first = Path.Combine(folder, "stamp-first.pdf"), second = Path.Combine(folder, "stamp-second.pdf");
        File.WriteAllBytes(first, TableExportSmoke.Fixture());
        File.WriteAllBytes(second, TableExportSmoke.Fixture());
        void Open(string path)
        {
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [path]);
            Wait(() => Descendants<TextBlock>(owner).Any(block => block.Name == "FileNameLabel"
                && block.Text == Path.GetFileName(path)), "stamp source open");
            Wait(() => ((Button)owner.FindName("ToolStampBtn")).IsEnabled
                && Descendants<Canvas>(owner).Any(IsPageCanvas), "rendered stamp-tool readiness");
        }
        Exception? failure = null;
        Window? detached = null;
        string expectedFormat = "{n}", desiredFormat = "STAMP_A";
        var handled = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                .Where(window => window != owner && window.IsVisible && window.GetType().Name == "StampWindow").ToArray())
            {
                if (!handled.Add(dialog)) continue;
                try
                {
                    Descendants<TextBox>(dialog).Single(box => box.Text == expectedFormat).Text = desiredFormat;
                    Button apply = Descendants<Button>(dialog).Single(button =>
                        System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "StampApply");
                    if (!apply.IsEnabled) throw new InvalidOperationException("Stamp Apply is disabled.");
                    apply.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch (Exception error) { failure = error; dialog.Close(); }
            }
        };
        try
        {
            Open(first);
            timer.Start();
            try { Click(owner, "ToolStampBtn"); }
            finally { timer.Stop(); }
            if (failure is not null) throw failure;
            if (handled.Count != 1 || !Dirty(owner)) throw new InvalidOperationException("Stamp tool did not apply the first document's page-number format.");
            Save(owner);
            if (!ReadText(first).Contains("STAMP_A", StringComparison.Ordinal))
                throw new InvalidOperationException("The first document did not save its own stamp.");
            Console.WriteLine("CONTROL first document strictly reopened with its applied STAMP_A page number.");
            if (saveAll)
            {
                expectedFormat = "STAMP_A"; desiredFormat = "STAMP_PENDING";
                timer.Start();
                try { Click(owner, "ToolStampBtn"); }
                finally { timer.Stop(); }
                if (failure is not null) throw failure;
                if (!Dirty(owner)) throw new InvalidOperationException("The first pending stamp did not become dirty.");
            }
            if (tearOut)
            {
                byte[] savedFirst = File.ReadAllBytes(first);
                expectedFormat = "STAMP_A";
                desiredFormat = "STAMP_PENDING";
                timer.Start();
                try { Click(owner, "ToolStampBtn"); }
                finally { timer.Stop(); }
                if (failure is not null) throw failure;
                if (!Dirty(owner)) throw new InvalidOperationException("Pending stamp edit did not become dirty.");
                Open(second);
                Wait(() => Descendants<TextBlock>(owner).Any(block => block.Name == "tabLbl"
                    && block.Text.TrimStart('•', ' ') == Path.GetFileNameWithoutExtension(first)), "stamped tab label");
                TextBlock label = Descendants<TextBlock>(owner).Single(block => block.Name == "tabLbl"
                    && block.Text.TrimStart('•', ' ') == Path.GetFileNameWithoutExtension(first));
                DependencyObject tab = label;
                while (tab is not FrameworkElement { Name: "tabBd" })
                    tab = VisualTreeHelper.GetParent(tab) ?? throw new InvalidOperationException("The stamped tab frame is missing.");
                ((UIElement)tab).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
                    { RoutedEvent = UIElement.MouseRightButtonUpEvent });
                Pump();
                DependencyObject[] roots = PresentationSource.CurrentSources.Cast<PresentationSource>()
                    .Select(source => source.RootVisual).OfType<DependencyObject>().ToArray();
                ContextMenu menu = roots.OfType<ContextMenu>().Concat(roots.SelectMany(Descendants<ContextMenu>))
                    .Distinct().Single(candidate => candidate.IsOpen);
                MenuItem move = menu.Items.OfType<MenuItem>().Single(item => item.InputGestureText.Contains("Ctrl", StringComparison.Ordinal)
                    && item.InputGestureText.Contains("Shift", StringComparison.Ordinal) && item.InputGestureText.EndsWith("N", StringComparison.Ordinal));
                if (!move.IsEnabled) throw new InvalidOperationException("Move to new window is disabled for the stamped document.");
                move.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.IsOpen = false;
                Wait(() => Application.Current.Windows.Cast<Window>().Any(window => window != owner
                    && window.GetType() == owner.GetType() && window.IsVisible), "detached stamped document");
                detached = Application.Current.Windows.Cast<Window>().Single(window => window != owner
                    && window.GetType() == owner.GetType() && window.IsVisible);
                detached.Left = 64; detached.Top = 64; detached.Width = 950; detached.Height = 700;
                detached.Opacity = 1; detached.ShowActivated = true; detached.Activate();
                Wait(() => ((Button)detached.FindName("ToolStampBtn")).IsEnabled
                    && Descendants<Canvas>(detached).Any(IsPageCanvas), "detached stamp tool readiness");
                if (!File.ReadAllBytes(first).AsSpan().SequenceEqual(savedFirst) || !Dirty(detached))
                    throw new InvalidOperationException("Tear-out wrote the source or lost the pending stamp's dirty state.");
                expectedFormat = desiredFormat = "STAMP_PENDING";
                timer.Start();
                try { Click(detached, "ToolStampBtn"); }
                finally { timer.Stop(); }
                if (failure is not null) throw failure;
                Save(detached);
                string transferredText = ReadText(first);
                if (!transferredText.Contains("STAMP_PENDING", StringComparison.Ordinal)
                    || transferredText.Contains("STAMP_A", StringComparison.Ordinal)
                    || !File.ReadAllBytes(second).AsSpan().SequenceEqual(TableExportSmoke.Fixture()))
                    throw new InvalidOperationException("Detached Save lost its pending stamp or changed the other document.");
                const string transferResult = "PASS verify-stamp-tearout: pending stamp specification and dirty state survive Move to new window and strict Save/reopen.";
                File.WriteAllText(Path.Combine(folder, "result.txt"), transferResult);
                Console.WriteLine(transferResult);
                return 0;
            }
            if (history)
            {
                if (!((Button)owner.FindName("UndoBtn")).IsEnabled)
                    throw new InvalidOperationException("APP-003: applying a stamp produced no usable Undo action.");
                Click(owner, "UndoBtn");
                if (!Dirty(owner) || Descendants<TextBlock>(owner).Any(block => block.IsVisible && block.Text == "STAMP_A"))
                    throw new InvalidOperationException("Stamp Undo did not remove the stamp and mark the saved document dirty.");
                Click(owner, "RedoBtn");
                if (Dirty(owner) || !Descendants<TextBlock>(owner).Any(block => block.IsVisible && block.Text == "STAMP_A"))
                    throw new InvalidOperationException("Stamp Redo did not return to the saved checkpoint and visible stamp.");
                Click(owner, "UndoBtn");
                Save(owner);
                if (ReadText(first).Contains("STAMP_A", StringComparison.Ordinal))
                    throw new InvalidOperationException("Saving Stamp Undo retained the removed stamp.");
                Click(owner, "RedoBtn");
                Save(owner);
                if (!ReadText(first).Contains("STAMP_A", StringComparison.Ordinal))
                    throw new InvalidOperationException("Saving Stamp Redo lost the restored stamp.");
                string historyResult = "PASS verify-stamp-history: stamp Undo/Redo, dirty checkpoints and strictly reopened output.";
                File.WriteAllText(Path.Combine(folder, "result.txt"), historyResult);
                Console.WriteLine(historyResult);
                return 0;
            }
            if (panes)
            {
                Click(owner, "SplitPaneRailBtn");
                var other = (FrameworkElement)owner.FindName("ViewerB");
                Wait(() => other.IsVisible && other.ActualWidth >= 320, "second stamp pane");
                other.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                    { RoutedEvent = Mouse.PreviewMouseDownEvent });
                Pump();
            }
            Open(second);
            Save(owner);
            if (ReadText(second).Contains("STAMP_A", StringComparison.Ordinal))
                throw new InvalidOperationException("APP-003: saving the second document published the first document's stamp.");
            if (tabs || panes || saveAll)
            {
                expectedFormat = "{n}";
                desiredFormat = "STAMP_B";
                timer.Start();
                try { Click(owner, "ToolStampBtn"); }
                finally { timer.Stop(); }
                if (failure is not null) throw failure;
                if (saveAll)
                {
                    SaveAllOnClose(owner);
                    string firstSaved = ReadText(first), secondSaved = ReadText(second);
                    if (!firstSaved.Contains("STAMP_PENDING", StringComparison.Ordinal) || firstSaved.Contains("STAMP_B", StringComparison.Ordinal)
                        || firstSaved.Contains("STAMP_A", StringComparison.Ordinal) || !secondSaved.Contains("STAMP_B", StringComparison.Ordinal)
                        || secondSaved.Contains("STAMP_PENDING", StringComparison.Ordinal) || secondSaved.Contains("STAMP_A", StringComparison.Ordinal))
                        throw new InvalidOperationException("Save All during close mixed or lost document-owned pending stamps.");
                    const string saveAllResult = "PASS verify-stamp-saveall: Save during close writes each document's own pending stamp and closes successfully.";
                    File.WriteAllText(Path.Combine(folder, "result.txt"), saveAllResult);
                    Console.WriteLine(saveAllResult);
                    return 0;
                }
                Save(owner);
                string secondText = ReadText(second);
                if (!secondText.Contains("STAMP_B", StringComparison.Ordinal) || secondText.Contains("STAMP_A", StringComparison.Ordinal))
                    throw new InvalidOperationException("The second tab did not save only its own STAMP_B.");
                if (panes)
                {
                    var firstViewer = (FrameworkElement)owner.FindName("Viewer");
                    var secondViewer = (FrameworkElement)owner.FindName("ViewerB");
                    TextBlock previous = Descendants<TextBlock>(secondViewer).Single(block => block.IsVisible && block.Text == "STAMP_B");
                    CaptureOwnedWindow(owner, Path.Combine(folder, "panes-before-view-change.png"));
                    var single = (RadioButton)owner.FindName("ViewSingleRadio");
                    ((RadioButton)owner.FindName(single.IsChecked == true ? "ViewContinuousRadio" : "ViewSingleRadio")).IsChecked = true;
                    firstViewer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                        { RoutedEvent = Mouse.PreviewMouseDownEvent });
                    try
                    {
                        Wait(() => Descendants<TextBlock>(secondViewer).Any(block => block.IsVisible && block.Text == "STAMP_B"
                            && !ReferenceEquals(previous, block)), "fresh unfocused-pane stamp rendering");
                    }
                    finally { CaptureOwnedWindow(owner, Path.Combine(folder, "panes-after-view-change.png")); }
                    if (Descendants<TextBlock>(secondViewer).Any(block => block.IsVisible && block.Text == "STAMP_A")
                        || !Descendants<TextBlock>(firstViewer).Any(block => block.IsVisible && block.Text == "STAMP_A")
                        || Descendants<TextBlock>(firstViewer).Any(block => block.IsVisible && block.Text == "STAMP_B"))
                        throw new InvalidOperationException("An unfocused pane rendered another document's stamp.");
                    if (((TextBlock)owner.FindName("FileNameLabel")).Text != Path.GetFileName(first))
                        throw new InvalidOperationException("Unfocused rendering changed the focused document label.");
                    Console.WriteLine("CONTROL fresh unfocused-pane render retained only its own stamp while the first pane stayed focused.");
                }
                if (!panes && !Descendants<TextBlock>(owner).Any(block => block.Name == "tabLbl"
                    && block.Text == Path.GetFileNameWithoutExtension(first)))
                    throw new InvalidOperationException("The first document's existing tab is unavailable.");
                Open(first);
                expectedFormat = desiredFormat = "STAMP_A";
                timer.Start();
                try { Click(owner, "ToolStampBtn"); }
                finally { timer.Stop(); }
                if (failure is not null) throw failure;
                Save(owner);
                string firstText = ReadText(first);
                if (!firstText.Contains("STAMP_A", StringComparison.Ordinal) || firstText.Contains("STAMP_B", StringComparison.Ordinal))
                    throw new InvalidOperationException("Returning to the first tab lost or replaced its stamp specification.");
                string tabsResult = "PASS " + (panes ? "verify-stamp-panes" : "verify-stamp-tabs")
                    + ": distinct specifications survive document switching and strict Save/reopen.";
                File.WriteAllText(Path.Combine(folder, "result.txt"), tabsResult);
                Console.WriteLine(tabsResult);
                return 0;
            }
            string result = "PASS verify-stamp-isolation: each public Save uses only its document's stamp. App SHA256="
                + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(appPath)));
            File.WriteAllText(Path.Combine(folder, "result.txt"), result);
            Console.WriteLine(result);
            return 0;
        }
        finally
        {
            timer.Stop();
            var cleanup = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            cleanup.Tick += (_, _) =>
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                    .Where(window => window != owner && window != detached && window.IsVisible).ToArray())
                {
                    Button? discard = Descendants<Button>(dialog).FirstOrDefault(button =>
                        Equals(button.Content, Application.Current.FindResource("Str_Btn_DontSave")));
                    if (discard is not null) discard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else dialog.Close();
                }
            };
            cleanup.Start();
            try
            {
                if (detached?.IsVisible == true)
                {
                    detached.Close();
                    Wait(() => !detached.IsVisible, "detached window cleanup");
                }
                owner.Close();
                Wait(() => !owner.IsVisible, "stamp window cleanup");
            }
            finally { cleanup.Stop(); }
        }
    }

    private static void SaveAllOnClose(Window owner)
    {
        int savePrompts = 0;
        Exception? failure = null;
        var handled = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (!handled.Add(dialog)) continue;
                Button? save = Descendants<Button>(dialog).FirstOrDefault(button => Equals(button.Content, owner.FindResource("Str_Btn_Save")));
                Button? yes = Descendants<Button>(dialog).FirstOrDefault(button => Equals(button.Content, owner.FindResource("Str_Btn_Yes")));
                if (save is not null) savePrompts++;
                Button? action = save ?? yes;
                if (action is null)
                {
                    failure = new InvalidOperationException("Unexpected dialog during Save All on close.");
                    dialog.Close();
                    continue;
                }
                _ = Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Input,
                    (Action)(() => action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));
            }
        };
        timer.Start();
        try
        {
            owner.Close();
            Wait(() => !owner.IsVisible || failure is not null, "Save All close completion");
            if (failure is not null) throw failure;
            if (savePrompts != 1) throw new InvalidOperationException("Save All close did not ask once for the pending documents.");
        }
        finally { timer.Stop(); }
    }

    private static void VerifyLayoutPending(Window owner, string source, byte[] original, TextBox editor, string folder, bool merge = false, string? layoutKey = null)
    {
        if (editor.IsVisible) Stroke(0x0D, 0x11);
        Wait(() => !editor.IsVisible && Dirty(owner), "pending layout annotation");
        if (!Descendants<TextBlock>(owner).Any(text => text.IsVisible && text.Text == Marker))
            throw new InvalidOperationException("The pending layout annotation is not visible.");
        string output = Path.Combine(folder, merge ? "pending-merge.pdf" : (layoutKey ?? "pending-layout") + ".pdf");
        string data = Path.Combine(folder, "pending-records.csv");
        if (merge) File.WriteAllText(data, "existing.text\r\nMERGED_RECORD\r\n");
        bool reportShown = false;
        Exception? failure = null;
        var handled = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                if (!handled.Add(dialog)) continue;
                try
                {
                    string type = dialog.GetType().Name;
                    if (type == "DocumentCommandDialog")
                        Descendants<Button>(dialog).Single(button => Equals(button.Content, owner.FindResource(layoutKey ?? "Str_Production_NUp")))
                            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    else if (type is "ProductionLayoutDialog" or "ProductionPreviewDialog" or "DataMergeDialog" or "ImportReviewDialog")
                    {
                        if (type == "ProductionLayoutDialog" && layoutKey == "Str_Production_Manual")
                            Descendants<TextBox>(dialog).Single(box => System.Windows.Automation.AutomationProperties.GetName(box)
                                == (string)owner.FindResource("Str_Production_Sequence")).Text = "1";
                        Descendants<Button>(dialog).Single(button => button.IsDefault).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else if (type == "FileDialog")
                    {
                        ((TextBox)dialog.FindName("FileNameBox")).Text = merge && dialog.Title.Contains(
                            (string)owner.FindResource("Str_DataMerge_SelectData"), StringComparison.Ordinal) ? data : output;
                        ((Button)dialog.FindName("AcceptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    else if (merge && type == "DocumentReportDialog") { reportShown = true; dialog.Close(); }
                    else throw new InvalidOperationException("Unexpected output dialog: " + type);
                    Console.WriteLine("CONTROL layout dialog: " + type);
                }
                catch (Exception error) { failure = error; dialog.Close(); }
            }
        };
        Click(owner, "SaveMenuBtn");
        Pump();
        var roots = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(item => item.RootVisual).OfType<DependencyObject>();
        ContextMenu menu = roots.OfType<ContextMenu>().Concat(roots.SelectMany(Descendants<ContextMenu>)).Distinct().Single(item => item.IsOpen);
        MenuItem tools = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, owner.FindResource("Str_DocumentTools")));
        MenuItem production = tools.Items.OfType<MenuItem>().Single(item => Equals(item.Header,
            owner.FindResource(merge ? "Str_DocumentTools_DataMerge" : "Str_DocumentTools_ProductionLayouts")));
        menu.IsOpen = false;
        timer.Start();
        try
        {
            production.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Wait(() => (File.Exists(output) && (!merge || reportShown)) || failure is not null, "complete document output");
            if (failure is not null) throw failure;
        }
        finally { timer.Stop(); }
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original) || !Dirty(owner))
            throw new InvalidOperationException("Layout export changed the source or consumed pending edit state.");
        if (!ReadText(output).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("WIRE-001: " + (merge ? "direct Data Merge" : "production layout") + " output omitted the visible pending text annotation.");
        if (merge && (ReadFormValue(output, "existing.text") != "MERGED_RECORD"
            || ReadFormValue(output, "untouched.sibling") != ReadFormValue(source, "untouched.sibling")))
            throw new InvalidOperationException("Direct Data Merge lost its mapped value or changed an unmapped field.");
        Console.WriteLine("CONTROL strict production-layout output retained pending annotation and unchanged live/source state.");
        if (layoutKey is not null) Console.WriteLine("CONTROL completed layout: " + layoutKey);
    }

    private static void VerifyPageEditFailure(Window owner, string source, byte[] original, TextBox editor, string folder)
    {
        Stroke(0x0D, 0x11);
        Wait(() => !editor.IsVisible && Dirty(owner), "annotation before page-edit failure");
        Save(owner);
        byte[] savedBefore = File.ReadAllBytes(source);
        if (savedBefore.AsSpan().SequenceEqual(original) || !ReadText(source).Contains(Marker, StringComparison.Ordinal) || Dirty(owner))
            throw new InvalidOperationException("Page-edit failure control did not save the initial annotation cleanly.");
        var visible = Descendants<Border>(owner).Single(border => border.IsVisible
            && Descendants<TextBlock>(border).Any(text => text.Text == Marker)
            && border.Parent is Canvas);
        double left = Canvas.GetLeft(visible), top = Canvas.GetTop(visible);
        bool undoEnabled = ((Button)owner.FindName("UndoBtn")).IsEnabled;
        string tempRoot = Path.GetFullPath(Path.Combine(folder, "data", "Temp"));
        string session = Directory.GetDirectories(tempRoot, "session-*").Single();
        var directory = new DirectoryInfo(session);
        DirectorySecurity originalSecurity = directory.GetAccessControl(AccessControlSections.Access);
        string originalSddl = originalSecurity.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        File.WriteAllText(Path.Combine(folder, "original-temp-access.txt"), originalSddl);
        DirectorySecurity denied = directory.GetAccessControl(AccessControlSections.Access);
        denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
            FileSystemRights.CreateFiles, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        int errors = 0;
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
            {
                Button? ok = Descendants<Button>(dialog).FirstOrDefault(button =>
                    Equals(button.Content, Application.Current.FindResource("Str_Btn_OK")));
                if (ok is null || !seen.Add(dialog)) continue;
                errors++;
                Console.WriteLine("CONTROL page-edit error: " + string.Join(" ", Descendants<TextBlock>(dialog).Select(text => text.Text)));
                ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        };
        try
        {
            directory.SetAccessControl(denied);
            bool blocked = false;
            try { File.WriteAllBytes(Path.Combine(session, "write-denial-control"), [1]); }
            catch (UnauthorizedAccessException) { blocked = true; }
            if (!blocked) throw new InvalidOperationException("Synthetic temporary-write denial was not effective.");
            timer.Start();
            try { RotateFromThumbnail(owner); }
            finally { timer.Stop(); }
        }
        finally
        {
            var restoredSecurity = new DirectorySecurity();
            restoredSecurity.SetSecurityDescriptorSddlForm(originalSddl, AccessControlSections.Access);
            directory.SetAccessControl(restoredSecurity);
            if (directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access) != originalSddl)
                throw new InvalidOperationException("Synthetic temporary-directory permissions were not restored exactly.");
        }
        Pump();
        Console.WriteLine($"CONTROL failed rotation: errors={errors}, dirty={Dirty(owner)}, sourceUnchanged={File.ReadAllBytes(source).AsSpan().SequenceEqual(savedBefore)}, undoUnchanged={((Button)owner.FindName("UndoBtn")).IsEnabled == undoEnabled}, left={Canvas.GetLeft(visible)}, top={Canvas.GetTop(visible)}, previousLeft={left}, previousTop={top}.");
        if (errors != 1 || Dirty(owner) || !File.ReadAllBytes(source).AsSpan().SequenceEqual(savedBefore)
            || ((Button)owner.FindName("UndoBtn")).IsEnabled != undoEnabled
            || Math.Abs(Canvas.GetLeft(visible) - left) > 0.01 || Math.Abs(Canvas.GetTop(visible) - top) > 0.01)
            throw new InvalidOperationException("APP-004: failed page rotation changed annotations, dirty state, history or original bytes.");
        Save(owner);
        if (!ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("Failed page rotation lost the annotation on subsequent Save.");
        Console.WriteLine("CONTROL denied page edit retained geometry, dirty/history state and strict saved annotation.");
        RotateFromThumbnail(owner);
        Wait(() => Dirty(owner), "successful rotation after write failure");
        Save(owner);
        if (ReadRotation(source) != 90 || !ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("Retry after failed rotation did not save its page turn and annotation.");
        Click(owner, "UndoBtn");
        if (!Dirty(owner)) throw new InvalidOperationException("Undo of the successful retry did not differ from the saved checkpoint.");
        Save(owner);
        if (ReadRotation(source) != 0 || !ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("Undo after the successful retry did not restore the original page turn and annotation.");
        Click(owner, "RedoBtn");
        Save(owner);
        if (ReadRotation(source) != 90 || !ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("Redo after the successful retry did not restore the saved page turn and annotation.");
        Console.WriteLine("CONTROL successful retry and Undo/Redo strictly reopened with expected rotation and annotation.");
    }

    private static void RotateFromThumbnail(Window owner)
    {
        var pages = (ListBox)owner.FindName("PageList");
        pages.SelectedIndex = 0;
        var thumbnail = (ListBoxItem)pages.ItemContainerGenerator.ContainerFromIndex(0);
        thumbnail.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
            { RoutedEvent = Mouse.MouseUpEvent });
        Pump();
        var roots = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual).OfType<DependencyObject>();
        ContextMenu[] menus = roots.OfType<ContextMenu>().Concat(roots.SelectMany(Descendants<ContextMenu>)).Distinct().Where(menu => menu.IsOpen).ToArray();
        string rotationLabel = (string)owner.FindResource("Str_Ctx_RotateCWShort");
        MenuItem rotate = menus.SelectMany(menu => menu.Items.OfType<MenuItem>()).Single(item => Equals(item.Header, rotationLabel));
        try { rotate.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
        finally { foreach (ContextMenu menu in menus) menu.IsOpen = false; }
    }

    private static int ReadRotation(string source)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(source))])!;
        var pages = (System.Collections.IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfPageInformation", true)!
            .GetMethod("Read")!.Invoke(null, [document])!;
        object page = pages.Cast<object>().First();
        return (int)page.GetType().GetProperty("Rotation")!.GetValue(page)!;
    }

    private static void VerifyHistory(Window owner, string source, TextBox editor)
    {
        Stroke(0x0D, 0x11);
        Wait(() => !editor.IsVisible && Dirty(owner), "committed dirty annotation");
        Console.WriteLine("CONTROL text commit removed the editor and marked the document dirty.");
        Save(owner);
        Wait(() => !Dirty(owner), "successful clean save");
        if (!ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("The first successful save did not retain the typed annotation.");
        Console.WriteLine("CONTROL saved annotation strictly reopened with its marker.");

        Click(owner, "UndoBtn");
        Pump();
        if (!Dirty(owner)) throw new InvalidOperationException("APP-001: Undo after Save incorrectly reports clean content.");
        Click(owner, "RedoBtn");
        Pump();
        if (Dirty(owner)) throw new InvalidOperationException("Redo to the saved checkpoint must become clean.");
        Click(owner, "UndoBtn");
        Save(owner);
        Wait(() => !Dirty(owner), "saved undo result");
        if (ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("Saving the Undo result retained the removed annotation.");
    }

    private static void VerifyFailedSaveHistory(Window owner, string source, byte[] original, TextBox editor)
    {
        Stroke(0x0D, 0x11);
        Wait(() => !editor.IsVisible && Dirty(owner), "committed annotation before failed save");
        using (var locked = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            Save(owner, expectFailure: true);
        if (!Dirty(owner) || !File.ReadAllBytes(source).AsSpan().SequenceEqual(original))
            throw new InvalidOperationException("Failed Save changed the source or accepted a clean checkpoint.");
        Click(owner, "UndoBtn");
        if (Dirty(owner)) throw new InvalidOperationException("Undo after failed Save did not return to the original checkpoint.");
        Click(owner, "RedoBtn");
        if (!Dirty(owner)) throw new InvalidOperationException("Redo after failed Save lost its unsaved state.");
        Save(owner);
        if (!ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("Retry after releasing the destination lock lost the annotation.");
        Console.WriteLine("CONTROL failed Save preserved source/checkpoint; Undo, Redo and successful retry retained content.");
    }

    private static void VerifyFormHistory(Window owner, string source, TextBox editor)
    {
        Stroke(0x0D, 0x11);
        Wait(() => !editor.IsVisible && Dirty(owner), "committed annotation on form");
        Save(owner);
        Wait(() => !Dirty(owner), "saved form checkpoint");
        TextBox field = Descendants<TextBox>(owner).Single(box => Equals(box.ToolTip, "existing.text"));
        field.Focus();
        field.SelectAll();
        field.SelectedText = "CHANGED";
        Pump();
        if (!Dirty(owner)) throw new InvalidOperationException("The form edit did not mark the document dirty.");
        Click(owner, "UndoBtn");
        if (!Dirty(owner)) throw new InvalidOperationException("Annotation Undo hid the independent form edit.");
        Save(owner);
        Wait(() => !Dirty(owner), "saved independent form edit");
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!
            .Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(source))])!;
        object[] widgets = ((System.Collections.IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfFormWidgetReader", true)!
            .GetMethod("ReadPage")!.Invoke(null, [document, 0])!).Cast<object>().ToArray();
        object changed = widgets.Single(widget => Equals(widget.GetType().GetProperty("FieldName")!.GetValue(widget), "existing.text"));
        if (!Equals(changed.GetType().GetProperty("Value")!.GetValue(changed), "CHANGED")
            || ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("Saved annotation Undo lost the independent form value or retained the removed annotation.");
        Console.WriteLine("CONTROL annotation Undo preserved the independent form edit, dirty state and strictly reopened output.");
    }

    private static void VerifyAnnotationHistory(Window owner, string source, TextBox editor, string folder)
    {
        Stroke(0x0D, 0x11);
        Wait(() => !editor.IsVisible && Dirty(owner), "committed annotation for nudge");
        Click(owner, "ToolSelectBtn");
        TextBlock MarkerBlock() => Descendants<TextBlock>(owner).Single(block => block.IsVisible && block.Text == Marker);
        double AnnotationLeft() => Canvas.GetLeft((UIElement)MarkerBlock().Parent);
        TextBlock marker = MarkerBlock();
        Point target = marker.PointToScreen(new Point(Math.Min(5, marker.ActualWidth / 2), marker.ActualHeight / 2));
        if (target.X < 0 || target.Y < 0 || target.X >= SystemParameters.PrimaryScreenWidth
            || target.Y >= SystemParameters.PrimaryScreenHeight)
            throw new InvalidOperationException("The annotation selection target is outside the visible desktop.");
        if (!SetCursorPos((int)target.X, (int)target.Y)) throw new InvalidOperationException("Cannot select annotation.");
        MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
        MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
        Pump();
        var selectButton = (Button)owner.FindName("ToolSelectBtn");
        if (!selectButton.Focus() || !selectButton.IsKeyboardFocused)
            throw new InvalidOperationException("Cannot focus the owned Select toolbar button for arrow input.");
        Console.WriteLine("CONTROL selected the visible annotation and focused the owned toolbar for arrow routing.");
        var capture = new RenderTargetBitmap((int)Math.Ceiling(owner.ActualWidth), (int)Math.Ceiling(owner.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        capture.Render(owner);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(capture));
        using (var output = File.Create(Path.Combine(folder, "annotation-before-nudge.png"))) png.Save(output);
        double initial = AnnotationLeft();
        var nudge = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(owner),
            Environment.TickCount, Key.Right) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        owner.RaiseEvent(nudge);
        Pump();
        Console.WriteLine($"CONTROL Right command handled={nudge.Handled}, modifiers={Keyboard.Modifiers}, canvas before={initial}, after={AnnotationLeft()}.");
        if (!nudge.Handled) throw new InvalidOperationException("The public Right command did not reach a handled application action.");
        Wait(() => AnnotationLeft() > initial + 0.5, "one-unit annotation nudge in public canvas geometry");
        Console.WriteLine("CONTROL real selection and public Right Arrow command moved the placed annotation.");
        Click(owner, "UndoBtn");
        if (Math.Abs(AnnotationLeft() - initial) > 0.01)
            throw new InvalidOperationException("The first Undo did not restore the annotation position.");
        Click(owner, "UndoBtn");
        bool markerRemains = Descendants<TextBlock>(owner).Any(block => block.IsVisible && block.Text == Marker);
        Save(owner);
        Wait(() => !Dirty(owner), "saved annotation Undo result");
        if (markerRemains || ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("APP-002: Add, Nudge, Undo, Undo retained the cloned annotation.");
        Click(owner, "RedoBtn");
        if (Math.Abs(AnnotationLeft() - initial) > 0.01)
            throw new InvalidOperationException("Redo add did not restore the original annotation position.");
        Click(owner, "RedoBtn");
        if (Math.Abs(AnnotationLeft() - initial - 1) > 0.01 || !Dirty(owner))
            throw new InvalidOperationException("Redo move did not restore the moved annotation and unsaved state.");
        Save(owner);
        if (!ReadText(source).Contains(Marker, StringComparison.Ordinal))
            throw new InvalidOperationException("The saved Redo result lost the restored annotation.");
        Console.WriteLine("CONTROL Redo add and move restored exact canvas positions and strictly reopened saved content.");
    }

    private static void VerifyClose(Window owner, string source, byte[] original, TextBox editor)
    {
        int prompts = 0;
        Exception? failure = null;
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            try
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                    .Where(window => window != owner && window.IsVisible).ToArray())
                {
                    if (!seen.Add(dialog)) continue;
                    Button? cancel = Descendants<Button>(dialog).FirstOrDefault(button =>
                        Equals(button.Content, Application.Current.FindResource("Str_Btn_CancelDlg")));
                    if (cancel is null) throw new InvalidOperationException("Close showed a dialog without an unsaved-change Cancel choice.");
                    prompts++;
                    cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            }
            catch (Exception error) { failure = error; timer.Stop(); }
        };
        timer.Start();
        try
        {
            Stroke(0x73, 0x12);
            Wait(() => prompts != 0 || !owner.IsVisible || failure is not null, "Alt+F4 close decision");
            Pump();
            if (failure is not null) throw failure;
            if (prompts != 1 || !owner.IsVisible)
                throw new InvalidOperationException($"APP-006: active text close skipped protection: prompts={prompts}, visible={owner.IsVisible}.");
            if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(original))
                throw new InvalidOperationException("Cancel changed the original source.");
            if (!editor.Text.Contains(Marker, StringComparison.Ordinal))
                throw new InvalidOperationException("Cancel discarded the active text.");
            if (!Descendants<TextBlock>(owner).Any(block => block.IsVisible && block.Text == Marker))
                throw new InvalidOperationException("Cancel did not preserve the committed text on the page.");
            Console.WriteLine("CONTROL Cancel retained the typed text visibly and preserved the source bytes.");
        }
        finally { timer.Stop(); }

        int savePrompts = 0;
        var saveSeen = new HashSet<Window>();
        var saveTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        saveTimer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                .Where(window => window != owner && window.IsVisible).ToArray())
            {
                Button? save = Descendants<Button>(dialog).FirstOrDefault(button =>
                    Equals(button.Content, Application.Current.FindResource("Str_Btn_Save")));
                if (save is null || !saveSeen.Add(dialog)) continue;
                savePrompts++;
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
        };
        saveTimer.Start();
        try
        {
            Stroke(0x73, 0x12);
            Wait(() => !owner.IsVisible, "Save during close");
            if (savePrompts != 1 || !ReadText(source).Contains(Marker, StringComparison.Ordinal))
                throw new InvalidOperationException("Save during close did not preserve the pending text in the reopened source.");
            Console.WriteLine("CONTROL Save during close retained the typed text in the strictly reopened PDF.");
        }
        finally { saveTimer.Stop(); }
    }

    internal static void Save(Window owner, bool expectFailure = false)
    {
        int confirmations = 0;
        string? saveError = null;
        var seen = new HashSet<Window>();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            foreach (Window dialog in Application.Current.Windows.Cast<Window>()
                .Where(window => window != owner && window.IsVisible).ToArray())
            {
                Button? yes = Descendants<Button>(dialog).FirstOrDefault(button =>
                    Equals(button.Content, Application.Current.FindResource("Str_Btn_Yes")));
                Button? acceptError = expectFailure ? Descendants<Button>(dialog).FirstOrDefault(button =>
                    Equals(button.Content, Application.Current.FindResource("Str_Btn_OK"))) : null;
                if ((yes is null && acceptError is null) || !seen.Add(dialog)) continue;
                if (yes is not null)
                {
                    confirmations++;
                    yes.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                else
                {
                    saveError = string.Join(" ", Descendants<TextBlock>(dialog).Select(block => block.Text));
                    acceptError!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            }
        };
        timer.Start();
        try { Click(owner, "SaveAsBtn"); }
        finally { timer.Stop(); }
        if (confirmations != 1) throw new InvalidOperationException("Expected one overwrite confirmation for the synthetic source.");
        if (expectFailure && (saveError is null || !saveError.Contains(
            (string)Application.Current.FindResource("Str_Err_SaveFailed"), StringComparison.Ordinal)))
            throw new InvalidOperationException("The locked destination did not show the expected Save failure.");
        Console.WriteLine("CONTROL confirmed overwrite through the public Save dialog.");
    }

    private static string? ReadFormValue(string source, string name)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(source))])!;
        object[] widgets = ((System.Collections.IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfFormWidgetReader", true)!
            .GetMethod("ReadPage")!.Invoke(null, [document, 0])!).Cast<object>().ToArray();
        object widget = widgets.Single(item => Equals(item.GetType().GetProperty("FieldName")!.GetValue(item), name));
        return (string?)widget.GetType().GetProperty("Value")!.GetValue(widget);
    }

    private static string ReadText(string source)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        Type documentType = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!;
        object document = documentType.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!
            .Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(source))])!;
        Type readerType = engine.GetType("KillerPdf.Engine.Documents.PdfPageContentReader", true)!;
        object reader = Activator.CreateInstance(readerType, [document])!;
        object content = readerType.GetMethod("Read", [typeof(int), typeof(CancellationToken)])!
            .Invoke(reader, [0, CancellationToken.None])!;
        return (string)content.GetType().GetProperty("Text")!.GetValue(content)!;
    }

    private static void CaptureOwnedWindow(Window owner, string path)
    {
        owner.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(owner.ActualWidth), (int)Math.Ceiling(owner.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(owner);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        png.Save(output);
    }

    private static bool IsPageCanvas(Canvas canvas) => canvas.IsVisible && canvas.ActualWidth > 100
        && canvas.ActualHeight > 100 && canvas.GetType().Name == "SafeCanvas";
    private static bool Dirty(Window owner) => ((Button)owner.FindName("SaveAsBtn")).Foreground
        is SolidColorBrush brush && brush.Color == Color.FromRgb(0xE0, 0x73, 0);
    internal static void Click(Window owner, string name)
    {
        var button = (Button)owner.FindName(name);
        if (!button.IsEnabled) throw new InvalidOperationException(name + " is disabled.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
    }
    private static void Stroke(byte key, byte modifier)
    {
        if (modifier != 0) KeyEvent(modifier, 0, 0, UIntPtr.Zero);
        KeyEvent(key, 0, 0, UIntPtr.Zero);
        KeyEvent(key, 0, 2, UIntPtr.Zero);
        if (modifier != 0) KeyEvent(modifier, 0, 2, UIntPtr.Zero);
    }
    internal static void Wait(Func<bool> condition, string stage)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Pump();
            if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("Timed out at " + stage);
            Thread.Sleep(10);
        }
    }
    private static void Pump() => Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T item) yield return item;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "mouse_event")]
    private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeyEvent(byte key, byte scan, uint flags, UIntPtr extra);
}
