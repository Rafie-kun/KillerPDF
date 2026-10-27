using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static DocumentStateSmoke;

internal static class TooltipCursorSmoke
{
    internal static int RunMatrix(Window owner, string folder)
    {
        Run(owner, folder, "OverflowChevron", "ThemeDarkRadio", rightEdge: true);
        Run(owner, folder, "ShortcutHelpRailBtn", "ThemeLightRadio", rightEdge: true);
        Run(owner, folder, "OpenFileBtn", "Theme98SERadio", rightEdge: true);
        Run(owner, folder, "OpenFileBtn", "ThemeDarkRadio", maximumScale: true);
        Console.WriteLine("PASS tooltip surface matrix: actual toolbar overflow, sidebar help, classic Open and maximum-scale Open glyphs clear the sampled native cursor and work area.");
        return 0;
    }

    internal static int Run(Window owner, string folder, string targetName = "OpenFileBtn", string? themeRadio = null,
        bool maximumScale = false, bool rightEdge = false)
    {
        Rect work = SystemParameters.WorkArea;
        owner.WindowState = WindowState.Normal;
        owner.Left = work.Left + 8; owner.Top = work.Top + 8;
        owner.Width = Math.Min(950, work.Width - 16); owner.Height = Math.Min(700, work.Height - 16);
        if (rightEdge) { owner.Left = work.Right - owner.Width - 8; owner.Top = work.Bottom - owner.Height - 8; }
        owner.Opacity = 1; owner.Activate();
        if (themeRadio is not null)
        {
            Click(owner, "RailThemeBtn");
            var radio = (RadioButton)owner.FindName(themeRadio);
            Wait(() => radio.IsVisible && radio.IsEnabled, "actual tooltip theme picker");
            ((ISelectionItemProvider)new RadioButtonAutomationPeer(radio).GetPattern(PatternInterface.SelectionItem)!).Select();
            Wait(() => radio.IsChecked == true, "tooltip theme selected");
            ((ContextMenu)owner.FindName("ThemeFlyout")).IsOpen = false;
        }
        if (maximumScale)
        {
            var logo = (FrameworkElement)owner.FindName("LogoBar");
            for (int step = 0; step < 80 && Scale() != "2.5"; step++)
                logo.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.MouseWheelEvent });
            Wait(() => Scale() == "2.5", "maximum isolated app scale for tooltip");
        }
        var open = (FrameworkElement)owner.FindName(targetName);
        Wait(() => open.IsVisible && open.ActualWidth > 12 && work.Contains(new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight)),
            "owned tooltip target " + targetName);
        Rect previous = Rect.Empty;
        long stableSince = Environment.TickCount64;
        Point target = default;
        Wait(() =>
        {
            owner.UpdateLayout();
            Rect bounds = open.TransformToAncestor(owner).TransformBounds(new Rect(open.RenderSize));
            if (bounds != previous) { previous = bounds; stableSince = Environment.TickCount64; }
            Point screen = owner.PointToScreen(new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2));
            target = owner.PointFromScreen(new Point(Math.Round(screen.X), Math.Round(screen.Y)));
            DependencyObject? hit = owner.InputHitTest(target) as DependencyObject;
            while (hit is not null && !ReferenceEquals(hit, open)) hit = VisualTreeHelper.GetParent(hit);
            return hit is not null && Environment.TickCount64 - stableSince >= 200;
        }, "stable exact rounded-pixel tooltip target " + targetName);
        var pane = (FrameworkElement)owner.FindName("Viewer");
        Point away = pane.TranslatePoint(new Point(pane.ActualWidth / 2, pane.ActualHeight / 2), owner);
        ShortcutHintsSmoke.MovePointer(owner, away);
        Wait(() => !Tips().Any(), "tooltip absence control before hover");
        ShortcutHintsSmoke.MovePointer(owner, target);
        ToolTip? observedTip = null;
        Exception? primaryFailure = null;
        try
        {
            Wait(() => Tips().Any(), "actual hover tooltip for " + targetName);
            ToolTip tip = Tips().Single();
            observedTip = tip;
            var placementPath = new List<string>();
            bool expectedOwner = false;
            for (DependencyObject? node = tip.PlacementTarget; node is not null; node = node is Visual
                ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
            {
                placementPath.Add(Describe(node));
                if (ReferenceEquals(node, open)) { expectedOwner = true; break; }
            }
            Console.WriteLine($"CONTROL tooltip identity: requested={targetName}, placementPath={string.Join('>', placementPath)}, expectedOwner={expectedOwner}, directlyOver={Describe(Mouse.DirectlyOver as DependencyObject)}, pointer={Mouse.GetPosition(owner)}, targetBounds={previous}, text={string.Join('|', Descendants<TextBlock>(tip).Select(text => text.Text))}.");
            if (!expectedOwner)
            {
                if (tip.ActualWidth > 0 && tip.ActualHeight > 0)
                {
                    var actual = new RenderTargetBitmap((int)Math.Ceiling(tip.ActualWidth), (int)Math.Ceiling(tip.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    actual.Render(tip); SaveBitmap("unexpected-target-tooltip.png", actual);
                }
                throw new InvalidOperationException("The observed tooltip belongs to another control rather than " + targetName + ".");
            }
            Wait(() => tip.ActualWidth > 20 && tip.ActualHeight > 10, "rendered tooltip geometry");
            var cursor = new CursorState { Size = Marshal.SizeOf<CursorState>() };
            if (!GetCursorInfo(ref cursor) || cursor.Handle == IntPtr.Zero || (cursor.Flags & 1) == 0)
                throw new InvalidOperationException("The current visible native cursor could not be sampled.");
            if (!GetIconInfo(cursor.Handle, out IconInfo icon)) throw new InvalidOperationException("The cursor resource and hotspot could not be read.");
            try
            {
                IntPtr sizeBitmap = icon.Color != IntPtr.Zero ? icon.Color : icon.Mask;
                if (GetObjectInfo(sizeBitmap, Marshal.SizeOf<NativeBitmap>(), out NativeBitmap size) == 0)
                    throw new InvalidOperationException("The cursor resource size could not be read.");
                int width = size.Width, height = icon.Color != IntPtr.Zero ? size.Height : size.Height / 2;
                if (width is < 1 or > 1024 || height is < 1 or > 1024) throw new InvalidOperationException("Unsupported cursor resource dimensions.");
                byte[] black = RenderCursor(cursor.Handle, width, height, 0), white = RenderCursor(cursor.Handle, width, height, 255);
                Rect visible = VisibleBounds(black, white, width, height);
                if (visible.IsEmpty || visible.Width < 2 || visible.Height < 2
                    || !VisibleBounds(RenderCursor(IntPtr.Zero, width, height, 0), RenderCursor(IntPtr.Zero, width, height, 255), width, height).IsEmpty)
                    throw new InvalidOperationException("The cursor-pixel detector failed its presence or blank-resource control.");
                Rect cursorBounds = new(cursor.Position.X - icon.HotX + visible.Left, cursor.Position.Y - icon.HotY + visible.Top, visible.Width, visible.Height);
                Rect tooltipBounds = ScreenBounds(tip), textBounds = Rect.Empty, inkBounds = Rect.Empty;
                int textIndex = 0, overlappingGlyphPixels = 0;
                foreach (TextBlock text in Descendants<TextBlock>(tip).Where(text => text.IsVisible && !string.IsNullOrWhiteSpace(text.Text)))
                {
                    textBounds.Union(ScreenBounds(text));
                    inkBounds.Union(GlyphBounds(text, textIndex++, cursorBounds, out int overlapping));
                    overlappingGlyphPixels += overlapping;
                }
                if (textBounds.IsEmpty) throw new InvalidOperationException("The tooltip has no actual visible text bounds.");
                var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (!GetMonitorInfo(MonitorFromPoint(cursor.Position, 2), ref monitor)) throw new InvalidOperationException("The current monitor work area could not be read.");
                Rect physicalWork = new(monitor.Work.Left, monitor.Work.Top, monitor.Work.Right - monitor.Work.Left, monitor.Work.Bottom - monitor.Work.Top);
                bool overlap = overlappingGlyphPixels != 0;
                File.WriteAllText(Output("tooltip-cursor-bounds.json"), JsonSerializer.Serialize(new
                {
                    targetName, themeRadio, scale = Scale(),
                    cursorResource = new { width, height, hotspotX = icon.HotX, hotspotY = icon.HotY },
                    visibleCursor = cursorBounds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    tooltip = tooltipBounds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    text = textBounds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    textInk = inkBounds.ToString(System.Globalization.CultureInfo.InvariantCulture), overlappingGlyphPixels,
                    layoutOverlap = PositiveOverlap(textBounds, cursorBounds),
                    workArea = physicalWork.ToString(System.Globalization.CultureInfo.InvariantCulture), overlap,
                    scope = "Current native cursor and actual named hover target only; no OS pointer settings changed."
                }, new JsonSerializerOptions { WriteIndented = true }));
                SaveBitmap("cursor-on-black.png", BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, black, width * 4));
                SaveBitmap("cursor-on-white.png", BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, white, width * 4));
                var rendered = new RenderTargetBitmap((int)Math.Ceiling(tip.ActualWidth), (int)Math.Ceiling(tip.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                rendered.Render(tip); SaveBitmap("actual-open-tooltip.png", rendered);
                Console.WriteLine($"CONTROL tooltip cursor: resource={width}x{height}, visibleCursor={cursorBounds}, textLayout={textBounds}, textInk={inkBounds}, overlappingGlyphPixels={overlappingGlyphPixels}, tooltip={tooltipBounds}, overlap={overlap}; actual-cursor and blank controls passed.");
                if (overlap || !physicalWork.Contains(tooltipBounds))
                    throw new InvalidOperationException("USER-003: actual tooltip text overlaps the current visible cursor or extends beyond the work area.");
            }
            finally { if (icon.Mask != IntPtr.Zero) DeleteObject(icon.Mask); if (icon.Color != IntPtr.Zero) DeleteObject(icon.Color); }
        }
        catch (Exception error) { primaryFailure = error; throw; }
        finally
        {
            try { ShortcutHintsSmoke.MovePointer(owner, away); Wait(() => observedTip is null || !observedTip.IsOpen, "specific tooltip closes after client-area hover for " + targetName); }
            catch (Exception cleanup) when (primaryFailure is not null) { Console.Error.WriteLine("CONTROL tooltip cleanup also failed: " + cleanup); }
        }
        Console.WriteLine("PASS tooltip cursor: actual " + targetName + " hover text clears the current visible cursor and remains within the monitor work area; larger-pointer and other-position cases remain separate.");
        return 0;

        string? Scale()
        {
            string path = Path.Combine(folder, "data", "settings.json");
            if (!File.Exists(path)) return null;
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(path));
            return settings.RootElement.TryGetProperty("AppScale", out JsonElement value) ? value.GetString() : null;
        }
        string Output(string name) => Path.Combine(folder, themeRadio is null ? name : targetName + "-" + themeRadio + "-" + name);
        static string Describe(DependencyObject? target) => target is FrameworkElement element
            ? element.GetType().Name + ":" + element.Name : target?.GetType().Name ?? "null";
        IEnumerable<ToolTip> Tips() => PresentationSource.CurrentSources.Cast<PresentationSource>()
            .Select(source => source.RootVisual).OfType<DependencyObject>()
            .SelectMany(root => (root is ToolTip tip ? new[] { tip } : Array.Empty<ToolTip>()).Concat(Descendants<ToolTip>(root)))
            .Where(tip => tip.IsOpen && tip.IsVisible).Distinct();
        static Rect ScreenBounds(FrameworkElement element) => new(element.PointToScreen(new Point()), element.PointToScreen(new Point(element.ActualWidth, element.ActualHeight)));
        static bool PositiveOverlap(Rect first, Rect second)
        {
            Rect intersection = Rect.Intersect(first, second);
            return !intersection.IsEmpty && intersection.Width > 0 && intersection.Height > 0;
        }
        Rect GlyphBounds(TextBlock text, int index, Rect cursorBounds, out int overlapping)
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(text);
            int width = (int)Math.Ceiling(text.ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(text.ActualHeight * dpi.DpiScaleY);
            var drawing = new DrawingVisual();
            using (DrawingContext context = drawing.RenderOpen())
                context.DrawRectangle(new VisualBrush(text) { AutoLayoutContent = false,
                    ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, text.ActualWidth, text.ActualHeight) },
                    null, new Rect(0, 0, text.ActualWidth, text.ActualHeight));
            var bitmap = new RenderTargetBitmap(width, height, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            byte[] pixels = new byte[checked(width * height * 4)]; bitmap.CopyPixels(pixels, width * 4, 0);
            Rect bounds = Rect.Empty; overlapping = 0;
            int visiblePixels = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    if (pixels[(y * width + x) * 4 + 3] == 0) continue;
                    visiblePixels++;
                    Rect pixel = new(text.PointToScreen(new Point(x / dpi.DpiScaleX, y / dpi.DpiScaleY)),
                        text.PointToScreen(new Point((x + 1) / dpi.DpiScaleX, (y + 1) / dpi.DpiScaleY)));
                    bounds.Union(pixel);
                    if (PositiveOverlap(pixel, cursorBounds)) overlapping++;
                }
            if (visiblePixels == 0 || visiblePixels == width * height)
                throw new InvalidOperationException("The tooltip glyph render has no text ink or an opaque background.");
            SaveBitmap("actual-open-tooltip-text-" + index + ".png", bitmap);
            return bounds;
        }
        void SaveBitmap(string name, BitmapSource bitmap)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Output(name)); encoder.Save(output);
        }
    }

    private static byte[] RenderCursor(IntPtr cursor, int width, int height, byte background)
    {
        int length = checked(width * height * 4);
        byte[] pixels = new byte[length];
        for (int index = 0; index < length; index += 4) { pixels[index] = pixels[index + 1] = pixels[index + 2] = background; pixels[index + 3] = 255; }
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero), bitmap = IntPtr.Zero, previous = IntPtr.Zero;
        if (dc == IntPtr.Zero) throw new InvalidOperationException("Cannot allocate a cursor-only memory context.");
        try
        {
            var header = new DibHeader { Size = (uint)Marshal.SizeOf<DibHeader>(), Width = width, Height = -height, Planes = 1, Bits = 32, ImageSize = (uint)length };
            bitmap = CreateDIBSection(dc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new InvalidOperationException("Cannot allocate a cursor-only bitmap.");
            previous = SelectObject(dc, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new InvalidOperationException("Cannot select the cursor-only bitmap.");
            Marshal.Copy(pixels, 0, bits, length);
            if (cursor != IntPtr.Zero && !DrawIconEx(dc, 0, 0, cursor, width, height, 0, IntPtr.Zero, 3))
                throw new InvalidOperationException("Cannot draw the sampled cursor resource in memory.");
            if (!GdiFlush()) throw new InvalidOperationException("Cannot finish the cursor-only memory drawing.");
            Marshal.Copy(bits, pixels, 0, length);
            for (int index = 3; index < length; index += 4) pixels[index] = 255;
            return pixels;
        }
        finally { if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous); if (bitmap != IntPtr.Zero) DeleteObject(bitmap); DeleteDC(dc); }
    }

    private static Rect VisibleBounds(byte[] black, byte[] white, int width, int height)
    {
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 4;
                if (black[index] == 0 && black[index + 1] == 0 && black[index + 2] == 0
                    && white[index] == 255 && white[index + 1] == 255 && white[index + 2] == 255) continue;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
        return right < left ? Rect.Empty : new Rect(left, top, right - left + 1, bottom - top + 1);
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct CursorState { public int Size, Flags; public IntPtr Handle; public NativePoint Position; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public int HotX, HotY; public IntPtr Mask, Color; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeBitmap { public int Type, Width, Height, Stride; public ushort Planes, Bits; public IntPtr Data; }
    [StructLayout(LayoutKind.Sequential)] private struct DibHeader { public uint Size; public int Width, Height; public ushort Planes, Bits; public uint Compression, ImageSize; public int XPels, YPels; public uint Colors, Important; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorState state);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr cursor, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr cursor, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObjectInfo(IntPtr bitmap, int size, out NativeBitmap info);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref DibHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
