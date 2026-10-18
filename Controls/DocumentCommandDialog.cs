using System.Windows;
using System.Windows.Controls;

namespace KillerPDF;

internal sealed class DocumentCommandDialog : Window
{
    internal Action? Command { get; private set; }

    internal DocumentCommandDialog(Window owner, string title, params (string Label, Action Run)[] commands)
    {
        Title = "KillerPDF - " + title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);
        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };
        foreach (var (label, run) in commands)
        {
            var button = UiKit.Make(label, accent: false);
            button.Margin = new Thickness(0, 0, 0, 8);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Click += (_, _) => { Command = run; DialogResult = true; };
            body.Children.Add(button);
        }
        var close = UiKit.Make(L("Str_DocumentTools_Close"), accent: false);
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        body.Children.Add(UiKit.ButtonRow(close));
        Content = DialogChrome.Frame(this, owner, Title, Close, body);
        Loaded += (_, _) => (body.Children[0] as Control)?.Focus();
    }

    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
