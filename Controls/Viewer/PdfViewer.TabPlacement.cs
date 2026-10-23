using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    private static readonly object TabPlacementMenuTag = new();
    private static readonly Transform BottomTabTransform = (Transform)new ScaleTransform(1, -1).GetAsFrozen();
    private static readonly string[] TabPlacementMargins =
    [
        "TabMargin", "TabActiveMargin", "TabInactiveFirstMargin", "TabInactiveLastMargin",
        "TabActiveFirstMargin", "TabActiveLastMargin", "TabActiveOnlyMargin"
    ];

    internal bool TabsAtBottom { get; private set; }
    internal Button NewTabButtonCtl => NewTabBtn;

    internal void SetTabPlacement(bool bottom)
    {
        TabsAtBottom = bottom;
        Grid.SetRow(TabStripBorder, bottom ? 2 : 0);
        RebuildTabStrip();
    }

    private void NewTab_Click(object sender, RoutedEventArgs e) => Host?.NewDocumentTab(this);

    private void TabPlacementMenu_Opened(object sender, RoutedEventArgs e)
        => AddTabPlacementMenuItems((ContextMenu)sender);

    private void AddTabPlacementMenuItems(ContextMenu menu)
    {
        for (int index = menu.Items.Count - 1; index >= 0; index--)
            if (menu.Items[index] is FrameworkElement item && ReferenceEquals(item.Tag, TabPlacementMenuTag))
                menu.Items.RemoveAt(index);
        if (menu.Items.Count > 0) menu.Items.Add(new Separator { Tag = TabPlacementMenuTag });
        void AddChoice(bool bottom)
        {
            var item = MakeMenuItem(Loc(bottom ? "Str_Layout_TabsBottom" : "Str_Layout_TabsTop"),
                (_, _) => Host?.SetTabPlacement(bottom), GestureFor(bottom ? "AltShift:Down" : "AltShift:Up"),
                bottom ? "\uE70D" : "\uE70E");
            item.Tag = TabPlacementMenuTag;
            item.IsCheckable = true;
            item.IsChecked = TabsAtBottom == bottom;
            menu.Items.Add(item);
        }
        AddChoice(false);
        AddChoice(true);
    }

    private void ApplyTabPlacementChrome(bool show)
    {
        bool bottom = TabsAtBottom && show;
        Transform tabTransform = bottom ? BottomTabTransform : Transform.Identity;
        if (!ReferenceEquals(TabStripBorder.Resources["TabPlacementTransform"], tabTransform))
            TabStripBorder.Resources["TabPlacementTransform"] = tabTransform;
        CardRow.Margin = new Thickness(0, bottom ? 3 : show ? -1 : 3, 0, bottom ? -1 : 0);
        TabStripFade.RenderTransform = tabTransform;
        RetroTabJoinLine.VerticalAlignment = bottom ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        RetroTabInnerJoin.VerticalAlignment = bottom ? VerticalAlignment.Top : VerticalAlignment.Bottom;

        // 98SE keeps Win98's single light source when the strip moves below the page, as
        // KillerShell does: the page stays raised and each tab hangs from it with a white left
        // edge and a dark right edge and foot. Flipping the top-strip chrome would flip the light,
        // so RetroTabBorder draws the tabs' edges instead (the template hides their bevels), and
        // the page foot is two lines, black against the tabs (the inner join) over #808080 (the
        // foot shade), broken by the selected tab (PdfViewer.TabStrip.cs).
        bool retroBottom = bottom && Services.ThemeManager.Current == Services.Theme.SE98;
        if (retroBottom) TabStripBorder.Resources["TabRetroBottomEdges"] = true;
        else TabStripBorder.Resources.Remove("TabRetroBottomEdges");
        string joinBrush = retroBottom ? "BevelOuterDarkBrush" : "BevelLightBrush";
        RetroTabInnerJoinLeft.SetResourceReference(Border.BackgroundProperty, joinBrush);
        RetroTabInnerJoinRight.SetResourceReference(Border.BackgroundProperty, joinBrush);
        RetroTabFootShade.Visibility = retroBottom ? Visibility.Visible : Visibility.Collapsed;
        // The full-width gray perimeter belongs to a top strip; the foot lines replace it below.
        if (retroBottom) RetroTabJoinLine.Visibility = Visibility.Collapsed;
        else RetroTabJoinLine.SetResourceReference(VisibilityProperty, "RetroTabJoinVisibility");
        RetroTabInnerJoin.Margin = retroBottom ? new Thickness(0, -1, 0, 0)
            : bottom ? new Thickness(1, -1, 2, 0) : new Thickness(1, 0, 2, -1);
        foreach (string key in TabPlacementMargins)
        {
            if (bottom && Application.Current.TryFindResource(key) is Thickness margin)
            {
                var reversed = ReverseTabVertical(margin);
                if (!Equals(TabStripBorder.Resources[key], reversed)) TabStripBorder.Resources[key] = reversed;
            }
            else TabStripBorder.Resources.Remove(key);
        }
        if (!bottom) return;
        PaneBorder.BorderThickness = ReverseTabVertical(PaneBorder.BorderThickness);
        if (retroBottom)
        {
            // Raised page: the outer gray rule and the white and #dfdfdf highlights run along the
            // top as they do down the left; the shadow runs down the right only. The foot is the
            // join lines in the strip.
            PaneBevelOuterDark.BorderThickness = new Thickness(1, 1, 0, 0);
            PaneBevelInnerDark.BorderThickness = new Thickness(1, 1, 0, 0);
            PaneBevelOuterLight.BorderThickness = new Thickness(0, 0, 1, 0);
            PaneBevelInnerLight.BorderThickness = new Thickness(0);
            return;
        }
        foreach (var bevel in new[] { PaneBevelOuterDark, PaneBevelOuterLight, PaneBevelInnerDark, PaneBevelInnerLight })
            bevel.BorderThickness = ReverseTabVertical(bevel.BorderThickness);
    }

    private static Thickness ReverseTabVertical(Thickness value)
        => new(value.Left, value.Bottom, value.Right, value.Top);
}
