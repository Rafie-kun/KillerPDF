using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF;

internal sealed class ProductionLayoutDialog : Window
{
    private readonly PdfMacroOperation _operation;
    private string _presetName;
    private readonly Dictionary<string, TextBox> _fields = [];
    private readonly Dictionary<string, CheckBox> _flags = [];
    private readonly ComboBox _sourceBox = new() { Style = UiKit.DarkComboStyle };
    internal PdfMacroStep? Step { get; private set; }

    internal ProductionLayoutDialog(Window owner, PdfMacroOperation operation, PdfMacroStep? current = null)
    {
        _operation = operation;
        var preset = current?.Settings?.TryGetValue("preset", out string? json) == true
            ? PdfImpositionPreset.FromJson(json)
            : new PdfImpositionPreset("Desktop", operation == PdfMacroOperation.ImposePoster ? 1 : 2, 1,
                842, 595, margin: 18, gutter: 12, duplex: operation == PdfMacroOperation.ImposeBooklet);
        _presetName = preset.Name;
        Title = "KillerPDF - " + L(TitleKey(operation));
        Width = 800;
        Height = operation is PdfMacroOperation.ImposeStepAndRepeat or PdfMacroOperation.ImposePoster ? 490 : 440;
        MinWidth = 700; MinHeight = 400;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 60);
        UseLayoutRounding = true;
        DialogFrame.Configure(this, owner);
        var body = new DockPanel { Margin = new Thickness(20, 6, 20, 16) };
        var cancel = UiKit.Make(L("Str_Btn_Cancel"), accent: false);
        cancel.IsCancel = true; cancel.Click += (_, _) => Close();
        var apply = UiKit.Make(L("Str_Tf_Apply"), accent: true);
        apply.IsDefault = true; apply.Click += (_, _) => Safely(() => { Step = Build(); DialogResult = true; });
        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var buttons = UiKit.ButtonRow(cancel, apply);
        DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        DockPanel.SetDock(footer, Dock.Bottom); body.Children.Add(footer);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var inputPanel = new StackPanel(); var flags = new StackPanel();
        var inputs = new Grid();
        inputs.ColumnDefinitions.Add(new ColumnDefinition());
        inputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        inputs.ColumnDefinitions.Add(new ColumnDefinition());
        var layoutHeading = UiKit.GroupLabel(L("Str_Print_SecLayout"));
        var outputHeading = UiKit.GroupLabel(L("Str_Print_SecOutput"));
        layoutHeading.Margin = outputHeading.Margin = new Thickness(0, 0, 0, 14);
        inputPanel.Children.Add(layoutHeading); inputPanel.Children.Add(inputs); flags.Children.Add(outputHeading);
        Grid.SetColumn(flags, 2); grid.Children.Add(inputPanel); grid.Children.Add(flags);
        body.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Add("columns", "Str_Production_Columns", preset.Columns);
        Add("rows", "Str_Production_Rows", preset.Rows);
        Add("width", "Str_Sign_Width", preset.SheetWidth, points: true);
        Add("height", "Str_Sign_Height", preset.SheetHeight, points: true);
        Add("margin", "Str_Print_Margins", preset.Margin, points: true);
        Add("gutter", "Str_Production_Gutter", preset.Gutter, points: true);
        Add("creep", "Str_Production_Creep", preset.CreepPerSheet, points: true);
        if (operation == PdfMacroOperation.ImposeBooklet) Add("signaturePages", "Str_Production_Signature", Setting("signaturePages", "0"));
        if (operation is PdfMacroOperation.ImposeStepAndRepeat or PdfMacroOperation.ImposePoster)
            Add("sourcePage", "Str_AutoBookmarks_Page", int.Parse(Setting("sourcePage", "0"), CultureInfo.InvariantCulture) + 1);
        if (operation == PdfMacroOperation.ImposeStepAndRepeat) Add("copyCount", "Str_Print_Copies", Setting("copyCount", "1"));
        if (operation == PdfMacroOperation.ImposePoster) Add("overlap", "Str_Production_Overlap",
            double.Parse(Setting("overlap", "0"), CultureInfo.InvariantCulture), points: true);
        if (operation == PdfMacroOperation.ImposeManualSequence)
        {
            int?[] sequence = current?.Settings?.TryGetValue("pageSequence", out string? sequenceJson) == true
                ? JsonSerializer.Deserialize<int?[]>(sequenceJson) ?? [] : [];
            Add("sequence", "Str_Production_Sequence", string.Join(",", sequence.Select(page => page.HasValue ? page + 1 : 0)));
        }
        Flag("duplex", "Str_Print_TwoSided", preset.Duplex);
        Flag("short", "Str_Production_BindingShort", preset.BindingEdge == PdfImpositionBindingEdge.Short);
        Flag("rotate", "Str_Production_Rotate", preset.RotateToFit);
        Flag("crop", "Str_Production_Marks", preset.IncludeCropMarks);
        Flag("register", "Str_Production_Registration", preset.IncludeRegistrationMarks);
        Flag("fold", "Str_Production_Fold", preset.IncludeFoldMarks);
        Flag("color", "Str_Production_ColorBars", preset.IncludeColorBars);
        Flag("info", "Str_Production_PageInfo", preset.IncludePageInformation);
        flags.Children.Add(UiKit.GroupLabel(L("Str_Production_SourceBox")));
        _sourceBox.ItemsSource = new[] { L("Str_Crop_Apply"), L("Str_Repair_Bleed"), L("Str_Repair_Trim"), L("Str_Repair_Art") };
        _sourceBox.SelectedIndex = (int)preset.SourceBox; _sourceBox.Margin = new Thickness(0, 6, 0, 16);
        flags.Children.Add(_sourceBox);
        var load = UiKit.Make(L("Str_Merge_LoadProfile"), accent: false);
        load.Click += (_, _) => Safely(() =>
        {
            var picker = new Controls.FileDialog(Controls.FileDialogMode.Open) { Filter = "JSON|*.json", Title = L("Str_Merge_LoadProfile") };
            if (picker.ShowDialog(this) == true) LoadPreset(PdfImpositionPreset.FromJson(File.ReadAllText(picker.FileName)));
        });
        var save = UiKit.Make(L("Str_Merge_SaveProfile"), accent: false);
        save.Click += (_, _) => Safely(() =>
        {
            var value = BuildPreset();
            var picker = new Controls.FileDialog(Controls.FileDialogMode.Save)
                { Filter = "JSON|*.json", DefaultExt = "json", FileName = "layout.json", Title = L("Str_Merge_SaveProfile") };
            if (picker.ShowDialog(this) == true) PdfEngineIntegration.WriteExchangeFile(picker.FileName,
                System.Text.Encoding.UTF8.GetBytes(value.ToJson(indented: true)));
        });
        var profiles = UiKit.ButtonRow(load, save);
        profiles.HorizontalAlignment = HorizontalAlignment.Left;
        footer.Children.Add(profiles);
        Content = DialogFrame.Frame(this, owner, Title, Close, body);

        string Setting(string key, string fallback) => current?.Settings?.GetValueOrDefault(key) ?? fallback;
        void Add(string key, string labelKey, object value, bool points = false)
        {
            string label = L(labelKey) + (points ? " (pt)" : "");
            int position = _fields.Count;
            int row = position / 2 * 2, column = position % 2 * 2;
            if (column == 0)
            {
                inputs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                inputs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            var caption = UiKit.GroupLabel(label);
            caption.TextWrapping = TextWrapping.Wrap;
            Grid.SetRow(caption, row); Grid.SetColumn(caption, column); inputs.Children.Add(caption);
            var input = UiKit.Field(); input.Text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
            input.Margin = new Thickness(0, 5, 0, 14); _fields.Add(key, input);
            Grid.SetRow(input, row + 1); Grid.SetColumn(input, column);
            System.Windows.Automation.AutomationProperties.SetName(input, label); inputs.Children.Add(input);
        }
        void Flag(string key, string label, bool value)
        {
            var check = UiKit.CheckBox(L(label)); check.IsChecked = value;
            check.Margin = new Thickness(0, 0, 0, 10); _flags.Add(key, check); flags.Children.Add(check);
        }
    }

    internal static string TitleKey(PdfMacroOperation operation) => "Str_Production_" + (operation switch
    {
        PdfMacroOperation.ImposeBooklet => "Booklet", PdfMacroOperation.ImposeStepAndRepeat => "Repeat",
        PdfMacroOperation.ImposeCutStack => "CutStack", PdfMacroOperation.ImposeManualSequence => "Manual",
        PdfMacroOperation.ImposePoster => "Poster", _ => "NUp"
    });

    private double D(string key) => double.Parse(_fields[key].Text, CultureInfo.CurrentCulture);
    private int I(string key) => int.Parse(_fields[key].Text, CultureInfo.CurrentCulture);
    private bool Flag(string key) => _flags[key].IsChecked == true;
    private PdfImpositionPreset BuildPreset() => new(_presetName, I("columns"), I("rows"), D("width"), D("height"),
        D("margin"), D("gutter"), Flag("duplex"), Flag("rotate"), Flag("crop"), Flag("register"), D("creep"),
        Flag("fold"), Flag("color"), Flag("info"), (PdfImpositionSourceBox)_sourceBox.SelectedIndex,
        Flag("short") ? PdfImpositionBindingEdge.Short : PdfImpositionBindingEdge.Long);

    private PdfMacroStep Build()
    {
        var preset = BuildPreset();
        if (_operation == PdfMacroOperation.ImposeStepAndRepeat && I("copyCount") < 1)
            throw new ArgumentOutOfRangeException("copyCount");
        if (_operation == PdfMacroOperation.ImposeManualSequence && string.IsNullOrWhiteSpace(_fields["sequence"].Text))
            throw new ArgumentException(L("Str_Production_Sequence"));
        return _operation switch
        {
            PdfMacroOperation.ImposeBooklet => PdfImpositionMacro.BookletStep(preset, I("signaturePages")),
            PdfMacroOperation.ImposeStepAndRepeat => PdfImpositionMacro.StepAndRepeatStep(preset, I("sourcePage") - 1, I("copyCount")),
            PdfMacroOperation.ImposeCutStack => PdfImpositionMacro.CutStackStep(preset),
            PdfMacroOperation.ImposePoster => PdfImpositionMacro.PosterStep(preset, I("sourcePage") - 1, D("overlap")),
            PdfMacroOperation.ImposeManualSequence => PdfImpositionMacro.ManualSequenceStep(preset, ReadSequence()),
            _ => PdfImpositionMacro.NUpStep(preset)
        };
    }

    private int?[] ReadSequence()
    {
        int?[] pages = _fields["sequence"].Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(text => int.Parse(text, CultureInfo.CurrentCulture)).Select(page => page == 0 ? (int?)null : page - 1).ToArray();
        if (pages.Length == 0) throw new ArgumentException(L("Str_Production_Sequence"));
        return pages;
    }

    private void LoadPreset(PdfImpositionPreset preset)
    {
        _presetName = preset.Name;
        void Set(string key, object value) => _fields[key].Text = Convert.ToString(value, CultureInfo.CurrentCulture) ?? "";
        Set("columns", preset.Columns); Set("rows", preset.Rows); Set("width", preset.SheetWidth); Set("height", preset.SheetHeight);
        Set("margin", preset.Margin); Set("gutter", preset.Gutter); Set("creep", preset.CreepPerSheet);
        _flags["duplex"].IsChecked = preset.Duplex; _flags["rotate"].IsChecked = preset.RotateToFit;
        _flags["crop"].IsChecked = preset.IncludeCropMarks; _flags["register"].IsChecked = preset.IncludeRegistrationMarks;
        _flags["fold"].IsChecked = preset.IncludeFoldMarks; _flags["color"].IsChecked = preset.IncludeColorBars;
        _flags["info"].IsChecked = preset.IncludePageInformation; _flags["short"].IsChecked = preset.BindingEdge == PdfImpositionBindingEdge.Short;
        _sourceBox.SelectedIndex = (int)preset.SourceBox;
    }

    private void Safely(Action action)
    {
        try { action(); }
        catch (Exception error) { KillerDialog.Show(this, L("Str_DocumentTools_Failed") + "\n" + error.Message); }
    }
    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
