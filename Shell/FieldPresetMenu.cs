using System.Windows;
using System.Windows.Controls;
using KillerPdf.Engine.Authoring;

namespace KillerPDF
{
    // The page menu's Fillable Field Preset submenu (#340): each saved preset inserts a field at
    // the point that was right-clicked.
    public partial class MainWindow
    {
        private MenuItem BuildFieldPresetMenu(Point pt, int pageIdx)
        {
            var root = new MenuItem { Header = Loc("Str_FieldPreset_Menu") };
            PdfTextFieldPresetCollection? presets = ActiveViewer.LoadFieldPresets(quiet: true);
            if (presets is { Presets.Count: > 0 })
                foreach (PdfTextFieldPreset preset in presets.Presets)
                {
                    var item = MakeMenuItem(preset.Name, (s, e) =>
                    {
                        if (!EnsureEditable()) return;
                        ActiveViewer.PlaceTextFieldPreset(pt, pageIdx, preset);
                    });
                    item.ToolTip = Controls.TextFieldPresetsDialog.Detail(preset);
                    root.Items.Add(item);
                }
            else
                root.Items.Add(new MenuItem { Header = Loc("Str_FieldPreset_None"), IsEnabled = false });
            root.Items.Add(new Separator());
            root.Items.Add(MakeMenuItem(Loc("Str_FieldPreset_Manage"), (s, e) => ActiveViewer.OpenFieldPresets()));
            return root;
        }
    }
}
