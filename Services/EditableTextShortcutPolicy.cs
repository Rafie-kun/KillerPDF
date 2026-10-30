using System.Windows.Input;

namespace KillerPDF.Services;

/// <summary>Separates text-editing gestures from window-level application shortcuts.</summary>
internal static class EditableTextShortcutPolicy
{
    internal static bool CommitsTextBox(Key key, ModifierKeys modifiers) =>
        key == Key.Enter && IsControlChord(modifiers);

    internal static bool BacksOutOfEdit(Key key, ModifierKeys modifiers) =>
        key == Key.Z && IsControlChord(modifiers);

    // Windows reports AltGr as Ctrl+Alt, and those keys type text on many layouts.
    private static bool IsControlChord(ModifierKeys modifiers) =>
        (modifiers & ModifierKeys.Control) != 0 && (modifiers & ModifierKeys.Alt) == 0;

    // Views that reuse one page canvas clear its children on a page turn or tab change, so the
    // layer can come back detached. A box added to a detached layer is never seen and never focused.
    internal static void AttachEditorLayer(System.Windows.Controls.Canvas page, System.Windows.Controls.Canvas layer)
    {
        if (layer.Parent is null) page.Children.Add(layer);
    }

    internal static double TextBoxTop(double pointerY, double fontSize) =>
        Math.Max(0, pointerY - Math.Max(0, fontSize) / 2.0);

    internal static bool KeepInTextBox(Key key, ModifierKeys modifiers,
        Key systemKey = Key.None)
    {
        Key effectiveKey = key == Key.System ? systemKey : key;
        if ((modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return false;
        if (effectiveKey is >= Key.F1 and <= Key.F24)
            return false;
        if ((modifiers & ModifierKeys.Control) == 0)
            return true;

        return effectiveKey is Key.A or Key.C or Key.V or Key.X or Key.Z or Key.Y
            or Key.Back or Key.Delete or Key.Insert
            or Key.Left or Key.Right or Key.Up or Key.Down
            or Key.Home or Key.End or Key.Space;
    }
}
