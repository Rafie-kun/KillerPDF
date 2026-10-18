using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KillerPDF
{
    /// <summary>
    /// Rail flyouts stay inside the content pane beside the rail and follow their opening button.
    /// (From KillerUI/Shell/FlyoutPlacement.cs - the family flyout standard.)
    ///
    /// That rail-adjacent corner is the answer because of what bounds it, and all three matter:
    ///   - it is INSIDE the window, so a flyout never hangs over the desktop;
    ///   - it is ABOVE the footer, so the status bar is never covered;
    ///   - it is clear of the icon rail, so the rail buttons are never covered.
    /// The content pane is the one element bounded by all three at once, so flyouts are positioned
    /// against it horizontally, with the opening button supplying the vertical center. A top or
    /// bottom rail turns that around: the flyout sits against the pane's top or bottom edge and
    /// the button supplies the horizontal center.
    ///
    /// WHY NOT PlacementMode.Right / Top / etc: a Popup is its own top-level window, and WPF's
    /// built-in modes only ever avoid the SCREEN edge. They do not know the app window exists, let
    /// alone the footer or the rail. "Right of the button" opened flyouts over the desktop when the
    /// rail sat near the window's right edge; "Top" opened them over the status bar. Hours went
    /// into re-tuning offsets before it was clear no built-in mode can express the requirement.
    /// The requirement: do not obscure the icons, do not obscure the status bar, and put the
    /// flyout eight units inside the rail-adjacent edge of the content pane.
    ///
    /// WIRING (once, before the flyouts open):
    ///     FlyoutPlacement.UsePane(pane, sidebarSide);  // the element the document content sits on
    /// then, each time a flyout opens:
    ///     FlyoutPlacement.AttachAtButtonHeight(themeMenu, themeButton);
    ///     themeMenu.IsOpen = true;
    ///
    /// The visible card sits eight units inside the pane edge. Its transparent shadow
    /// margin is measured from FlyoutCard and compensated separately.
    /// </summary>
    internal static class FlyoutPlacement
    {
        /// <summary>The content pane. Set once; every flyout positions against it.</summary>
        private static FrameworkElement? _pane;
        private static SidebarSide _side;

        internal static void UsePane(FrameworkElement pane, SidebarSide side)
        {
            _pane = pane;
            _side = side;
        }

        // A flyout without a button to follow takes the pane's bottom corner on the rail's side.
        // A top or bottom rail keeps its buttons at its left end unless moved, so that is the left.
        private static bool AlignRight => _side == SidebarSide.Right;

        internal static void Attach(Popup popup, UIElement _)
        {
            popup.PlacementTarget = _pane;
            popup.Placement = PlacementMode.Custom;
            popup.CustomPopupPlacementCallback =
                (popupSize, targetSize, __) => PaneCorner(popupSize, targetSize, AlignRight);
        }

        internal static void Attach(ContextMenu menu, UIElement _)
        {
            menu.PlacementTarget = _pane;
            menu.Placement = PlacementMode.Custom;
            // The shared ContextMenu style compensates ordinary pointer-anchored menus for its
            // enlarged shadow halo. Rail flyouts compensate that halo explicitly.
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.CustomPopupPlacementCallback =
                (popupSize, targetSize, __) => PaneCorner(popupSize, targetSize, AlignRight);
        }

        internal static void AttachAtButtonHeight(ContextMenu menu, FrameworkElement button)
        {
            Attach(menu, button);
            menu.PlacementTarget = button;
            var pane = _pane;
            var side = _side;
            bool alignRight = AlignRight;
            menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
            {
                var placement = PaneCorner(popupSize, targetSize, alignRight);
                if (pane is null || PresentationSource.FromVisual(button)?.CompositionTarget is not { } target)
                    return placement;

                var halo = pane.TryFindResource("FlyoutCard") is Style style
                    ? new Border { Style = style }.Margin : new Thickness(22, 18, 22, 26);
                var paneOrigin = pane.PointToScreen(new Point());
                var buttonOrigin = button.PointToScreen(new Point());
                var paneExtent = pane.PointToScreen(new Point(pane.ActualWidth, pane.ActualHeight));
                var paneSize = target.TransformFromDevice.Transform(paneExtent - paneOrigin);
                if (side.IsStrip())
                {
                    var buttonCenter = button.PointToScreen(new Point(button.ActualWidth / 2, 0));
                    double centerX = target.TransformFromDevice.Transform(buttonCenter - paneOrigin).X;
                    placement = AtButtonColumn(popupSize, new Size(paneSize.X, paneSize.Y),
                        side == SidebarSide.Bottom, centerX, halo);
                }
                else
                {
                    var buttonCenter = button.PointToScreen(new Point(0, button.ActualHeight / 2));
                    double centerY = target.TransformFromDevice.Transform(buttonCenter - paneOrigin).Y;
                    placement = AtButtonHeight(popupSize, new Size(paneSize.X, paneSize.Y), alignRight, centerY, halo);
                }
                // WPF offsets are relative to the clicked button. Convert both visual origins
                // through the same device transform, including display DPI and app scaling.
                var origin = target.TransformFromDevice.Transform(paneOrigin - buttonOrigin);
                placement[0] = new CustomPopupPlacement(placement[0].Point + origin, PopupPrimaryAxis.None);
                return placement;
            };
        }

        internal static CustomPopupPlacement[] AtButtonHeight(
            Size popupSize, Size targetSize, bool alignRight, double centerY, Thickness halo)
        {
            const double inset = 8;
            double cardHeight = popupSize.Height - halo.Top - halo.Bottom;
            double cardTop = System.Math.Max(inset,
                System.Math.Min(centerY - cardHeight / 2, targetSize.Height - cardHeight - inset));
            double x = alignRight
                ? targetSize.Width - popupSize.Width + halo.Right - inset : inset - halo.Left;
            return [new CustomPopupPlacement(new Point(x, cardTop - halo.Top), PopupPrimaryAxis.None)];
        }

        /// <summary>
        /// A top or bottom rail's flyout: the visible card sits eight units inside the pane's
        /// rail-adjacent edge (its bottom for a bottom rail, its top for a top rail), centered
        /// under or over the button and kept eight units inside the pane's left and right edges.
        /// </summary>
        internal static CustomPopupPlacement[] AtButtonColumn(
            Size popupSize, Size targetSize, bool alignBottom, double centerX, Thickness halo)
        {
            const double inset = 8;
            double cardWidth = popupSize.Width - halo.Left - halo.Right;
            double cardLeft = System.Math.Max(inset,
                System.Math.Min(centerX - cardWidth / 2, targetSize.Width - cardWidth - inset));
            double y = alignBottom
                ? targetSize.Height - popupSize.Height + halo.Bottom - inset
                : inset - halo.Top;
            // A card taller than the pane would otherwise start above it and run over the toolbar.
            if (alignBottom && y < 0) y = 0;
            return [new CustomPopupPlacement(new Point(cardLeft - halo.Left, y), PopupPrimaryAxis.None)];
        }

        /// <summary>
        /// Coordinates are relative to the pane's top-left. The horizontal coordinate mirrors
        /// between pane edges, while y puts the flyout's bottom above the footer.
        /// </summary>
        internal static CustomPopupPlacement[] PaneCorner(
            Size popupSize, Size targetSize, bool alignRight)
        {
            var halo = _pane?.TryFindResource("FlyoutCard") is Style style
                ? new Border { Style = style }.Margin : new Thickness(22, 18, 22, 26);
            const double inset = 8;
            double x = alignRight
                ? targetSize.Width - popupSize.Width + halo.Right - inset
                : inset - halo.Left;
            double y = targetSize.Height - popupSize.Height + halo.Bottom - inset;

            // A flyout taller than the pane would otherwise start above it and run over the
            // toolbar; pin it to the pane's top instead and let it use the height it has.
            if (y < 0) y = 0;

            return [new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None)];
        }
    }
}
