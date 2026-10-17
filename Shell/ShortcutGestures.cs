using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace KillerPDF
{
    // ============================================================
    // Keyboard shortcuts as the action surfaces show them: menu gesture text and tooltips.
    //
    // One source for every surface, so a tooltip, a menu item and the shortcuts overlay cannot
    // describe one command two ways. Keys follow the user's custom shortcuts (#190) and key names
    // follow the active language, so a German menu reads Strg+C beside Strg+R instead of mixing
    // Ctrl and Strg. Menus are built when they open and pick this up then; the long-lived
    // tooltips are rebuilt by ApplyShortcutTooltips after a language change or a remap.
    // ============================================================
    public partial class MainWindow
    {
        /// <summary>The key a command answers to now, as display text: "Ctrl+S", "Strg+Umschalt+O",
        /// "F11". <paramref name="cap"/> uses the keyboard map's names, the way ShortcutTable.Cap
        /// does: "Ctrl:S", "CtrlShift:O", "F11", "Del", "Alt:Left".</summary>
        internal string GestureFor(string cap)
            => KeyText(CapKeys(ShortcutRemaps.Effective(ShortcutTable.Cap(cap))));

        /// <summary>Several keys for one command, joined the way the overlay joins them: "1 or T".</summary>
        private string GesturesFor(params string[] caps)
            => string.Join(" " + Loc("Str_Key_Or") + " ", caps.Select(GestureFor));

        /// <summary>A cap written with the overlay's key-name tokens.</summary>
        private static string CapKeys(KsCap cap)
        {
            string prefix = cap.Layer switch
            {
                KbLayer.Ctrl => "%ctrl%+",
                KbLayer.CtrlShift => "%ctrl%+%shift%+",
                KbLayer.Shift => "%shift%+",
                KbLayer.Alt => "%alt%+",
                KbLayer.AltShift => "%alt%+%shift%+",
                _ => ""
            };
            string key = cap.Id switch
            {
                "Del" => "%del%",
                "Enter" => "%enter%",
                "Esc" => "%esc%",
                "Menu" => "%menu%",
                "Home" => "%home%",
                "End" => "%end%",
                "PgUp" => "%pgup%",
                "PgDn" => "%pgdn%",
                "Tab" => "%tab%",
                "Equals" => "%zin%",
                "Minus" => "%zout%",
                "Slash" => "?",
                "Left" => "←",
                "Up" => "↑",
                "Right" => "→",
                "Down" => "↓",
                var digit when digit.Length == 2 && digit[0] == 'D' && char.IsDigit(digit[1]) => digit[1..],
                var other => other
            };
            return prefix + key;
        }

        /// <summary>Overlay key text with its tokens replaced by the active language's key names.</summary>
        private static string KeyText(string keys)
        {
            string text = ResolveKeyLabel(keys);
            foreach (var (token, key) in ShortcutTable.KeyTokens)
                if (text.Contains(token, StringComparison.Ordinal))
                    text = text.Replace(token, Loc(key), StringComparison.Ordinal);
            return text;
        }

        // Every tooltip string was written with its shortcut in brackets at the end, in every
        // language. The bracket is dropped and rebuilt from the live binding, so a moved key or a
        // language that does not say Ctrl never shows a stale shortcut. A bracket counts as a
        // shortcut when it holds one key (a letter, a digit or an F key) or a chord with a plus.
        private static readonly Regex TrailingShortcut =
            new(@"\s*\((?:[A-Z0-9]|F\d{1,2}|[^()]*\+[^()]*)\)\s*$", RegexOptions.CultureInvariant);

        internal static string WithoutShortcut(string text) => TrailingShortcut.Replace(text, "");

        /// <summary>A label with its current shortcut appended: "Save (Ctrl+S)".</summary>
        private string WithGesture(string label, params string[] caps)
            => caps.Length == 0 ? label : $"{label} ({GesturesFor(caps)})";

        /// <summary>A tooltip resource with its current shortcut appended.</summary>
        private string ShortcutTip(string resourceKey, params string[] caps)
            => WithGesture(WithoutShortcut(Loc(resourceKey)), caps);

        /// <summary>
        /// Rebuilds every tooltip that names a shortcut. Called at startup, after a language
        /// change and after the user moves a shortcut. Tool buttons list the toolbar digit first
        /// and the letter second, the same order as the overlay rows.
        /// </summary>
        private void ApplyShortcutTooltips()
        {
            void Tip(FrameworkElement? element, string key, params string[] caps)
            {
                if (element is null) return;
                element.ToolTip = ShortcutTip(key, caps);
            }

            Tip(NewBlankBtn, "Str_TT_NewBlank", "Ctrl:N");
            Tip(CloseFileBtn, "Str_TT_CloseFile", "Ctrl:W");
            Tip(OpenFileBtn, "Str_TT_Open", "Ctrl:O");
            Tip(SaveAsBtn, "Str_TT_Save", "Ctrl:S");
            Tip(OcrBtn, "Str_TT_Ocr", "CtrlShift:O");
            Tip(PrintBtn, "Str_TT_Print", "Ctrl:P");

            Tip(ToolSelectBtn, "Str_TT_SelectTool", "V");
            Tip(ToolTextBtn, "Str_TT_TextTool", "D1", "T");
            Tip(ToolHighlightBtn, "Str_TT_HighlightTool", "D2", "H");
            Tip(ToolUnderlineBtn, "Str_TT_LineTool", "D3", "L", "U");   // repurposed to the Line tool
            Tip(ToolShapeBtn, "Str_TT_ShapeTool", "D4");
            Tip(ToolDrawBtn, "Str_TT_DrawTool", "D5", "D");
            Tip(ToolImageBtn, "Str_TT_ImageTool", "D6", "I");
            Tip(ToolSignatureBtn, "Str_TT_SignatureTool", "D7", "G");
            Tip(ToolCropBtn, "Str_TT_CropTool", "D8", "C");
            Tip(_toolRotateBtn, "Str_TT_RotateTool", "D9", "R");
            Tip(ToolStampBtn, "Str_TT_StampTool", "D0", "S");
            Tip(ToolColorCorrectionBtn, "Str_ColorCorrection", "E");
            Tip(ToolMeasureBtn, "Str_TT_MeasureTool", "M");
            Tip(ToolRedactBtn, "Str_TT_RedactTool", "X");
            Tip(ToolFormFieldBtn, "Str_TT_FormFieldTool", "F");

            Tip(UndoBtn, "Str_TT_UndoLast", "Ctrl:Z");
            Tip(RedoBtn, "Str_Ctx_Redo", "Ctrl:Y");
            Tip(SearchBtn, "Str_TT_Search", "Ctrl:F");

            Tip(SplitPaneRailBtn, "Str_TT_SplitPane", "F10");
            Tip(FullScreenRailBtn, "Str_KS_FullScreen", "F11");
            Tip(DocInvertBtn, "Str_TT_DocInvert", "N");
            Tip(DocInfoRailBtn, "Str_TT_DocInfo", "F4");
            Tip(ShortcutHelpRailBtn, "Str_TT_KeyboardShortcuts", "F1");
            Tip(VersionLabel, "Str_TT_About", "F12");
            SyncSidebarToggleTip();

            Tip(ViewContinuousRadio, "Str_View_Continuous", "F5");
            Tip(ViewSingleRadio, "Str_View_Single", "F6");
            Tip(ViewTwoPageRadio, "Str_View_TwoPage", "F7");
            Tip(ViewGridRadio, "Str_View_Grid", "F8");

            Tip(ZoomOutBtn, "Str_TT_ZoomOut", "Ctrl:Minus");
            Tip(ZoomInBtn, "Str_TT_ZoomIn", "Ctrl:Equals");
            ZoomBox.ToolTip = WithoutShortcut(Loc("Str_TT_ZoomLevel")) + " (" + KeyText("%ctrl%+%scroll%")
                + " · " + GestureFor("Ctrl:Equals") + " / " + GestureFor("Ctrl:Minus")
                + " · " + GestureFor("Ctrl:D0") + ")";
            LogoBar.ToolTip = WithoutShortcut(Loc("Str_TT_AppSize")) + " (" + GestureFor("CtrlShift:Equals")
                + " / " + GestureFor("CtrlShift:Minus") + " / " + GestureFor("CtrlShift:D0") + ")";

            FilmGrainCheck.ToolTip = WithGesture(Loc("Str_Theme_FilmGrain"), "AltShift:G");
            _replaceToggleBtn?.ToolTip = ShortcutTip("Str_Replace_ToggleTT", "Ctrl:H");

            ApplyOverflowGestures();
        }

        /// <summary>The sidebar chevron's tooltip for its current state.</summary>
        private void SyncSidebarToggleTip()
            => _sidebarToggleBtn.ToolTip = ShortcutTip(
                _sidebarCollapsed ? "Str_TT_ExpandSidebar" : "Str_TT_CollapseSidebar", "F9");

        /// <summary>
        /// The overflow chevron's items carry the same right-hand shortcut column as the context
        /// menus. Tools show their letter there, as the page menu does, because the toolbar digit
        /// describes a position on a bar the item has left.
        /// </summary>
        private void ApplyOverflowGestures()
        {
            (Button Item, string Cap)[] items =
            [
                (MiText, "T"), (MiFormField, "F"), (MiUnderline, "L"), (MiHighlight, "H"),
                (MiDraw, "D"), (MiShape, "D4"), (MiImage, "I"), (MiSignature, "G"), (MiCrop, "C"),
                (MiColorCorrection, "E"), (MiMeasure, "M"), (MiRedact, "X"), (MiStamp, "S"),
                (MiUndo, "Ctrl:Z"), (MiRedo, "Ctrl:Y"),
            ];
            foreach (var (item, cap) in items)
                SetOverflowGesture(item, GestureFor(cap));
        }

        private static void SetOverflowGesture(Button item, string gestureText)
        {
            if (item.Content is DockPanel dock && dock.Children.Count > 0
                && dock.Children[0] is TextBlock existing && Equals(existing.Tag, "gesture"))
            {
                existing.Text = gestureText;
                return;
            }
            if (item.Content is not UIElement content) return;

            // Docked right in a row that fills the item, so the keys line up down the right edge
            // whatever length each translated label is.
            item.Content = null;
            var gesture = new TextBlock
            {
                Tag = "gesture",
                Text = gestureText,
                Margin = new Thickness(24, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            gesture.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            DockPanel.SetDock(gesture, Dock.Right);
            var row = new DockPanel { LastChildFill = true };
            row.Children.Add(gesture);
            row.Children.Add(content);
            item.Content = row;
        }
    }
}
