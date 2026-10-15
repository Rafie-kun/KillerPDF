using System.Windows;
using System.Windows.Controls;

namespace KillerPDF;

// Scan to PDF (#73): pick the scanner and whether its own settings window opens first.
internal sealed class ScanDialog : Window
{
    private readonly ComboBox _scanners = new() { Margin = new Thickness(0, 3, 0, 10) };
    private readonly CheckBox _showSettings;

    internal bool Confirmed { get; private set; }
    internal int SelectedIndex => _scanners.SelectedIndex;
    internal bool ShowSettings => _showSettings.IsChecked == true;

    internal ScanDialog(Window owner, IReadOnlyList<string> scanners, string? lastScanner, bool showSettings)
    {
        Title = "KillerPDF - " + L("Str_Scan_Title");
        Width = 440;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        var hint = new TextBlock
        {
            Text = L("Str_Scan_Hint"),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        body.Children.Add(hint);

        body.Children.Add(UiKit.GroupLabel(L("Str_Scan_Scanner")));
        foreach (string scanner in scanners) _scanners.Items.Add(scanner);
        int last = lastScanner is null ? -1 : scanners.ToList().IndexOf(lastScanner);
        _scanners.SelectedIndex = last >= 0 ? last : 0;
        if (owner.TryFindResource("DarkComboBox") is Style comboStyle) _scanners.Style = comboStyle;
        System.Windows.Automation.AutomationProperties.SetName(_scanners, L("Str_Scan_Scanner"));
        body.Children.Add(_scanners);

        _showSettings = UiKit.CheckBox(L("Str_Scan_ShowSettings"));
        _showSettings.IsChecked = showSettings;
        body.Children.Add(_showSettings);

        var cancel = UiKit.Make(L("Str_Tf_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        var scan = UiKit.Make(L("Str_Scan_Start"), accent: true);
        scan.IsDefault = true;
        scan.Click += (_, _) => { Confirmed = true; Close(); };
        var buttons = UiKit.ButtonRow(cancel, scan);
        buttons.Margin = new Thickness(0, 14, 0, 0);
        body.Children.Add(buttons);

        Content = DialogChrome.Frame(this, owner, Title, Close, body);
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
