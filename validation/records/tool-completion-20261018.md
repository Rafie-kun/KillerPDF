# KillerPDF 1.9 tool and dialog follow-up

Local Debug build on `dev/1.9-overkill`, October 18, 2026. No release or push.

## Changes

- Macro Add step now exposes layer flattening, layer edits, attachment edits, portfolio presentation and page labels.
- Settings support layer rename, visibility and locking; attachment rename and description; portfolio presentation and metadata removal; and reusable page-label ranges. Editing restores existing values, and cancel preserves the original step. Imported actions without a matching editor remain unchanged.
- Shared dialog chrome uses the dialog caption brush, chrome text colors, subtitle size and Light/Yellow accent shadow. Dialog lists use the existing picker row template and semantic selection colors.

## Evidence

- App build: zero warnings and errors. App suite: 818 passed. Existing engine layer, attachment, collection and navigation macro suites: 24 passed.
- Isolated WPF host drove modal settings and executed the resulting macro steps through the engine dispatcher. Verified layer rename, visibility and locking; attachment rename and description; portfolio presentation and removal; page labels and layer flattening. Reopening preserved false visibility and custom page labels. Cancel and unsupported imported actions preserved their original settings.
- Attachment editor, bookmark proposal review, toolbar customization, document information, field recognition, layer editor, field presets and Macro dialog rendered in all 13 themes. Actual custom Open and Save picker entry points also rendered in all 13 themes. Retained images: `C:\Users\steve\scratch\kp-bench-render\tool-completion-20261018`.
- Visual inspection covered each of these dialog types in Dark, plus Light document information, 98SE Macros, Blood attachments and Malaise Macros. The empty preset collection exercises its empty state. Toolbar fixture uses a synthetic toolbar item. These are fixture checks, not complete user-workflow, DPI, locale or assistive-technology coverage.

## Test-host correction

The initial full-resource host instantiated the product Application subclass, allowing normal startup to create an unexpected MainWindow. A generic test window selector then drove that window and raised the scratch harness's field-count exception, which surfaced through the product crash handler. The owned test process was stopped. The corrected host uses a plain WPF Application, parses the actual application resources and selected owner chrome resources, supplies the same deterministic grain, and selects only dialogs owned by its fixture window. Macro and 13-theme fixture runs then exited successfully. Initial previews missing owner chrome and grain were incomplete visual evidence and were replaced.

## Remaining tool work

- ConvertColor, Resize and Watermark require typed settings and an explicit content-preservation policy before desktop host execution. Existing standalone tools do not supply a generic stream handler with safe defaults.
- DataMerge can generate multiple PDFs, while a macro step returns one PDF. Rename affects planned output paths. Both need output planning integrated with preview, collision handling and overwrite approval.
- The dynamic XFA corpus sample `xfa_filled_imm1344e.pdf` has 298 template fields, nine duplicate paths and only one AcroForm field. Dropping XFA or deduplicating names would lose information. Occurrence-aware template identity and dataset binding remain necessary; the explicit rejection remains in place.
- No checked-in custom OCR model or independent labeled holdout was available for replacing the remaining native fallback. OCR quality validation remains open.
- The broader desktop interaction, undo, rendering and corpus checks remain in the shared backlog.
