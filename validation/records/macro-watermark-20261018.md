# Desktop macro text watermark

October 18, 2026. KillerPDF 1.9.0 on `dev/1.9-overkill`.

## Behavior

The desktop Macro dialog exposes Watermark with reusable text, font size, opacity and rotation settings. It uses the existing Stamp writer to add centered gray vector text to every page. Original text, vector content and form fields remain editable. Exported steps use the same handler through `--macro`. This increment does not add image watermarks; imported unsupported settings are rejected.

Settings use invariant decimal values in the macro file and the current culture in the editor. The editor displays opacity as a percentage. Reopening restores values, and cancel leaves the original step unchanged. Empty or multiline text, invalid numeric values and unknown settings fail without a PDF result. Cancellation is checked before work, between pages and before returning output. Font availability is checked before writing.

## Verification

Tests target the macro dispatcher and saved PDF readers, using synthetic PDFs with original text, vector rectangles, two differently sized pages and an editable form field. The feature test initially failed to compile because the host API was absent. A later imported decimal-comma test reproduced numeric misinterpretation and passed after restricting macro-file parsing to invariant floating-point syntax without grouping separators.

- Thirteen watermark cases pass: content and form preservation with Latin and Cyrillic text, invalid imported settings, unknown image settings and cancellation.
- Full application suite passes 831 tests. Application build has zero warnings and errors.
- An isolated plain WPF Application loaded actual app and owner chrome resources without starting the product Application subclass. Settings acceptance, restoration and cancellation passed in Dark, Light, 98SE and Malaise. Light used German decimal-comma input and saved invariant `0.325` opacity.
- Saved vector output rendered without diagnostics at page rotations of 0, 90, 180 and 270 degrees. Visual inspection of settings and portrait/rotated output confirmed the centered translucent mark, legible source content and family styling.
- Existing locale keys cover all 19 locale dictionaries; no new translated strings were introduced.
- The actual executable initially returned exit code 2 for an exported Watermark macro, identifying the missing CLI host registration. After registration, the same command returned exit code 0 and wrote the watermarked PDF.
- A malformed watermark macro returned exit code 1 and a failed-step report. The SHA-256 of the existing output PDF stayed identical, confirming the failed command did not replace it.

Retained settings screenshots, rendered page PNGs and synthetic output PDFs are in `C:\Users\steve\scratch\kp-bench-render\macro-watermark-20261018`.

These checks do not establish complete language shaping, full desktop batch interaction, every theme/accent combination, native display scaling or broad corpus correctness. Color conversion, resize, data merge and rename remain separate macro host work in the shared backlog.
