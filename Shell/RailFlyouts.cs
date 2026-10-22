using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace KillerPDF
{
    /// <summary>
    /// The rail's flyout buttons (family order, locked 2026-07-30: app-specific toggles, then
    /// ? / language / theme, theme bottom-most) and their flyouts. The theme and language
    /// pickers live here as
    /// family-standard flyouts: ContextMenus with FlyoutCard/FlyoutGrain chrome, opened against
    /// the content pane's rail-adjacent edge at the opening button's height so they never cover the rail,
    /// the footer, or the desktop. Their radio/dot sync is SyncPickerState (SettingsPanel.cs).
    /// </summary>
    public partial class MainWindow
    {
        // The shortcuts ? is the strip's ORIGINAL button (ShortcutHelp_Click) - it just moved
        // into the family slot above language; no second implementation was added.

        private void RailMenu_Opening(object sender, ContextMenuEventArgs e)
        {
            e.Handled = true;
            ToggleRailFlyout(sender == RailLangBtn ? LangFlyout : ThemeFlyout, atCursor: true);
        }

        private void RailLang_Click(object sender, RoutedEventArgs e) => ToggleRailFlyout(LangFlyout);

        private void RailTheme_Click(object sender, RoutedEventArgs e) => ToggleRailFlyout(ThemeFlyout);

        private void ToggleRailFlyout(ContextMenu menu, bool atCursor = false)
        {
            if (menu.IsOpen) { menu.IsOpen = false; return; }

            // The theme strip always faces the document. Mirroring the two columns also makes its
            // width animation grow outward from the rail on either side of the window.
            if (menu == ThemeFlyout)
                SyncThemeFlyoutSide();

            // Radios and accent dots reflect live state before the card shows - the same single
            // sync the Settings panel runs on open.
            SyncPickerState();

            // SplitHost is the full document region beside the rail. Its rail-adjacent edge is
            // whichever edge the sidebar is docked to. Using one viewer pane here stranded the flyouts on the left after
            // the sidebar moved right, and could place them in the middle of a split document.
            if (atCursor)
            {
                menu.CustomPopupPlacementCallback = null;
                menu.PlacementTarget = menu == LangFlyout ? RailLangBtn : RailThemeBtn;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                menu.ClearValue(ContextMenu.HorizontalOffsetProperty);
                menu.ClearValue(ContextMenu.VerticalOffsetProperty);
            }
            else
            {
                if (SplitHost is FrameworkElement pane)
                    FlyoutPlacement.UsePane(pane, _sidebarSide);
                FlyoutPlacement.AttachAtButtonHeight(menu, menu == LangFlyout ? RailLangBtn : RailThemeBtn);
            }

            menu.IsOpen = true;
            // 150ms ease-out, the family fade (the flyout template replaces the implicit
            // ContextMenu template, whose Loaded trigger normally drives this).
            menu.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150)))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                });
        }

        private void SyncThemeFlyoutSide()
        {
            if (ThemePickerLayout is null || ThemeSubmenu is null || AccentStripHost is null ||
                AccentStripDivider is null || AccentStrip is null)
                return;

            // A top or bottom rail has no side to face, so the strip keeps its left-rail place.
            bool right = _sidebarSide == SidebarSide.Right;
            Grid.SetColumn(ThemeSubmenu, right ? 1 : 0);
            Grid.SetColumn(AccentStripHost, right ? 0 : 1);
            AccentStripDivider.HorizontalAlignment = right
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Left;
            AccentStrip.Margin = right
                ? new Thickness(2, 6, 7, 6)
                : new Thickness(7, 6, 2, 6);
            // The theme column is the star one on either side, so its hover rows fill the menu.
            // ThemeRadio rows keep their -8,0,2,0 on both sides, so the list's left inset moves
            // onto the list itself on a right rail: 2 + 10 - 8 = 4 left, 2 + 2 = 4 right, and the
            // strip keeps the same 2 + 2 air against the left edge that it has on the right.
            ThemePickerLayout.ColumnDefinitions[0].Width = right ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            ThemePickerLayout.ColumnDefinitions[1].Width = right ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            ThemeSubmenu.Margin = right ? new Thickness(10, 0, 0, 0) : new Thickness(0);
            ThemePickerLayout.Margin = right
                ? new Thickness(2, 10, 2, 10)
                : new Thickness(12, 10, 2, 10);
        }
    }
}
