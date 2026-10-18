using System.Windows;
using System.Windows.Controls;

namespace KillerPDF;

internal sealed class DocumentValuesDialog : Window
{
    private readonly List<TextBox> _fields = [];
    internal string[] Values => _fields.Select(input => input.Text.Trim()).ToArray();

    internal DocumentValuesDialog(Window owner, string title, params (string Label, string Value)[] fields)
    {
        Title = "KillerPDF - " + title;
        Width = 540;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);
        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        foreach (var (label, value) in fields)
        {
            body.Children.Add(UiKit.GroupLabel(label));
            var input = UiKit.Field();
            input.Text = value;
            input.Margin = new Thickness(0, 5, 0, 12);
            System.Windows.Automation.AutomationProperties.SetName(input, label);
            _fields.Add(input);
            body.Children.Add(input);
        }
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        var apply = UiKit.Make(L("Str_Btn_Save"), accent: true);
        apply.IsDefault = true;
        apply.Click += (_, _) => { DialogResult = true; };
        body.Children.Add(UiKit.ButtonRow(cancel, apply));
        Content = DialogChrome.Frame(this, owner, Title, Close, body);
        Loaded += (_, _) => _fields.FirstOrDefault()?.Focus();
    }

    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
