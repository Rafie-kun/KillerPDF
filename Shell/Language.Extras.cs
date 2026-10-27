namespace KillerPDF.Shell
{
    /// <summary>
    /// APP-OWNED: what the kit's language switch (Shell/Language.cs) needs from KillerPDF. The
    /// toolbar keeps KillerPDF's own appearance engine (SettingsPanel.cs), so its rebuild after a
    /// language change lives here, and the shortcuts card reopens in the view it was showing.
    /// KillerPDF's remaining code-built text is rebuilt in RelocalizeAppUi (SettingsPanel.cs).
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>True while the shortcuts card shows the keyboard view (KeyboardMapOverlay.cs).</summary>
        private bool _ksMapView => App.GetSetting(KsViewSetting) == "keyboard";

        /// <summary>Rebuilds the toolbar captions and its right-click menu in the new language.</summary>
        private void RelocalizeToolbar()
        {
            BuildToolbarMenu();
            ApplyToolbarAppearance();
        }
    }
}
