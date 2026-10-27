using System.Windows;
using System.Windows.Controls;

/// <summary>
/// The theme rows of the kit's theme menu (Controls/ThemePicker.cs) are built in code each time it
/// opens, so they have no XAML names. The smokes still say "ThemeLightRadio" and so on; this finds
/// the open menu's row for that theme by its Tag.
/// </summary>
internal static class ThemeMenuRadios
{
    internal static RadioButton Find(Window owner, string radioName)
    {
        string theme = radioName switch
        {
            "Theme98SERadio" => "SE98",
            "ThemeHCRadio" => "Black",
            _ => radioName[5..^5],
        };
        var menu = (ContextMenu)owner.FindName("ThemeMenu");
        foreach (object item in menu.Items)
            if (item is DependencyObject root && Search(root, theme) is RadioButton radio) return radio;
        throw new InvalidOperationException("The theme menu has no row for " + theme + ".");
    }

    private static RadioButton? Search(DependencyObject node, string theme)
    {
        if (node is RadioButton { Tag: { } tag } radio && tag.ToString() == theme) return radio;
        foreach (object child in LogicalTreeHelper.GetChildren(node))
            if (child is DependencyObject element && Search(element, theme) is RadioButton found) return found;
        return null;
    }
}
