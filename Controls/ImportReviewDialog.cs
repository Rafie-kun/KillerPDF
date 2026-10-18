using System.Windows;
using System.Windows.Controls;

namespace KillerPDF;

internal sealed class ImportReviewDialog : Window
{
    private readonly CheckBox _option;
    internal bool Option => _option.IsChecked == true;

    internal ImportReviewDialog(Window owner, string title, string report, string? optionLabel,
        string? actionLabel = null)
    {
        Title = "KillerPDF - " + title;
        Width = 700;
        Height = 520;
        MinWidth = 460;
        MinHeight = 320;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);
        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        var apply = UiKit.Make(actionLabel ?? L("Str_Exchange_Import"), accent: true);
        apply.IsDefault = true;
        apply.Click += (_, _) => { DialogResult = true; };
        var buttons = UiKit.ButtonRow(cancel, apply);
        buttons.Margin = new Thickness(0, 14, 0, 0);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(buttons);
        _option = UiKit.CheckBox(optionLabel ?? "");
        if (optionLabel is null) _option.Visibility = Visibility.Collapsed;
        _option.Margin = new Thickness(0, 12, 0, 0);
        DockPanel.SetDock(_option, Dock.Bottom);
        body.Children.Add(_option);
        var text = UiKit.Field();
        text.Text = report;
        System.Windows.Automation.AutomationProperties.SetName(text, title);
        text.IsReadOnly = true;
        text.AcceptsReturn = true;
        text.TextWrapping = TextWrapping.Wrap;
        text.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        body.Children.Add(text);
        Content = DialogChrome.Frame(this, owner, Title, Close, body);
        Loaded += (_, _) => text.Focus();
    }

    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
