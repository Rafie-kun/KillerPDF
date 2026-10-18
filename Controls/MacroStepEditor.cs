using System.Globalization;
using System.Text.Json;
using System.Windows;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPDF.Services;

namespace KillerPDF;

internal static class MacroStepEditor
{
    internal static string? TitleKey(PdfMacroOperation operation) => operation switch
    {
        PdfMacroOperation.EditLayers => "Str_DocumentTools_EditLayers",
        PdfMacroOperation.EditAttachments => "Str_DocumentTools_EditAttachments",
        PdfMacroOperation.EditPortfolio => "Str_DocumentTools_EditPortfolio",
        PdfMacroOperation.SetPageLabels => "Str_DocumentTools_PageLabels",
        PdfMacroOperation.Watermark => "Str_Stamp_SecWatermark",
        PdfMacroOperation.Rename => "Str_Ctx_BmRename",
        PdfMacroOperation.DataMerge => "Str_DocumentTools_DataMerge",
        PdfMacroOperation.Resize => "Str_Tf_ResizePage",
        _ => null
    };

    internal static PdfMacroStep? Edit(Window owner, PdfMacroStep step)
    {
        try
        {
            if (step.Operation == PdfMacroOperation.Resize)
            {
                double percent = step.Settings is null ? 100 : PdfResizeMacro.ReadPercent(step);
                var resize = new DocumentValuesDialog(owner, L("Str_Tf_ResizePage"),
                    (L("Str_Tf_Scale") + " (%)", percent.ToString(CultureInfo.CurrentCulture)));
                return resize.ShowDialog() == true
                    ? PdfResizeMacro.ScaleStep(double.Parse(resize.Values[0], CultureInfo.CurrentCulture)) : null;
            }
            if (step.Operation == PdfMacroOperation.DataMerge)
            {
                var picker = new Controls.FileDialog(Controls.FileDialogMode.Open)
                {
                    Title = L("Str_Merge_LoadProfile"), Filter = "JSON|*.json"
                };
                return picker.ShowDialog(owner) == true
                    ? PdfDataMergeProfile.FromJson(System.IO.File.ReadAllText(picker.FileName)).ToMacroStep() : null;
            }
            if (step.Operation == PdfMacroOperation.Rename)
            {
                string template = step.Settings is null ? "{name}-renamed" : PdfRenameMacro.ReadTemplate(step);
                var rename = new DocumentValuesDialog(owner, L("Str_Ctx_BmRename"),
                    (L("Str_Attachment_Name") + " ({name}, {index})", template));
                return rename.ShowDialog() == true ? PdfRenameMacro.RenameStep(rename.Values[0]) : null;
            }
            if (step.Operation == PdfMacroOperation.Watermark)
            {
                PdfTextWatermarkOptions current = step.Settings is null || step.Settings.Count == 0
                    ? new(L("Str_Stamp_DefaultText")) : PdfEngineBurn.ReadWatermarkStep(step);
                var dialog = new DocumentValuesDialog(owner, L("Str_Stamp_SecWatermark"),
                    (L("Str_Stamp_WmTextLabel"), current.Text),
                    (L("Str_Stamp_FontSize"), current.FontSize.ToString(CultureInfo.CurrentCulture)),
                    (L("Str_Stamp_Opacity") + " (%)", (current.Opacity * 100).ToString(CultureInfo.CurrentCulture)),
                    (L("Str_Stamp_Angle") + " (°)", current.RotationDegrees.ToString(CultureInfo.CurrentCulture)));
                if (dialog.ShowDialog() != true) return null;
                return PdfEngineBurn.WatermarkStep(new(dialog.Values[0],
                    double.Parse(dialog.Values[1], CultureInfo.CurrentCulture),
                    double.Parse(dialog.Values[2], CultureInfo.CurrentCulture) / 100,
                    double.Parse(dialog.Values[3], CultureInfo.CurrentCulture)));
            }
            string Current(string key, string fallback = "") => step.Settings?.GetValueOrDefault(key) ?? fallback;
            if ((step.Operation == PdfMacroOperation.EditLayers && Current("action") is not ("" or "rename" or "visibility" or "lock"))
                || (step.Operation == PdfMacroOperation.EditAttachments && Current("action") is not ("" or "rename" or "description"))
                || (step.Operation == PdfMacroOperation.EditPortfolio && Current("action") is not ("" or "presentation" or "clear")))
            {
                KillerDialog.Show(owner, L("Str_Macro_NoSettings"));
                return null;
            }
            int ChooseSelected(string title, int selected, params string[] keys)
            {
                var choices = new DocumentListDialog(owner, L(title), keys.Select(L).ToArray());
                choices.SelectedIndex = selected;
                return choices.ShowDialog() == true ? choices.SelectedIndex : -1;
            }
            if (step.Operation == PdfMacroOperation.EditLayers)
            {
                int action = ChooseSelected("Str_DocumentTools_EditLayers", Current("action") switch
                    { "visibility" => 1, "lock" => 2, _ => 0 }, "Str_Ctx_BmRename",
                    "Str_DocumentTools_LayerVisible", "Str_DocumentTools_LayerLocked");
                if (action < 0) return null;
                var fields = new List<(string, string)> { (L("Str_DocumentTools_LayerName"), Current("layer")) };
                if (action == 0) fields.Add((L("Str_Attachment_Name"), Current("action") == "rename" ? Current("value") : ""));
                var values = new DocumentValuesDialog(owner, L("Str_DocumentTools_EditLayers"), fields.ToArray());
                if (values.ShowDialog() != true) return null;
                if (action == 0) return PdfLayerMacro.RenameStep(values.Values[0], values.Values[1]);
                int enabled = ChooseSelected(action == 1 ? "Str_DocumentTools_LayerVisible" : "Str_DocumentTools_LayerLocked",
                    Current("action") == (action == 1 ? "visibility" : "lock") && Current("value") == "false" ? 1 : 0,
                    "Str_Btn_Yes", "Str_Btn_No");
                if (enabled < 0) return null;
                return action == 1 ? PdfLayerMacro.VisibilityStep(values.Values[0], enabled == 0)
                    : PdfLayerMacro.LockStep(values.Values[0], enabled == 0);
            }
            if (step.Operation == PdfMacroOperation.EditAttachments)
            {
                int action = ChooseSelected("Str_DocumentTools_EditAttachments", Current("action") == "description" ? 1 : 0,
                    "Str_Ctx_BmRename", "Str_Attachment_Description");
                if (action < 0) return null;
                string key = action == 0 ? "newFileName" : "description";
                var values = new DocumentValuesDialog(owner, L("Str_DocumentTools_EditAttachments"),
                    (L("Str_Attachment_Name"), Current("fileName")),
                    (L(action == 0 ? "Str_Ctx_BmRename" : "Str_Attachment_Description"), Current(key)));
                if (values.ShowDialog() != true) return null;
                return action == 0 ? PdfAttachmentMacro.RenameStep(values.Values[0], values.Values[1])
                    : PdfAttachmentMacro.DescriptionStep(values.Values[0], string.IsNullOrWhiteSpace(values.Values[1]) ? null : values.Values[1]);
            }
            if (step.Operation == PdfMacroOperation.EditPortfolio)
            {
                int view = ChooseSelected("Str_DocumentTools_EditPortfolio", Current("action") == "clear" ? 3
                    : Current("view").ToLowerInvariant() switch { "tile" => 1, "hidden" => 2, _ => 0 }, "Str_Portfolio_Details", "Str_Portfolio_Tile",
                    "Str_Portfolio_Hidden", "Str_Portfolio_RemoveMetadata");
                if (view < 0) return null;
                if (view == 3) return PdfCollectionMacro.ClearStep();
                var values = new DocumentValuesDialog(owner, L("Str_DocumentTools_EditPortfolio"),
                    (L("Str_Portfolio_InitialDocument"), Current("initialDocument")));
                if (values.ShowDialog() != true) return null;
                return PdfCollectionMacro.PresentationStep(new[] { PdfCollectionView.Details, PdfCollectionView.Tile,
                    PdfCollectionView.Hidden }[view], string.IsNullOrWhiteSpace(values.Values[0]) ? null : values.Values[0]);
            }
            if (step.Operation == PdfMacroOperation.SetPageLabels)
            {
                var ranges = step.Settings?.TryGetValue("ranges", out string? json) == true
                    ? JsonSerializer.Deserialize<PdfPageLabelMacroRange[]>(json, JsonOptions) : null;
                var dialog = new PageLabelsDialog(owner, int.MaxValue, ranges);
                return dialog.ShowDialog() == true ? PdfNavigationMacro.PageLabelsStep(dialog.Ranges) : null;
            }
            if (step.Operation is PdfMacroOperation.ImposeNUp or PdfMacroOperation.ImposeBooklet
                or PdfMacroOperation.ImposeStepAndRepeat or PdfMacroOperation.ImposeCutStack
                or PdfMacroOperation.ImposeManualSequence or PdfMacroOperation.ImposePoster)
            {
                var dialog = new ProductionLayoutDialog(owner, step.Operation, step);
                return dialog.ShowDialog() == true ? dialog.Step : null;
            }
            if (step.Operation == PdfMacroOperation.Validate)
            {
                PdfPreflightProfile[] profiles = [PdfPreflightProfile.General, PdfPreflightProfile.PrintProduction,
                    PdfPreflightProfile.Attachments];
                var select = new DocumentListDialog(owner, L("Str_DocumentTools_Preflight"),
                    new[] { L("Str_DocumentTools_Preflight"), L("Str_DocumentTools_PrintProduction"), L("Str_DocumentTools_EditAttachments") });
                return select.ShowDialog() == true ? new PdfMacroStep(step.Operation,
                    new Dictionary<string, string> { ["profileJson"] = profiles[select.SelectedIndex].ToJson() }) : null;
            }
            if (step.Operation == PdfMacroOperation.NumberPages)
            {
                if (step.Settings?.ContainsKey("batesOptions") == true)
                {
                    KillerDialog.Show(owner, L("Str_Macro_NoSettings"));
                    return null;
                }
                PdfPageNumberMacroOptions current = step.Settings?.TryGetValue("options", out string? json) == true
                    ? JsonSerializer.Deserialize<PdfPageNumberMacroOptions>(json, JsonOptions)
                        ?? throw new JsonException("Empty page-numbering options.")
                    : new() { Date = DateOnly.FromDateTime(DateTime.Today) };
                var dialog = new DocumentValuesDialog(owner, L("Str_Ctx_StampPages"),
                    (L("Str_Lbl_Text") + " ({page}, {pages}, {date}, {filename})", current.Template),
                    (L("Str_Stamp_FontSize"), current.FontSize.ToString(CultureInfo.CurrentCulture)));
                if (dialog.ShowDialog() != true) return null;
                return PdfPageFurnitureMacro.NumberPagesStep(current with { Template = dialog.Values[0],
                    FontSize = double.Parse(dialog.Values[1], CultureInfo.CurrentCulture) });
            }
            if (step.Operation is PdfMacroOperation.GenerateBookmarks or PdfMacroOperation.GenerateTableOfContents)
            {
                var fields = new List<(string, string)> { (L("Str_AutoBookmarks_MaximumDepth"), Current("maximumDepth", "6")) };
                if (step.Operation == PdfMacroOperation.GenerateBookmarks)
                    fields.Add((L("Str_AutoBookmarks_MinimumSize"), Current("minimumPointSize", "12")));
                var dialog = new DocumentValuesDialog(owner, L("Str_DocumentTools_AutoBookmarks"), fields.ToArray());
                if (dialog.ShowDialog() != true) return null;
                int depth = int.Parse(dialog.Values[0], CultureInfo.CurrentCulture);
                if (depth < 1 || depth > 256) throw new ArgumentOutOfRangeException(nameof(depth));
                if (step.Operation == PdfMacroOperation.GenerateTableOfContents) return PdfNavigationMacro.TableOfContentsStep(depth);
                double size = double.Parse(dialog.Values[1], CultureInfo.CurrentCulture);
                if (!double.IsFinite(size) || size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
                var settings = step.Settings is null ? new Dictionary<string, string>() : new(step.Settings);
                settings["maximumDepth"] = depth.ToString(CultureInfo.InvariantCulture);
                settings["minimumPointSize"] = size.ToString(CultureInfo.InvariantCulture);
                settings.TryAdd("maximumTitleLength", "160");
                settings["acceptDetectedHeadings"] = "true";
                return new(step.Operation, settings);
            }
            KillerDialog.Show(owner, L("Str_Macro_NoSettings"));
        }
        catch (Exception error) when (error is ArgumentException or FormatException or OverflowException
            or JsonException or InvalidOperationException or NotSupportedException)
        {
            KillerDialog.Show(owner, L("Str_Macro_Invalid") + "\n" + error.Message);
        }
        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
    private static string L(string key) => Application.Current.TryFindResource(key) as string ?? key;
}
