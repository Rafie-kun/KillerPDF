using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using KillerPDF.Services;
using KillerPdf.Engine.Documents;

namespace KillerPDF;

// Set Scale: the drawing scale the Measure tool uses on a page, typed as a ratio ("1 in = 10 ft")
// or calibrated from a line drawn along a length the user knows. The result is saved in the PDF as
// the page's viewport scale, so other viewers measure the page the same way.
internal sealed class MeasureScaleDialog : Window
{
    /// <summary>What the dialog keeps when it closes to let the user draw a calibration line.</summary>
    internal sealed record DialogState(bool Calibrate, string PaperLength, string PaperUnit, string RealLength,
        string RealUnit, int Precision, bool AllPages, bool UpdateExisting);

    private readonly RadioButton _ratio;
    private readonly RadioButton _calibrate;
    private readonly TextBox _paperLength;
    private readonly ComboBox _paperUnit;
    private readonly TextBox _realLength;
    private readonly ComboBox _realUnit;
    private readonly TextBox _knownLength;
    private readonly ComboBox _knownUnit;
    private readonly TextBlock _lineText;
    private readonly ComboBox _precision;
    private readonly CheckBox _allPages;
    private readonly CheckBox? _updateExisting;
    private readonly TextBlock _problem;
    private readonly double? _linePoints;
    private readonly int _measurementsHere;
    private readonly int _measurementsEverywhere;

    /// <summary>The chosen scale, or null for actual size.</summary>
    internal PdfMeasurementProfile? Profile { get; private set; }
    internal bool AllPages => _allPages.IsChecked == true;
    internal bool UpdateExisting => _updateExisting?.IsChecked == true;
    /// <summary>True when the user asked to draw a calibration line; reopen with its length.</summary>
    internal bool RequestCalibration { get; private set; }
    internal DialogState State => new(_calibrate.IsChecked == true, _paperLength.Text, UnitOf(_paperUnit),
        _calibrate.IsChecked == true ? _knownLength.Text : _realLength.Text,
        UnitOf(_calibrate.IsChecked == true ? _knownUnit : _realUnit),
        _precision.SelectedIndex < 0 ? 2 : _precision.SelectedIndex, AllPages, _updateExisting?.IsChecked ?? true);

    internal MeasureScaleDialog(Window owner, PdfMeasurementProfile? current, string unit, double? linePoints,
        DialogState? previous, int measurementsHere, int measurementsEverywhere)
    {
        _linePoints = linePoints;
        _measurementsHere = measurementsHere;
        _measurementsEverywhere = measurementsEverywhere;
        Title = "KillerPDF - " + L("Str_MeasureScale_Title");
        Width = 520;
        SizeToContent = SizeToContent.Height;
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);
        CultureInfo culture = CultureInfo.CurrentCulture;

        // Start from what the page has: a typed ratio reads back from the scale name when it can,
        // otherwise the scale shows as one paper inch (or centimeter) to its real length.
        string realUnit = current is not null && MeasurementUnits.CanConvert(current.UnitSymbol) ? current.UnitSymbol : unit;
        bool metric = MeasurementUnits.Find(realUnit).Metric;
        string paperUnit = metric ? "cm" : "in";
        double realPerPaper = current is not null && MeasurementUnits.CanConvert(current.UnitSymbol)
            ? MeasurementUnits.ConvertScale(current.UnitsPerPoint, current.UnitSymbol, realUnit) * 72 * MeasurementUnits.Find(paperUnit).Inches
            : MeasurementUnits.Find(paperUnit).Inches / MeasurementUnits.Find(realUnit).Inches;
        previous ??= new DialogState(linePoints is not null, "1", paperUnit,
            Number(realPerPaper, culture), realUnit, current?.Precision ?? MeasurementUnits.DefaultPrecision(realUnit),
            false, true);

        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 16) };

        _ratio = UiKit.Radio(L("Str_MeasureScale_Ratio"));
        _ratio.GroupName = "MeasureScaleMode";
        _ratio.Margin = new Thickness(0, 2, 0, 6);
        body.Children.Add(_ratio);
        var ratioRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 0, 0, 12) };
        _paperLength = LengthField(previous.PaperLength);
        _paperUnit = UnitCombo(previous.PaperUnit);
        _realLength = LengthField(previous.Calibrate ? Number(realPerPaper, culture) : previous.RealLength);
        _realUnit = UnitCombo(previous.Calibrate ? realUnit : previous.RealUnit);
        ratioRow.Children.Add(_paperLength);
        ratioRow.Children.Add(_paperUnit);
        ratioRow.Children.Add(new TextBlock
        {
            Text = "=",
            FontFamily = UiKit.UiFont,
            FontSize = 13,
            Foreground = UiKit.Brush("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 10, 0)
        });
        ratioRow.Children.Add(_realLength);
        ratioRow.Children.Add(_realUnit);
        body.Children.Add(ratioRow);

        _calibrate = UiKit.Radio(L("Str_MeasureScale_Calibrate"));
        _calibrate.GroupName = "MeasureScaleMode";
        _calibrate.Margin = new Thickness(0, 2, 0, 6);
        body.Children.Add(_calibrate);
        var lineRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 0, 0, 6) };
        _lineText = new TextBlock
        {
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Foreground = UiKit.Brush("MutedTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 250,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        lineRow.Children.Add(_lineText);
        var drawLine = UiKit.Make(L("Str_MeasureScale_DrawLine"), false);
        drawLine.Padding = new Thickness(10, 4, 10, 4);
        drawLine.Click += (_, _) => { RequestCalibration = true; DialogResult = false; Close(); };
        lineRow.Children.Add(drawLine);
        body.Children.Add(lineRow);
        var knownRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 0, 0, 12) };
        knownRow.Children.Add(new TextBlock
        {
            Text = L("Str_MeasureScale_RealLength"),
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Foreground = UiKit.Brush("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        _knownLength = LengthField(previous.Calibrate ? previous.RealLength : "");
        _knownUnit = UnitCombo(previous.RealUnit);
        _knownUnit.SelectionChanged += (_, _) => UpdateLineText();
        knownRow.Children.Add(_knownLength);
        knownRow.Children.Add(_knownUnit);
        body.Children.Add(knownRow);

        var precisionRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        precisionRow.Children.Add(new TextBlock
        {
            Text = L("Str_MeasureScale_Precision"),
            FontFamily = UiKit.UiFont,
            FontSize = 12,
            Foreground = UiKit.Brush("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        _precision = new ComboBox { Width = 64, VerticalAlignment = VerticalAlignment.Center };
        if (UiKit.DarkComboStyle is Style precisionStyle) _precision.Style = precisionStyle;
        for (int digits = 0; digits <= 4; digits++) _precision.Items.Add(digits.ToString(culture));
        _precision.SelectedIndex = Math.Clamp(previous.Precision, 0, 4);
        precisionRow.Children.Add(_precision);
        body.Children.Add(precisionRow);

        _allPages = UiKit.CheckBox(L("Str_MeasureScale_AllPages"));
        _allPages.IsChecked = previous.AllPages;
        _allPages.Margin = new Thickness(0, 0, 0, 6);
        body.Children.Add(_allPages);
        if (measurementsEverywhere > 0)
        {
            _updateExisting = UiKit.CheckBox("");
            _updateExisting.IsChecked = previous.UpdateExisting;
            _updateExisting.Margin = new Thickness(0, 0, 0, 6);
            body.Children.Add(_updateExisting);
            _allPages.Checked += (_, _) => UpdateExistingLabel();
            _allPages.Unchecked += (_, _) => UpdateExistingLabel();
            UpdateExistingLabel();
        }

        _problem = new TextBlock
        {
            Text = L("Str_MeasureScale_Invalid"),
            Foreground = UiKit.Brush("MutedTextBrush"),
            FontFamily = UiKit.UiFont,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 4, 0, 0)
        };
        body.Children.Add(_problem);
        body.Children.Add(new TextBlock
        {
            Text = L("Str_MeasureScale_Hint"),
            Foreground = UiKit.Brush("MutedTextBrush"),
            FontFamily = UiKit.UiFont,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 12)
        });

        var actualSize = UiKit.Make(L("Str_MeasureScale_Reset"), false);
        actualSize.Click += (_, _) => { Profile = null; DialogResult = true; Close(); };
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), false);
        cancel.IsCancel = true;
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = UiKit.Make(L("Str_Btn_OK"), true);
        ok.IsDefault = true;
        ok.Click += (_, _) => Commit();
        var buttons = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(actualSize, Dock.Left);
        buttons.Children.Add(actualSize);
        var right = UiKit.ButtonRow(cancel, ok);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        body.Children.Add(buttons);

        _ratio.IsChecked = !previous.Calibrate && linePoints is null;
        _calibrate.IsChecked = previous.Calibrate || linePoints is not null;
        UpdateLineText();
        Content = DialogFrame.Frame(this, owner, Title, () => { DialogResult = false; Close(); }, body);
        Loaded += (_, _) => (_calibrate.IsChecked == true ? _knownLength : _realLength).Focus();
        _knownLength.GotFocus += (_, _) => _calibrate.IsChecked = true;
        _paperLength.GotFocus += (_, _) => _ratio.IsChecked = true;
        _realLength.GotFocus += (_, _) => _ratio.IsChecked = true;
    }

    private void UpdateExistingLabel()
    {
        if (_updateExisting is null) return;
        int count = AllPages ? _measurementsEverywhere : _measurementsHere;
        _updateExisting.Content = string.Format(L("Str_MeasureScale_UpdateExisting"), count);
        _updateExisting.IsEnabled = count > 0;
    }

    private void UpdateLineText()
    {
        if (_linePoints is not double points)
        {
            _lineText.Text = L("Str_MeasureScale_NoLine");
            return;
        }
        string unit = UnitOf(_knownUnit);
        bool metric = MeasurementUnits.Find(unit).Metric;
        string paper = metric
            ? MeasurementUnits.FormatLength(points / 72 * 25.4, "mm", 1, CultureInfo.CurrentCulture)
            : MeasurementUnits.FormatLength(points / 72, "in", 2, CultureInfo.CurrentCulture);
        _lineText.Text = string.Format(L("Str_MeasureScale_LineLength"), paper);
    }

    private void Commit()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        int precision = _precision.SelectedIndex < 0 ? 2 : _precision.SelectedIndex;
        try
        {
            if (_calibrate.IsChecked == true)
            {
                if (_linePoints is not double points || points <= 0
                    || !MeasurementUnits.TryParseLength(_knownLength.Text, culture, out double known))
                {
                    _problem.Visibility = Visibility.Visible;
                    return;
                }
                string unit = UnitOf(_knownUnit);
                double paperInches = points / 72;
                string paperUnit = MeasurementUnits.Find(unit).Metric ? "cm" : "in";
                double paperLength = paperInches / MeasurementUnits.Find(paperUnit).Inches;
                Profile = new PdfMeasurementProfile(
                    MeasurementUnits.RatioText(Math.Round(paperLength, 4), paperUnit, known, unit),
                    known / points, unit, precision);
            }
            else
            {
                if (!MeasurementUnits.TryParseLength(_paperLength.Text, culture, out double paper)
                    || !MeasurementUnits.TryParseLength(_realLength.Text, culture, out double real))
                {
                    _problem.Visibility = Visibility.Visible;
                    return;
                }
                string paperUnit = UnitOf(_paperUnit), realUnit = UnitOf(_realUnit);
                Profile = new PdfMeasurementProfile(MeasurementUnits.RatioText(paper, paperUnit, real, realUnit),
                    MeasurementUnits.UnitsPerPoint(paper, paperUnit, real, realUnit), realUnit, precision);
            }
        }
        catch (ArgumentException)
        {
            _problem.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
        Close();
    }

    private static TextBox LengthField(string text)
    {
        var field = UiKit.Field(72);
        field.Text = text;
        field.Margin = new Thickness(0, 0, 6, 0);
        field.VerticalAlignment = VerticalAlignment.Center;
        return field;
    }

    private static ComboBox UnitCombo(string selected)
    {
        var combo = new ComboBox { Width = 120, VerticalAlignment = VerticalAlignment.Center };
        if (UiKit.DarkComboStyle is Style style) combo.Style = style;
        foreach (MeasureUnit unit in MeasurementUnits.Offered)
        {
            var item = new ComboBoxItem { Content = UnitName(unit.Id), Tag = unit.Id };
            combo.Items.Add(item);
            if (unit.Id == selected) combo.SelectedItem = item;
        }
        if (combo.SelectedItem is null) combo.SelectedIndex = 0;
        return combo;
    }

    private static string UnitOf(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "in";

    /// <summary>The unit's name in the active language: "Feet and inches", "Millimeters".</summary>
    internal static string UnitName(string unit) => L("Str_Measure_Unit_" + unit.Replace("-", "", StringComparison.Ordinal));

    private static string Number(double value, CultureInfo culture) =>
        value.ToString(value >= 100 ? "0.##" : "0.####", culture);

    private static string L(string key) =>
        Application.Current?.TryFindResource(key) as string ?? key;
}
