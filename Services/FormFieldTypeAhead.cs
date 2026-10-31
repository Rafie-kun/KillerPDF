using System.Windows.Input;

namespace KillerPDF.Services;

/// <summary>
/// Decides whether a key pressed over a selected text form field starts editing it. With the Select tool
/// a single click only selects the field, so the keys that follow reach the main window, where a plain
/// letter is a tool shortcut.
/// </summary>
internal static class FormFieldTypeAhead
{
    /// <summary>
    /// A plain character, shifted or not, starts editing, as typing over a selected spreadsheet cell does.
    /// Ctrl, Alt and the Windows key keep their shortcut meaning, and a key that types nothing, a control
    /// character or a blank stays with the window. Space is one of those, since held it pans the view.
    /// <paramref name="typed"/> is what the key types on the current layout, or '\0' for none.
    /// </summary>
    internal static bool StartsEditing(Key key, ModifierKeys modifiers, char typed)
    {
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
            return false;
        // An input method composing text owns the key until it commits.
        if (key is Key.ImeProcessed or Key.None)
            return false;
        return typed != '\0' && !char.IsControl(typed) && !char.IsWhiteSpace(typed);
    }
}
