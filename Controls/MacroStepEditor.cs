using System.Globalization;
using System.Text.Json;
using System.Windows;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;

namespace KillerPDF;

internal static class MacroStepEditor
{
    internal static PdfMacroStep? Edit(Window owner, PdfMacroStep step)
    {
        try
        {
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
                string Current(string key, string fallback) => step.Settings?.GetValueOrDefault(key) ?? fallback;
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
