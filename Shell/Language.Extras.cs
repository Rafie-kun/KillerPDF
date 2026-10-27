namespace KillerPDF.Shell
{
    /// <summary>
    /// APP-OWNED: what the kit's language switch (Shell/Language.cs) needs from KillerPDF. The
    /// toolbar rebuild after a language change is the kit's (ToolbarStyle.cs), and the shortcuts
    /// card reopens in the view it was showing.
    /// KillerPDF's remaining code-built text is rebuilt in RelocalizeAppUi (SettingsPanel.cs).
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>True while the shortcuts card shows the keyboard view (KeyboardMapOverlay.cs).</summary>
        private bool _ksMapView => App.GetSetting(KsViewSetting) == "keyboard";
    }
}
