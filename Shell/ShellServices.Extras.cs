namespace KillerPDF.Shell
{
    /// <summary>
    /// APP-OWNED: KillerPDF's side of the kit's IShellServices (Shell/ShellServices.cs). A feature's
    /// status line goes through KillerPDF's own SetStatus, so a held message (the app-size readout
    /// while the logo wheel turns) is not replaced and the crash reporter records the line.
    /// </summary>
    public partial class MainWindow
    {
        partial void SetStatusText(string text, ref bool handled)
        {
            SetStatus(text);
            handled = true;
        }
    }
}
