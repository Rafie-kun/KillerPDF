using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    private static readonly object TabPlacementMenuTag = new();
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
        CardRow.Margin = new Thickness(0, bottom ? 3 : show ? -1 : 3, 0, bottom ? -1 : 0);
        TabStripFade.RenderTransform = bottom ? new ScaleTransform(1, -1) : Transform.Identity;
        RetroTabJoinLine.VerticalAlignment = bottom ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        RetroTabInnerJoin.VerticalAlignment = bottom ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        RetroTabInnerJoin.Margin = bottom ? new Thickness(1, -1, 2, 0) : new Thickness(1, 0, 2, -1);
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
        foreach (var bevel in new[] { PaneBevelOuterDark, PaneBevelOuterLight, PaneBevelInnerDark, PaneBevelInnerLight })
            bevel.BorderThickness = ReverseTabVertical(bevel.BorderThickness);
    }

    private static Thickness ReverseTabVertical(Thickness value)
        => new(value.Left, value.Bottom, value.Right, value.Top);
}
