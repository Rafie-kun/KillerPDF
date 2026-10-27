using System.Windows;
using System.Windows.Controls;

namespace KillerPDF;

// Reduce File Size (#433). The lossless cleanup always runs: unused and duplicate objects go,
// streams are compressed and packed. Downsampling images is the optional, lossy part.
internal sealed class ReduceSizeDialog : Window
{
    private readonly CheckBox _downsample;
    private readonly TextBox _dpi;
    private readonly Slider _quality;
    private readonly TextBlock _qualityValue;

    internal bool Confirmed { get; private set; }
    internal bool Downsample { get; private set; }
    internal double Dpi { get; private set; } = 150;
    internal int Quality { get; private set; } = 80;

    internal ReduceSizeDialog(Window owner)
    {
        Title = "KillerPDF - " + L("Str_Reduce_Title");
        Width = 420;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);

        Style? sliderStyle = owner.TryFindResource("DarkSlider") as Style;
        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };

        var hint = new TextBlock
        {
            Text = L("Str_Reduce_Hint"),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        body.Children.Add(hint);

        _downsample = UiKit.CheckBox(L("Str_Reduce_Downsample"));
        _downsample.IsChecked = true;
        _downsample.Margin = new Thickness(0, 0, 0, 8);
        body.Children.Add(_downsample);

        body.Children.Add(UiKit.GroupLabel(L("Str_ExportImg_Dpi")));
        _dpi = UiKit.Field();
        _dpi.Text = "150";
        _dpi.Margin = new Thickness(0, 3, 0, 10);
        body.Children.Add(_dpi);

        body.Children.Add(UiKit.GroupLabel(L("Str_Reduce_Quality")));
        _quality = new Slider
        {
            Minimum = 25,
            Maximum = 100,
            Value = 80,
            TickFrequency = 5,
            SmallChange = 1,
            LargeChange = 5,
            Margin = new Thickness(0, 2, 0, 2),
        };
        if (sliderStyle is not null) _quality.Style = sliderStyle;
        _qualityValue = new TextBlock
        {
            Text = "80%",
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = UiKit.Brush("MutedTextBrush"),
            FontFamily = UiKit.MonoFont,
            FontSize = 10,
        };
        _quality.ValueChanged += (_, e) => _qualityValue.Text = $"{e.NewValue:0}%";
        body.Children.Add(_quality);
        body.Children.Add(_qualityValue);

        void UpdateEnabled()
        {
            bool on = _downsample.IsChecked == true;
            _dpi.IsEnabled = on;
            _quality.IsEnabled = on;
        }
        _downsample.Checked += (_, _) => UpdateEnabled();
        _downsample.Unchecked += (_, _) => UpdateEnabled();

        var cancel = UiKit.Make(L("Str_Tf_Cancel"), accent: false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        var save = UiKit.Make(L("Str_Btn_Save"), accent: true);
        save.IsDefault = true;
        save.Click += (_, _) => Commit();
        var buttons = UiKit.ButtonRow(cancel, save);
        buttons.Margin = new Thickness(0, 12, 0, 0);
        body.Children.Add(buttons);

        Content = DialogFrame.Frame(this, owner, Title, Close, body);
    }

    private void Commit()
    {
        Downsample = _downsample.IsChecked == true;
        Dpi = double.TryParse(_dpi.Text.Trim(), out double parsed) && parsed >= 36 && parsed <= 1200 ? parsed : 150;
        Quality = (int)Math.Round(_quality.Value);
        Confirmed = true;
        Close();
    }

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
