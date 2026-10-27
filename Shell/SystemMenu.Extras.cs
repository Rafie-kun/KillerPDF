using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace KillerPDF.Shell
{
    /// <summary>
    /// APP-OWNED: KillerPDF's side of the kit system menu (Shell/SystemMenu.cs). The menu takes the
    /// same style and text options as the app's other code-built menus, and carries the page
    /// navigation buttons toggle above Close.
    /// </summary>
    public partial class MainWindow
    {
        partial void CustomizeSystemMenu(ContextMenu menu)
        {
            if (TryFindResource(typeof(ContextMenu)) is Style style) menu.Style = style;
            TextOptions.SetTextFormattingMode(menu, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(menu, TextRenderingMode.Grayscale);
        }

        partial void AddSystemMenuItems(ContextMenu menu)
        {
            _pageNavigationMenuItem = new MenuItem { IsCheckable = true, Padding = new Thickness(12, 7, 24, 7) };
            _pageNavigationMenuItem.SetResourceReference(HeaderedItemsControl.HeaderProperty, "Str_NavigationButtons_Show");
            _pageNavigationMenuItem.Click += (_, _) => TogglePageNavigationButtons();
            menu.Items.Add(_pageNavigationMenuItem);
            menu.Items.Add(new Separator());
        }

        partial void SystemMenuOpening() => SyncPageNavigationButtons();
    }
}
