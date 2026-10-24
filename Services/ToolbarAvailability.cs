namespace KillerPDF.Services;

// Which toolbar buttons can be used in the current document state. Free of WPF and of
// MainWindow so the rules can be checked without standing up a window.
internal static class ToolbarAvailability
{
    internal readonly record struct State(bool HasDocument, bool CanUndo, bool CanRedo, bool CanClear,
        int SelectedCount, int SelectedIndex, int PageCount);

    /// <summary>Whether a toolbar unit's main button runs its command.</summary>
    internal static bool MainEnabled(string id, State state) => id switch
    {
        "new" or "open" => true,
        "undo" => state.HasDocument && state.CanUndo,
        "redo" => state.HasDocument && state.CanRedo,
        "clear" => state.HasDocument && state.CanClear,
        "extract" => state.HasDocument && state.SelectedCount > 0,
        "delete" => state.HasDocument && state.SelectedCount > 0 && state.SelectedCount < state.PageCount,
        "moveup" => state.HasDocument && state.SelectedIndex > 0,
        "movedown" => state.HasDocument && state.SelectedIndex >= 0 && state.SelectedIndex < state.PageCount - 1,
        _ => state.HasDocument,
    };

    /// <summary>Whether a unit's group, including any dropdown beside its main button, takes clicks.
    /// The Save dropdown stays open to clicks with no document: it still offers Document tools.</summary>
    internal static bool GroupEnabled(string id, State state) => id == "save" || MainEnabled(id, state);
}
