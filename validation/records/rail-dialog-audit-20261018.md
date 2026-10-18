# Rail, dialogs and tool exposure audit, October 18, 2026

Scope: current KillerPDF 1.9 development checkout. Product code was inspected and
rendered but not changed. This is an audit, not a complete visual or interaction
certification of every dialog.

## Rail wiring

All 14 button controls under SidebarRailGrid have actual click handlers:

| Control | Action |
| --- | --- |
| Collapse | SidebarToggle_Click |
| Pages, Outlines | OpenSidebarPanel for the corresponding panel |
| Comments, Attachments, Layers, Signatures | OpenSidebarPanel for the corresponding panel |
| Split pane | ToggleSplit |
| Full screen | ToggleFullScreen |
| Invert | DocInvertBtn_Click |
| Document information | OpenDocumentInfo |
| Shortcuts | Viewer shortcut overlay handler |
| Language, Theme | ToggleRailFlyout for the corresponding picker |

The four list panels read real comment, attachment, optional-content and signature
data through background tasks. Panel edit actions route to the corresponding
editor/report; attachment double-click saves a copy. Panel availability is gated
by an open document, with additional outline availability checks. No placeholder
rail handler was found. This was source tracing, not a click-through test of all
14 controls with a populated document in every sidebar orientation.

## Family dialog coverage

All 45 named dialog window classes other than the separate FileDialog use
DialogChrome.Configure and DialogChrome.Frame. The file picker uses its own
family XAML and theme-aware close control. PrintPreviewWindow, StampWindow,
TransformWindow and the two signature creation/image-adjustment windows also use
the shared dialog chrome. About is its established custom overlay/window surface;
native printer properties and the color-picker capture surface are special cases.

The dialog helper supplies family wordmark/subtitle, close control, frame brush,
font setup and classic 98SE treatment. This establishes shared shell construction,
not the correctness of every field, focus path or translated layout.

Attempted 135 dialog/theme cases: 45 dialog types in Dark, Light and SE98.
Thirty-eight types rendered in each theme, totaling 114 renders. Several renders
use minimal or empty fixture collections, so this is initial-state coverage.
Inspected Macros/Dark, Data merge/Light and Portfolio structure/SE98 screenshots.

Seven types were not exercised successfully by the generic fixture host:
AttachmentEditorDialog, BookmarkReviewDialog, CustomizeToolbarDialog,
DocumentInfoDialog, FormRecognitionReviewDialog, LayerEditorDialog and
TextFieldPresetsDialog. These require richer typed fixtures or main-window
resources. Their fixture failures are not classified as product bugs. Their
shared chrome was verified from source. FileDialog and the custom window
surfaces above were source-reviewed, not rendered in this run.

## Findings

1. MacroDialog's _availability TextBlock has no themed foreground. The runtime
   foreground is FF000000 in Dark, Light and SE98. The Dark render shows the
   readiness message nearly disappearing against the dark background. The same
   control displays unavailable-operation feedback, so its contrast matters.
2. Five executable engine macro operations are not offered in the desktop Add
   step list: FlattenLayers, EditLayers, EditPortfolio, SetPageLabels and
   EditAttachments. Importing a suitably configured macro can expose capabilities
   that cannot currently be authored in the desktop library. Settings editors
   also need review before adding them to the list.
3. The desktop registers host macro handlers for Save, OCR and Redact. ConvertColor,
   Resize, Watermark, DataMerge and Rename have no registered host handler. Their
   standalone tools should not be confused with support inside a macro. Imported
   unsupported steps are displayed as unavailable and block Run.
4. Export is executable in the engine dispatcher but deliberately excluded from
   desktop macro support. Do not advertise desktop macro export as wired.

These were findings at the audit checkpoint. Subsequent local completion wired
all listed host operations and settings editors. Export now saves extra files
alongside editable PDFs in desktop, batch, Data Merge and CLI macros. See
`macro-export-20261018.md`, `macro-color-20261018.md`,
`macro-resize-20261018.md` and `macro-data-merge-20261018.md` for verification.

Repeated XFA template paths and the native OCR model/quality gates remain known
tool gaps in BACKLOG.md. No new disconnected rail icon was found.

## Retained evidence

Internal run: `C:/Users/steve/scratch/kp-bench-render/rail-dialog-audit-20261018`.
Retain audit.json and theme screenshots as development evidence for this record.
Native high-DPI, long translations, populated dialog states and complete keyboard
interaction still need coverage. No product tests were rerun for this read-only
product audit, and no GitHub issues were changed.

## Populated follow-up

The seven types unavailable to the original generic host now render with actual
MainWindow resources and typed populated fixtures: attachments, bookmark review,
toolbar customization, document information, form recognition, layers and field
presets. All seven rendered in all 13 default themes (91 captures). The Dark,
Light and SE98 sets were visually inspected. Evidence is `remaining-*.png` under
`C:\Users\steve\scratch\kp-bench-render\form-workflows-20261018`.

Field recognition now labels its Add, Name and Page columns with existing keys
present in all 19 locales. Actual cancel, accept/reject, rename, Undo and Redo
checks passed after the layout change. The preview geometry remains unchanged.

The populated Light preset list exposed a local TextBrush override that defeated
the standard selected-text color. Its detail also stayed muted while selected.
A runtime assertion failed before the repair. Rows now inherit the family item
selection style and details switch to SelectionFg when selected, retaining muted
unselected details. The retained `--verify-presets` regression checks both rows
after each selection across all 13 themes, with no private-method assertions.
It passed against app SHA-256
`955BC9D93596ACDED790642E66B7554E0C38CD4A3BF7410ABA6B229F1880430E`.

The existing native UI Automation smoke also passed stamp color acceptance,
cancellation and working-document save/reopen in Dark, Light and SE98. Its
`native-ui-smoke` evidence is under the same run root. This narrows the native
gate; physical input, screen-reader operation, display scaling and complete
interaction coverage remain unverified.
