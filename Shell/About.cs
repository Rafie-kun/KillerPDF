using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using KillerPDF.Features;

namespace KillerPDF
{
    /// <summary>
    /// The About overlay's window half: the IAboutHost implementation that maps the controller's
    /// values onto the named XAML elements, the inline construction the card needs, and the click
    /// handlers. All the logic - signature, hashing, update check, self-update - lives in
    /// <see cref="AboutController"/>.
    ///
    /// NOTE: this stays "namespace KillerPDF" rather than KillerPDF.Shell, because it is a partial
    /// of MainWindow and every partial of a class must share one namespace. It moves to
    /// KillerPDF.Shell when MainWindow itself does.
    /// </summary>
    public partial class MainWindow : IAboutHost
    {
        private AboutController? _aboutController;
        private AboutController About => _aboutController ??= new AboutController(this);

        private void VersionLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ShowAboutOverlay();
        }

        private void ShowAboutOverlay() => About.Show();

        private void CloseAboutOverlay()
        {
            if (_aboutHost is { } host) FadeOverlayOut(AboutOverlay, () => host.Close());
            else FadeOverlayOut(AboutOverlay);
        }

        // ---- About above dialogs and tool windows ----------------------------------------------
        //
        // The card is a main-window overlay. A tool window such as Transform, or a dialog, is owned
        // by the main window and always sits above it, so an overlay drawn there would open behind
        // that window and leave it in front of the darkened backdrop. While one is open, the same
        // card moves into a transparent window over the main window, owned by the window in front,
        // and comes back when About closes. F12 also opens it from those windows.

        private Window? _aboutHost;
        private Panel? _aboutHome;
        private int _aboutHomeIndex;

        static MainWindow()
        {
            EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent,
                new KeyEventHandler(OwnedWindowAboutKey));
        }

        /// <summary>F12 (or whatever key About is mapped to) pressed in a window the main window
        /// owns, directly or through another owned window, opens About above it.</summary>
        private static void OwnedWindowAboutKey(object sender, KeyEventArgs e)
        {
            if (e.Handled || sender is not Window window || window is MainWindow) return;
            MainWindow? main = null;
            for (Window? owner = window.Owner; owner is not null; owner = owner.Owner)
                if (owner is MainWindow found) { main = found; break; }
            if (main is null || ReferenceEquals(window, main._aboutHost) || !KeyboardShortcutsEnabled) return;
            var (key, _, _, swallow) = main.TranslateShortcut(e.Key, Keyboard.Modifiers, e.SystemKey);
            if (swallow || key != Key.F12) return;
            main.ShowAboutOverlay();
            e.Handled = true;
        }

        /// <summary>The window About should open above: the active window the main window owns,
        /// else the newest one still showing, else none and the card stays a main-window overlay.</summary>
        private Window? AboutFrontWindow()
        {
            Window? active = null, newest = null;
            foreach (Window window in Application.Current.Windows)
            {
                if (ReferenceEquals(window, this) || ReferenceEquals(window, _aboutHost) || !window.IsVisible) continue;
                bool ours = false;
                for (Window? owner = window.Owner; owner is not null; owner = owner.Owner)
                    if (ReferenceEquals(owner, this)) { ours = true; break; }
                if (!ours) continue;
                newest = window;
                if (window.IsActive) active = window;
            }
            return active ?? newest;
        }

        /// <summary>Moves the card into a borderless transparent window that covers the main
        /// window, owned by <paramref name="front"/> so it sits above it.</summary>
        private void HostAboutAbove(Window front)
        {
            if (_aboutHost is not null || AboutOverlay.Parent is not Panel home) return;
            _aboutHome = home;
            _aboutHomeIndex = home.Children.IndexOf(AboutOverlay);
            home.Children.Remove(AboutOverlay);

            // Cover the main window's frame, or the front window when the main one is minimized.
            FrameworkElement area = WindowState == WindowState.Minimized ? front : RootBorder;
            var source = PresentationSource.FromVisual(area);
            Point topLeft = area.PointToScreen(new Point(0, 0));
            if (source?.CompositionTarget is { } target) topLeft = target.TransformFromDevice.Transform(topLeft);
            double width = area.ActualWidth, height = area.ActualHeight;
            double radius = ReferenceEquals(area, RootBorder) ? RootBorder.CornerRadius.TopLeft : 0;

            var frame = new Grid { Clip = new RectangleGeometry(new Rect(0, 0, width, height), radius, radius) };
            frame.Children.Add(AboutOverlay);
            var host = new Window
            {
                Owner = front,
                Title = Title,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = topLeft.X,
                Top = topLeft.Y,
                Width = width,
                Height = height,
                FontFamily = FontFamily,
                Content = frame
            };
            // The card's styles and brushes live in the main window's resources.
            host.Resources.MergedDictionaries.Add(Resources);
            host.PreviewKeyDown += (_, e) =>
            {
                var (key, _, _, swallow) = TranslateShortcut(e.Key, Keyboard.Modifiers, e.SystemKey);
                if (!swallow && (key == Key.Escape || key == Key.F12))
                {
                    CloseAboutOverlay();
                    e.Handled = true;
                }
            };
            host.Closed += (_, _) => ReturnAboutHome(front);
            _aboutHost = host;
            host.Show();
        }

        /// <summary>Puts the card back in the main window after the host window closes.</summary>
        private void ReturnAboutHome(Window front)
        {
            if (AboutOverlay.Parent is Panel frame) frame.Children.Remove(AboutOverlay);
            if (_aboutHome is { } home)
                home.Children.Insert(Math.Clamp(_aboutHomeIndex, 0, home.Children.Count), AboutOverlay);
            AboutOverlay.BeginAnimation(UIElement.OpacityProperty, null);
            AboutOverlay.Opacity = 1;
            AboutOverlay.Visibility = Visibility.Collapsed;
            _aboutHost = null;
            _aboutHome = null;
            if (front.IsVisible) front.Activate();
        }

        // ---- IShellServices ------------------------------------------------------------------

        Window IShellServices.Window => this;

        // MainWindow's own Loc and SetStatus are private, and a private member cannot implement an
        // interface. Forwarding explicitly satisfies IShellServices without widening either of them
        // to public, which would be a change nobody asked for.
        string IShellServices.Loc(string key) => Loc(key);
        void IShellServices.SetStatus(string text) => SetStatus(text);

        // ---- IAboutHost ----------------------------------------------------------------------

        string IAboutHost.Publisher   { set => AboutPublisherBlock.Text  = value; }
        string IAboutHost.Thumbprint  { set => AboutThumbprintBlock.Text = value; }
        string IAboutHost.Sha256      { set => AboutSha256Block.Text     = value; }
        string IAboutHost.ReleaseDate { set => AboutReleaseDateBlock.Text = value; }
        string IAboutHost.UpdateText  { set => AboutUpdateText.Text      = value; }

        bool IAboutHost.UpdateVisible
        {
            set => AboutUpdateButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }

        bool IAboutHost.UpdateEnabled { set => AboutUpdateButton.IsEnabled = value; }

        bool IAboutHost.IsDirty => Viewer.IsDirtyRef || ViewerB.IsDirtyRef
            || AllSessions().Any(session => session.IsDirty);

        string? IAboutHost.FileToReopen => _originalFile ?? _currentFile;

        /// <summary>Version line, as a hyperlink through to that release tag.</summary>
        void IAboutHost.SetVersion(string version)
        {
            AboutVersionBlock.Inlines.Clear();
            AboutVersionBlock.Inlines.Add(AccentLink($"v{version}", () => AboutController.OpenReleaseNotes()));
        }

        /// <summary>The AKA line. Null hides it entirely.</summary>
        void IAboutHost.SetAlias(string? alias)
        {
            AboutAkaBlock.Visibility = alias is null ? Visibility.Collapsed : Visibility.Visible;
            if (alias is null) return;

            AboutAkaBlock.Inlines.Clear();
            AboutAkaBlock.Inlines.Add(new Run(Loc("Str_About_Aka") + " ") { Foreground = Res("MutedTextBrush") });
            var hl = AccentLink(alias, () => AboutController.OpenUrl("https://thekiller.net"));
            hl.ToolTip = "thekiller.net";
            AboutAkaBlock.Inlines.Add(hl);
        }

        /// <summary>Dismisses any other full-window overlay, then fades the card in. The overlays
        /// are mutually exclusive rather than stacking on top of one another.</summary>
        void IAboutHost.ShowCard()
        {
            if (ShortcutOverlay.Visibility == Visibility.Visible) FadeOverlayOut(ShortcutOverlay);

            BuildAboutStaticContent();
            if (_aboutHost is null && AboutFrontWindow() is { } front) HostAboutAbove(front);
            FadeOverlayIn(AboutOverlay);
        }

        // ---- Card content that never varies with the signature or the update state ------------

        private void BuildAboutStaticContent()
        {
            StartupUpdateCheck.IsChecked = Services.ReleaseUpdateCheck.IsEnabled(
                App.GetSetting(Services.ReleaseUpdateCheck.Setting));
            // The family film-grain texture on the About card, repainted with the theme.
            Controls.GrainTexture.Track(AboutGrainBrush);

            BuildAboutWordmark();
            BuildAboutTagline();
        }

        /// <summary>"Killer" in the text color, "PDF" in the brand green, the pair clickable.</summary>
        private void BuildAboutWordmark()
        {
            AboutLogoBlock.Inlines.Clear();
            var hl = new Hyperlink { TextDecorations = null };
            hl.Inlines.Add(new Run("Killer")
            {
                FontFamily = UiKit.WordmarkFont,
                FontSize   = 16.43,
                FontWeight = FontWeights.Normal,
                Foreground = Res("TextBrush")
            });
            hl.Inlines.Add(new Run("PDF")
            {
                FontFamily = UiKit.WordmarkFontPdf,
                FontSize   = 31.83,
                FontWeight = FontWeights.Normal,
                Foreground = Res("AccentLogo")
            });
            hl.Click += (_, _) => AboutController.OpenUrl("https://killerpdf.net");
            AboutLogoBlock.Inlines.Add(hl);

            // The shadow copy mirrors the real runs exactly (sizes, weights, fonts), so the blur
            // sits directly behind each letter instead of smearing above the word - a flat
            // single-size copy had different line metrics than the two-run wordmark.
            AboutLogoShadowBlock.Inlines.Clear();
            var shadowBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0xB0, 0, 0, 0));
            shadowBrush.Freeze();
            AboutLogoShadowBlock.Inlines.Add(new Run("Killer")
            {
                FontFamily = UiKit.WordmarkFont,
                FontSize   = 16.43,
                FontWeight = FontWeights.Normal,
                Foreground = shadowBrush
            });
            AboutLogoShadowBlock.Inlines.Add(new Run("PDF")
            {
                FontFamily = UiKit.WordmarkFontPdf,
                FontSize   = 31.83,
                FontWeight = FontWeights.Normal,
                Foreground = shadowBrush
            });
        }

        /// <summary>
        /// Localized tagline. {0} is the (untranslated) brand, so splitting on the placeholder keeps
        /// "Killer Tools" a styled, clickable link while the rest translates and the brand can sit
        /// anywhere in the sentence the language needs it. A "\n" in the localized string marks the
        /// line break; the second line carries the license and the brand.
        /// </summary>
        private void BuildAboutTagline()
        {
            AboutTaglineBlock.Inlines.Clear();
            var dim  = Res("MutedTextBrush");
            var text = Loc("Str_Tagline");

            int brand  = text.IndexOf("{0}", System.StringComparison.Ordinal);
            string pre = brand >= 0 ? text[..brand]        : text;
            string suf = brand >= 0 ? text[(brand + 3)..]  : "";

            void AddText(string s)
            {
                var lines = s.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0) AboutTaglineBlock.Inlines.Add(new LineBreak());
                    AboutTaglineBlock.Inlines.Add(new Run(lines[i]) { Foreground = dim });
                }
            }

            AddText(pre);
            AboutTaglineBlock.Inlines.Add(
                AccentLink("Killer Tools", () => AboutController.OpenUrl("https://killertools.net")));
            AddText(suf);
        }

        // ---- Small helpers -------------------------------------------------------------------

        private Brush Res(string key) => (Brush)FindResource(key);

        /// <summary>An accent-colored hyperlink with no underline - the family's one treatment for
        /// "this is clickable" on a card.</summary>
        private Hyperlink AccentLink(string text, System.Action onClick)
        {
            var hl = new Hyperlink(new Run(text))
            {
                Foreground      = Res("PrimaryBrush"),
                TextDecorations = null
            };
            hl.Click += (_, _) => onClick();
            return hl;
        }

        // ---- Handlers ------------------------------------------------------------------------

        // Click the dim backdrop to dismiss; a click on the card itself is swallowed.
        private void AboutOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => CloseAboutOverlay();

        private void AboutOverlayCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => e.Handled = true;

        private void AboutOverlayClose_Click(object sender, RoutedEventArgs e)
            => CloseAboutOverlay();

        private void AboutUpdateButton_Click(object sender, RoutedEventArgs e) => About.Update();

        private void StartupUpdateCheck_Click(object sender, RoutedEventArgs e)
            => App.SetSetting(Services.ReleaseUpdateCheck.Setting,
                StartupUpdateCheck.IsChecked == true ? "1" : "0");

        /// <summary>
        /// "Clear all Data" footer link: wipes settings, downloaded OCR language packs, and temp
        /// files after an explicit confirmation. Destructive, so it always warns first; the user's
        /// PDFs are untouched.
        /// </summary>
        private void AboutClearData_Click(object sender, RoutedEventArgs e)
        {
            Window owner = _aboutHost ?? this;
            var res = KillerDialog.Show(owner,
                Loc("Str_ClearDataConfirm"),
                Loc("Str_ClearAllData"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (res != MessageBoxResult.Yes) return;

            App.ClearAllData();
            SetStatus(Loc("Str_St_DataCleared"));
            KillerDialog.Show(owner,
                Loc("Str_ClearDataDone"),
                Loc("Str_ClearAllData"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
