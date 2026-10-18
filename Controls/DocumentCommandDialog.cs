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
        MaxHeight = Math.Max(300, SystemParameters.WorkArea.Height - 80);
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogChrome.Configure(this, owner);
        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        var choices = new StackPanel();
        foreach (var (label, run) in commands)
        {
            var button = UiKit.Make(label, accent: false);
            button.Margin = new Thickness(0, 0, 0, 8);
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.Click += (_, _) => { Command = run; DialogResult = true; };
            choices.Children.Add(button);
        }
        var close = UiKit.Make(L("Str_DocumentTools_Close"), accent: false);
        close.IsCancel = true;
        close.Click += (_, _) => Close();
        var footer = UiKit.ButtonRow(close);
        footer.Margin = new Thickness(0, 8, 0, 0);
        DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        body.Children.Add(new ScrollViewer { Content = choices, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = DialogChrome.Frame(this, owner, Title, Close, body);
        Loaded += (_, _) => (choices.Children.Cast<Control>().FirstOrDefault() ?? close).Focus();
    }

    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
