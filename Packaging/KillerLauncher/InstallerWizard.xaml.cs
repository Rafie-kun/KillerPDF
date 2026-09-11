using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;

namespace KillerLauncher
{
    public partial class InstallerWizard : Window
    {
        private int _page;
        private bool _installed;
        private bool _closeAfterNotice;
        private string _installedDirectory = string.Empty;

        private InstallerWizard()
        {
            InitializeComponent();
            InstallFolder.Text = Program.DefaultInstallDirectory(false);
            ImageBrush grain = CreateGrain();
            GrainLayer.Background = grain;
            SidebarGrain.Background = grain;
            FrameGrain.Background = grain;
            RenderPage();
        }

        internal static int Run(string[] args)
        {
            var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var wizard = new InstallerWizard();
            bool? result = wizard.ShowDialog();
            application.Shutdown();
            return result == true ? 0 : 1;
        }

        internal static int ShowFailure(string message)
        {
            var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var wizard = new InstallerWizard { _closeAfterNotice = true };
            wizard.Loaded += (_, _) => wizard.ShowNotice(
                "Installation could not continue", message, NoticeKind.Error);
            wizard.ShowDialog();
            application.Shutdown();
            return 1;
        }

        private void RenderPage()
        {
            bool options = _page == 1;
            Options.Visibility = options ? Visibility.Visible : Visibility.Collapsed;
            RuntimeStatus.Visibility = options ? Visibility.Visible : Visibility.Collapsed;
            BackButton.IsEnabled = _page > 0 && !_installed;
            CancelButton.Visibility = _installed ? Visibility.Collapsed : Visibility.Visible;
            if (_page == 0)
            {
                Heading.Text = "Welcome to KillerPDF!";
                Copy.Text = "Fast, private PDF editing is only a few clicks away.\nLet's get you set up.";
                NextButton.Content = "Next";
            }
            else if (options)
            {
                Heading.Text = "Make it yours";
                Copy.Text = "Choose where KillerPDF lives and who gets to use it.";
                SetRuntimeStatus();
                NextButton.Content = "Next";
            }
            else
            {
                Heading.Text = _installed ? "You're all set!" : "Ready to go!";
                Copy.Text = _installed ? "KillerPDF is installed and ready to make PDFs less painful." :
                    (AllUsers.IsChecked == true ? "All users" : "Current user") +
                    (DesktopShortcut.IsChecked == true ? "  •  Desktop shortcut" : "  •  No desktop shortcut");
                SetRuntimeStatus();
                NextButton.Content = _installed ? "Launch" : "Install";
            }
        }

        private void SetRuntimeStatus()
        {
            bool ready = Program.HasDesktopRuntime10();
            RuntimeStatus.Text = ready ? ".NET 10 Desktop Runtime detected" : ".NET 10 Desktop Runtime required";
            RuntimeStatus.Foreground = new SolidColorBrush(ready
                ? Color.FromRgb(30, 165, 76) : Color.FromRgb(255, 190, 80));
        }

        private async void Next_Click(object sender, RoutedEventArgs e)
        {
            if (_installed)
            {
                Process.Start(new ProcessStartInfo(Program.InstalledExecutable(_installedDirectory)) { UseShellExecute = true });
                DialogResult = true;
                return;
            }
            if (_page < 2) { _page++; RenderPage(); return; }
            string installDirectory;
            try { installDirectory = Program.ValidateInstallDirectory(InstallFolder.Text); }
            catch (Exception ex)
            {
                _page = 1;
                RenderPage();
                InstallFolder.Focus();
                InstallFolder.SelectAll();
                ShowNotice("Check installation options", ex.Message, NoticeKind.Warning);
                return;
            }
            if (!Program.HasDesktopRuntime10())
            {
                Process.Start(new ProcessStartInfo("https://dotnet.microsoft.com/en-us/download/dotnet/10.0") { UseShellExecute = true });
                ShowNotice(".NET 10 Desktop Runtime required",
                    "Install the .NET 10 Desktop Runtime, then return to setup.", NoticeKind.Information);
                return;
            }
            try
            {
                NextButton.IsEnabled = BackButton.IsEnabled = CancelButton.IsEnabled = false;
                Heading.Text = "Installing KillerPDF";
                Copy.Text = "Almost there. We're putting everything in place...";
                RuntimeStatus.Visibility = Visibility.Collapsed;
                InstallProgress.Visibility = Visibility.Visible;
                bool machine = AllUsers.IsChecked == true;
                bool desktop = DesktopShortcut.IsChecked == true;
                Task<int> install = Task.Run(() => machine ? InstallForEveryone(desktop, installDirectory)
                    : Program.Install(false, desktop, installDirectory));
                await Task.WhenAll(install, Task.Delay(2000));
                int result = install.Result;
                if (result != 0) throw new InvalidOperationException("Setup returned " + result + ".");
                _installedDirectory = installDirectory;
                _installed = true;
                NextButton.IsEnabled = true;
                InstallProgress.Visibility = Visibility.Collapsed;
                RenderPage();
            }
            catch (Exception ex)
            {
                NextButton.IsEnabled = BackButton.IsEnabled = CancelButton.IsEnabled = true;
                InstallProgress.Visibility = Visibility.Collapsed;
                RenderPage();
                ShowNotice("Installation failed", ex.Message, NoticeKind.Error);
            }
        }

        private enum NoticeKind { Information, Warning, Error }

        private void ShowNotice(string heading, string message, NoticeKind kind)
        {
            NoticeHeading.Text = heading;
            NoticeMessage.Text = message;
            NoticeGlyph.Text = kind == NoticeKind.Error ? "×" : kind == NoticeKind.Warning ? "!" : "i";
            NoticeGlyph.Foreground = new SolidColorBrush(kind == NoticeKind.Error
                ? Color.FromRgb(227, 93, 106)
                : kind == NoticeKind.Warning ? Color.FromRgb(255, 190, 80) : Color.FromRgb(30, 165, 76));
            NoticeRing.BorderBrush = NoticeGlyph.Foreground;
            NoticeOverlay.Visibility = Visibility.Visible;
            NoticeOk.Focus();
        }

        private void NoticeOk_Click(object sender, RoutedEventArgs e)
        {
            if (_closeAfterNotice) { DialogResult = false; return; }
            NoticeOverlay.Visibility = Visibility.Collapsed;
        }

        private static int InstallForEveryone(bool desktop, string installDirectory)
        {
            string arguments = "/silent " + (desktop ? "/desktop " : string.Empty) +
                Program.EncodeInstallDirectoryArgument(installDirectory);
            var start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
                arguments) { UseShellExecute = true, Verb = "runas" };
            using (Process elevated = Process.Start(start))
            {
                if (elevated == null) return 1;
                elevated.WaitForExit();
                return elevated.ExitCode;
            }
        }

        private void Scope_Checked(object sender, RoutedEventArgs e)
        {
            if (InstallFolder == null) return;
            string user = Program.DefaultInstallDirectory(false);
            string machine = Program.DefaultInstallDirectory(true);
            if (string.IsNullOrWhiteSpace(InstallFolder.Text) ||
                string.Equals(InstallFolder.Text, user, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(InstallFolder.Text, machine, StringComparison.OrdinalIgnoreCase))
                InstallFolder.Text = Program.DefaultInstallDirectory(AllUsers.IsChecked == true);
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Choose where KillerPDF will be installed",
                ShowNewFolderButton = true,
                SelectedPath = Directory.Exists(InstallFolder.Text) ? InstallFolder.Text :
                    (Directory.Exists(Path.GetDirectoryName(InstallFolder.Text)) ? Path.GetDirectoryName(InstallFolder.Text) : string.Empty)
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                InstallFolder.Text = dialog.SelectedPath;
        }

        private void Back_Click(object sender, RoutedEventArgs e) { if (_page > 0) { _page--; RenderPage(); } }
        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); }
        private void Website_Click(object sender, RoutedEventArgs e) =>
            Process.Start(new ProcessStartInfo("https://thekiller.net") { UseShellExecute = true });

        private static ImageBrush CreateGrain()
        {
            const int size = 128;
            var pixels = new byte[size * size * 4];
            var random = new Random(1979);
            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte value = (byte)random.Next(82, 174);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = value;
                pixels[i + 3] = (byte)random.Next(34, 92);
            }
            var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
            bitmap.Freeze();
            return new ImageBrush(bitmap) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, size, size), Stretch = Stretch.None };
        }
    }
}
