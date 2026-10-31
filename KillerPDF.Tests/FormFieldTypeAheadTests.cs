using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Input;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// With the Select tool a single click selects a text form field and a double click edits it (#307).
/// The keys of a single click went to the main window, so a person filling in a form who typed after
/// the click ran tool shortcuts instead. A typed character now starts editing the selected field.
/// </summary>
public sealed class FormFieldTypeAheadTests
{
    [Theory]
    [InlineData(Key.T, ModifierKeys.None, 't')]
    [InlineData(Key.T, ModifierKeys.Shift, 'T')]
    [InlineData(Key.D5, ModifierKeys.None, '5')]
    [InlineData(Key.NumPad7, ModifierKeys.None, '7')]
    [InlineData(Key.OemPeriod, ModifierKeys.None, '.')]
    [InlineData(Key.D2, ModifierKeys.Shift, '@')]
    public void APrintableCharacterStartsEditing(Key key, ModifierKeys modifiers, char typed) =>
        Assert.True(FormFieldTypeAhead.StartsEditing(key, modifiers, typed));

    [Theory]
    [InlineData(Key.S, ModifierKeys.Control, 's')]
    [InlineData(Key.S, ModifierKeys.Control | ModifierKeys.Shift, 'S')]
    [InlineData(Key.F, ModifierKeys.Control, 'f')]
    [InlineData(Key.P, ModifierKeys.Control, 'p')]
    [InlineData(Key.Z, ModifierKeys.Control, 'z')]
    [InlineData(Key.V, ModifierKeys.Control, 'v')]
    [InlineData(Key.Left, ModifierKeys.Alt, '\0')]
    [InlineData(Key.T, ModifierKeys.Alt, 't')]
    [InlineData(Key.R, ModifierKeys.Windows, 'r')]
    public void AModifierShortcutKeepsItsMeaning(Key key, ModifierKeys modifiers, char typed) =>
        Assert.False(FormFieldTypeAhead.StartsEditing(key, modifiers, typed));

    [Theory]
    [InlineData(Key.Left, '\0')]
    [InlineData(Key.PageDown, '\0')]
    [InlineData(Key.F5, '\0')]
    [InlineData(Key.Escape, '\u001B')]
    [InlineData(Key.Enter, '\r')]
    [InlineData(Key.Tab, '\t')]
    [InlineData(Key.Back, '\b')]
    [InlineData(Key.Delete, '\u007F')]
    [InlineData(Key.LeftShift, '\0')]
    public void AKeyThatTypesNothingStaysWithTheWindow(Key key, char typed) =>
        Assert.False(FormFieldTypeAhead.StartsEditing(key, ModifierKeys.None, typed));

    // Space pans the view while it is held, so it does not open an edit with a leading blank.
    [Fact]
    public void SpaceStaysWithTheWindow() =>
        Assert.False(FormFieldTypeAhead.StartsEditing(Key.Space, ModifierKeys.None, ' '));

    [Fact]
    public void AKeyAnInputMethodIsComposingIsNotTaken() =>
        Assert.False(FormFieldTypeAhead.StartsEditing(Key.ImeProcessed, ModifierKeys.None, 'a'));

    // The window and viewer are not built here, so the wiring is pinned in the source the way the
    // other shortcut tests do it.
    [Fact]
    public void TheWindowOffersTheKeyToTheSelectedFieldBeforeAnyShortcutRuns()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Shell", "KeyboardShortcuts.cs"));
        int typeAhead = source.IndexOf("FormFieldTypeAhead.StartsEditing", StringComparison.Ordinal);
        Assert.True(typeAhead >= 0, "The window never asks whether a typed key starts a field edit.");
        Assert.True(typeAhead < source.IndexOf("_syntheticShortcut is null && !KeyboardShortcutsEnabled", StringComparison.Ordinal),
            "The edit must start even while the keyboard shortcuts are switched off.");
        Assert.True(typeAhead < source.IndexOf("TryRunExpandedShortcut(", StringComparison.Ordinal));
        Assert.True(typeAhead < source.IndexOf("TryToolShortcut(key)", StringComparison.Ordinal));

        // Only the window itself, with no other control holding the keyboard, hands a key to the field.
        string call = source[(typeAhead - 400)..(typeAhead + 400)];
        Assert.Contains("_syntheticShortcut is null", call);
        Assert.Contains("ReferenceEquals(e.OriginalSource, this)", call);
        Assert.Contains("ActiveViewer.CanTypeIntoSelectedFormFieldExt", call);
        Assert.Contains("ActiveViewer.BeginTypedFormFieldEditExt()", call);
        // The key itself must reach the field as text, so the window leaves it unhandled.
        Match branch = Regex.Match(source[typeAhead..], @"BeginTypedFormFieldEditExt\(\)\)\s*return;");
        Assert.True(branch.Success, "The window must return without handling the key.");
    }

    [Fact]
    public void OnlyASelectedTextFieldWithAnEditorTakesTheKey()
    {
        string forms = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Controls", "Viewer", "PdfViewer.Forms.cs"));
        Match resolve = Regex.Match(forms, @"private Control\? SelectedFormFieldEditor\(\)\s*\{(?<body>.*?)\n        \}",
            RegexOptions.Singleline);
        Assert.True(resolve.Success, "SelectedFormFieldEditor was not found.");
        string body = resolve.Groups["body"].Value;
        // The Select tool only: the Form Field tool drags its fields and never edits them.
        Assert.Contains("_currentTool != EditTool.Select", body);
        // Editors come from the text and password boxes, so a check box or button is never one.
        Assert.Contains("_formValueBoxes.TryGetValue", body);
        Assert.Contains("_formPasswordBoxes.TryGetValue", body);
        Assert.DoesNotContain("FormToggleSurface", body);
        Assert.Contains("IsReadOnly", body);
        Assert.Contains("IsVisible", body);

        Match begin = Regex.Match(forms, @"internal bool BeginTypedFormFieldEdit\(\)\s*\{(?<body>.*?)\n        \}",
            RegexOptions.Singleline);
        Assert.True(begin.Success, "BeginTypedFormFieldEdit was not found.");
        // The field takes the keyboard with its content selected, so the character that follows replaces
        // the value, as it does over a selected spreadsheet cell.
        Assert.Matches(@"\.Focus\(\)[\s\S]*SelectAll\(\)", begin.Groups["body"].Value);
    }

    [Fact]
    public void TheFieldsAreRegisteredWhereTheViewerCanFindThemAgainAfterARedraw()
    {
        string forms = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Controls", "Viewer", "PdfViewer.Forms.cs"));
        Assert.Matches(@"_formPasswordBoxes\[passwordKey\]\s*=\s*pb", forms);
        Assert.Contains("RegisterFormValueBox(capturedKey, tb);", forms);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("KillerPDF.csproj was not found above the test output.");
    }
}
