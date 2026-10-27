using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static DocumentStateSmoke;

internal static class OrientationFailureSmoke
{
    internal static int VerifyFixture(Assembly engine)
    {
        Type documentType = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!;
        object document = documentType.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!
            .Invoke(null, [new ReadOnlyMemory<byte>(TableExportSmoke.Fixture(sidewaysText: true))])!;
        object[] pages = ((IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfPageInformation", true)!
            .GetMethod("Read")!.Invoke(null, [document])!).Cast<object>().ToArray();
        if (pages.Length != 1 || (int)pages[0].GetType().GetProperty("Rotation")!.GetValue(pages[0])! != 0)
            throw new InvalidOperationException("Orientation fixture did not strictly reopen as one unrotated page.");
        object reader = Activator.CreateInstance(engine.GetType("KillerPdf.Engine.Documents.PdfPageContentReader", true)!, [document])!;
        object content = reader.GetType().GetMethod("Read", [typeof(int), typeof(CancellationToken)])!
            .Invoke(reader, [0, CancellationToken.None])!;
        object[] letters = ((IEnumerable)content.GetType().GetProperty("Letters")!.GetValue(content)!).Cast<object>()
            .Where(letter => ((string)letter.GetType().GetProperty("Value")!.GetValue(letter)!).Any(char.IsLetter)).ToArray();
        if (letters.Length < 20 || ((IEnumerable)content.GetType().GetProperty("Images")!.GetValue(content)!).Cast<object>().Any())
            throw new InvalidOperationException("Orientation fixture lacks its vector-text presence control.");
        foreach (object letter in letters)
        {
            object bounds = letter.GetType().GetProperty("BoundingBox")!.GetValue(letter)!;
            if ((double)bounds.GetType().GetProperty("Left")!.GetValue(bounds)! <= 100)
                throw new InvalidOperationException("Orientation fixture does not leave the intended x=50 text-entry area blank.");
            object start = letter.GetType().GetProperty("StartBaseLine")!.GetValue(letter)!;
            object end = letter.GetType().GetProperty("EndBaseLine")!.GetValue(letter)!;
            double X(object point) => (double)point.GetType().GetProperty("X")!.GetValue(point)!;
            double Y(object point) => (double)point.GetType().GetProperty("Y")!.GetValue(point)!;
            if (Math.Abs(X(end) - X(start)) > 0.0001 || Y(end) <= Y(start))
                throw new InvalidOperationException("Orientation fixture does not have a consistent positive vertical baseline.");
        }
        Console.WriteLine($"PASS orientation fixture: {letters.Length} sideways vector letters, positive vertical baselines, one unrotated page and no images; no Window created.");
        return 0;
    }

    internal static int Run(Window owner, string folder)
    {
        const string marker = "Z";
        VerifyFixture(Assembly.Load("KillerPdf.Engine"));
        string source = Path.Combine(folder, "orientation-source.pdf");
        byte[] original = TableExportSmoke.Fixture(sidewaysText: true);
        File.WriteAllBytes(source, original);
        Rect work = SystemParameters.WorkArea;
        owner.Left = work.Left + 8; owner.Top = work.Top + 8;
        owner.Width = Math.Min(950, work.Width - 16); owner.Height = Math.Min(700, work.Height - 16);
        owner.Opacity = 1; owner.Activate();
        ShortcutHintsSmoke.Focus(owner);
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
        try
        {
            owner.GetType().GetMethod("OpenFromExternal", [typeof(string)])!.Invoke(owner, [source]);
            Wait(() => Descendants<Canvas>(owner).Any(IsPageCanvas) && !Dirty(owner), "clean sideways page rendered");
            Click(owner, "ToolTextBtn");
            Canvas canvas = Descendants<Canvas>(owner).Where(IsPageCanvas).OrderByDescending(item => item.ActualWidth * item.ActualHeight).First();
            Point screen = canvas.PointToScreen(new Point(canvas.ActualWidth * .25, canvas.ActualHeight * .72));
            Point target = owner.PointFromScreen(new Point(Math.Round(screen.X), Math.Round(screen.Y)));
            ShortcutHintsSmoke.InjectClickAt(owner, target, () =>
            {
                DependencyObject? hit = owner.InputHitTest(target) as DependencyObject;
                while (hit is not null && !ReferenceEquals(hit, canvas)) hit = hit is Visual ? VisualTreeHelper.GetParent(hit) : LogicalTreeHelper.GetParent(hit);
                if (hit is null) throw new InvalidOperationException("The rounded Text target is not on the owned annotation canvas.");
            });
            Wait(() => Keyboard.FocusedElement is TextBox { AcceptsReturn: true }, "native Text editor on sideways page");
            var editor = (TextBox)Keyboard.FocusedElement;
            Console.WriteLine($"CONTROL orientation Text entry: initialCharacters={editor.Text.Length}, targetX=50 PDF points.");
            if (editor.Text.Length != 0) throw new InvalidOperationException("The Text target entered existing page content instead of a blank overlay editor.");
            editor.Text = marker;
            ShortcutHintsSmoke.Key(owner, 0x0D, 0x11);
            Border? Marker() => Descendants<Border>(owner).FirstOrDefault(border => border.IsVisible && border.Parent is Canvas
                && Descendants<TextBlock>(border).Any(text => text.Text == marker));
            Wait(() => Marker() is not null && Dirty(owner), "pending orientation annotation");
            Point StablePosition()
            {
                Point? last = null; long unchanged = Environment.TickCount64;
                Wait(() =>
                {
                    if (Marker() is not Border border || border.Parent is not Canvas page || page.ActualWidth < 100 || page.ActualHeight < 100) return false;
                    Point point = border.TranslatePoint(new Point(border.ActualWidth / 2, border.ActualHeight / 2), page);
                    point = new Point(point.X / page.ActualWidth, point.Y / page.ActualHeight);
                    if (last is null || (point - last.Value).Length > .0001) { last = point; unchanged = Environment.TickCount64; }
                    return Environment.TickCount64 - unchanged >= 200;
                }, "settled public annotation position");
                return last!.Value;
            }
            Point ZoomCycle()
            {
                double Width() => (Marker()?.Parent as Canvas)?.ActualWidth ?? 0;
                double before = Width(); Click(owner, "ZoomInBtn");
                Wait(() => Width() > before + 1, "public zoom-in changed the annotation viewport");
                double enlarged = Width(); Click(owner, "ZoomOutBtn");
                Wait(() => Width() > 100 && Width() < enlarged - 1, "public zoom-out repainted the annotation viewport");
                return StablePosition();
            }
            Point beforePosition = StablePosition();
            Point baseline = ZoomCycle();
            if ((baseline - beforePosition).Length > .01) throw new InvalidOperationException("The independent zoom-cycle position control is unstable.");
            string identity = ((TextBlock)owner.FindName("FileNameLabel")).Text;
            bool undo = ((Button)owner.FindName("UndoBtn")).IsEnabled, redo = ((Button)owner.FindName("RedoBtn")).IsEnabled;
            CaptureOwnedWindow(owner, Path.Combine(folder, "orientation-before-failure.png"));
            string session = Directory.GetDirectories(Path.Combine(folder, "data", "Temp"), "session-*").Single();
            var directory = new DirectoryInfo(session);
            string originalSddl = directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            File.WriteAllText(Path.Combine(folder, "orientation-original-temp-access.txt"), originalSddl);
            DirectorySecurity denied = directory.GetAccessControl(AccessControlSections.Access);
            denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
            int errors = 0; Exception? failure = null;
            var seen = new HashSet<Window>();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) =>
            {
                foreach (Window dialog in Application.Current.Windows.Cast<Window>().Where(window => window != owner && window.IsVisible).ToArray())
                {
                    if (!seen.Add(dialog)) continue;
                    string text = string.Join(" ", Descendants<TextBlock>(dialog).Select(block => block.Text));
                    Button? ok = Descendants<Button>(dialog).FirstOrDefault(button => Equals(button.Content, owner.FindResource("Str_Btn_OK")));
                    if (ok is null || !text.Contains(session, StringComparison.OrdinalIgnoreCase) || !text.Contains("serialized", StringComparison.OrdinalIgnoreCase))
                    { failure = new InvalidOperationException("Unexpected orientation dialog before scoped serialization failure: " + text); dialog.Close(); continue; }
                    errors++; Console.WriteLine("CONTROL orientation serialization error: " + text);
                    ok.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
            };
            try
            {
                directory.SetAccessControl(denied);
                bool blocked = false;
                try { File.WriteAllBytes(Path.Combine(session, "orientation-write-denial-control"), [1]); }
                catch (UnauthorizedAccessException) { blocked = true; }
                if (!blocked) throw new InvalidOperationException("The scoped temporary-write denial was ineffective.");
                timer.Start(); InvokeOrientation(owner);
                Wait(() => errors == 1 || failure is not null, "orientation error after actual serialization attempt");
                if (failure is not null) throw failure;
            }
            finally
            {
                timer.Stop();
                var restored = new DirectorySecurity(); restored.SetSecurityDescriptorSddlForm(originalSddl, AccessControlSections.Access);
                directory.SetAccessControl(restored);
                if (directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access) != originalSddl)
                    throw new InvalidOperationException("Orientation fixture did not restore its exact temporary-directory permissions.");
            }
            Point after = ZoomCycle();
            CaptureOwnedWindow(owner, Path.Combine(folder, "orientation-after-failure.png"));
            Console.WriteLine($"CONTROL orientation failure: errors={errors}, before={baseline}, after={after}, dirty={Dirty(owner)}, sourceUnchanged={File.ReadAllBytes(source).AsSpan().SequenceEqual(original)}.");
            if ((after - baseline).Length > .01 || !Dirty(owner) || !File.ReadAllBytes(source).AsSpan().SequenceEqual(original)
                || ReadRotation(source) != 0 || ((TextBlock)owner.FindName("FileNameLabel")).Text != identity
                || ((Button)owner.FindName("UndoBtn")).IsEnabled != undo || ((Button)owner.FindName("RedoBtn")).IsEnabled != redo)
                throw new InvalidOperationException("APP-004: failed Fix Page Orientation changed pending annotation geometry, rotation, identity or history.");
            ShortcutHintsSmoke.Focus(owner); Save(owner);
            if (!ReadText(source).Contains(marker) || ReadRotation(source) != 0) throw new InvalidOperationException("Save after failed orientation lost the original annotation or rotation.");
            Click(owner, "UndoBtn"); Save(owner);
            if (ReadText(source).Contains(marker) || ReadRotation(source) != 0) throw new InvalidOperationException("Failed orientation added phantom Undo history.");
            Click(owner, "RedoBtn"); Save(owner);
            if (!ReadText(source).Contains(marker) || ReadRotation(source) != 0) throw new InvalidOperationException("Redo after failed orientation lost annotation state.");
            InvokeOrientation(owner); Wait(() => Dirty(owner), "successful orientation retry"); Save(owner);
            if (ReadRotation(source) != 90 || !ReadText(source).Contains(marker)) throw new InvalidOperationException("Orientation retry did not retain annotation and expected quarter-turn.");
            Click(owner, "UndoBtn"); Save(owner);
            if (ReadRotation(source) != 0 || !ReadText(source).Contains(marker)) throw new InvalidOperationException("Orientation Undo lost annotation or original rotation.");
            Click(owner, "RedoBtn"); Save(owner);
            if (ReadRotation(source) != 90 || !ReadText(source).Contains(marker)) throw new InvalidOperationException("Orientation Redo lost annotation or quarter-turn.");
            Console.WriteLine("PASS orientation write failure: pending geometry, identity and history survive; strict Save/Undo/Redo and successful retry retain annotation and rotation.");
            return 0;
        }
        finally
        {
            cleanup.Start();
            try { if (owner.IsVisible) owner.Close(); Wait(() => !owner.IsVisible, "owned orientation fixture cleanup"); }
            finally { cleanup.Stop(); }
        }
    }

    private static void InvokeOrientation(Window owner)
    {
        var pages = (ListBox)owner.FindName("PageList"); pages.SelectedIndex = 0;
        var thumbnail = (ListBoxItem)pages.ItemContainerGenerator.ContainerFromIndex(0);
        thumbnail.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right) { RoutedEvent = Mouse.MouseUpEvent });
        Pump();
        var roots = PresentationSource.CurrentSources.Cast<PresentationSource>().Select(source => source.RootVisual).OfType<DependencyObject>();
        ContextMenu[] menus = roots.OfType<ContextMenu>().Concat(roots.SelectMany(Descendants<ContextMenu>)).Distinct().Where(menu => menu.IsOpen).ToArray();
        MenuItem action = menus.SelectMany(menu => menu.Items.OfType<MenuItem>()).Single(item => Equals(item.Header, owner.FindResource("Str_Pages_FixOrientation")));
        try { action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); }
        finally { foreach (ContextMenu menu in menus) menu.IsOpen = false; }
    }
}
