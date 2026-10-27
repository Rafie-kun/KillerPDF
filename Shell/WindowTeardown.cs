using System.Windows;

namespace KillerPDF.Shell;

public partial class MainWindow
{
    private bool _windowResourcesReleased;

    private void MainWindow_Closed(object? sender, EventArgs args)
    {
        if (_windowResourcesReleased) return;
        _windowResourcesReleased = true;
        Services.ThemeManager.ThemeChanged -= OnThemeChanged;
        App.RememberClosedWindow(this);
        if (Application.Current.MainWindow is null || ReferenceEquals(Application.Current.MainWindow, this))
            Application.Current.MainWindow = App.PrimaryWindow(this);
        _busyCts?.Cancel();
        Viewer.ReleaseWindowResourcesExt();
        ViewerB.ReleaseWindowResourcesExt();
    }
}
