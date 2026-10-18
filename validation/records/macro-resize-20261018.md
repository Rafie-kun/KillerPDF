# Editable macro page resizing

Date: 2026-10-18. Scope: KillerPDF 1.9 proportional Resize macro support.

Resize scales pages by a configured percentage while retaining selectable text,
images and editable PDF fields. It scales page boxes, form rectangles and default
font sizes, covered annotation coordinates and border styles, and local bookmark
and named-destination coordinates. Destination zoom values remain unchanged.
The desktop editor uses the existing family input dialog and localized labels.
The same handler runs in current-document, batch and CLI macros, including after
Data Merge, where every generated PDF is resized independently.

XFA and rich-text layouts are rejected before any output is written. Their XML
or styled-text formatting is not silently flattened or left with mismatched
coordinates. A 100% resize preserves the original bytes.

## Verification

- Eight new public-seam tests cover retained text and form editing, scaled field
  fonts, dimension-line coordinates and stroke width, named and rectangular
  bookmark destinations, XML-layout rejection, and resource-name collisions.
- The field-font regression first showed 12 points after a 50% resize instead of
  the required 6 points. Navigation and line-coordinate regressions also failed
  before their geometry was updated. An ordinary font resource named `RV` exposed
  an overbroad rich-text guard; it and unrelated catalog metadata now remain valid.
- A public renderer comparison of page text, a filled form and ink was pixel
  identical at the same fitted output dimensions before and after a 50% resize.
- Data Merge integration verifies separate resized pages while preserving each
  record's editable field value and subsequent Rename behavior.
- Final engine suite: 6,518 passed, zero failed or skipped. The normal analyzer
  build passed with zero warnings or errors.
- Ordinary app suite: 834 passed, zero failed or skipped. The app build passed
  with zero warnings or errors.
- Actual desktop execution saved a 150 by 200 point PDF from a 300 by 400 point
  source, retaining selectable text and the filled field. Source bytes stayed
  unchanged, and preview cancellation wrote no output.
- Actual `KillerPDF.exe --macro` execution returned 0. The saved output retained
  the new page size, selectable text and editable field value.
- Settings restored, accepted and canceled correctly in Dark, Light and SE98.
  Dark and SE98 captures were visually inspected. Both labels exist in all 19
  application locales.

The settings fixture originally loaded theme dictionaries without updating
ThemeManager.Current. That made a 98SE capture misleading. It now uses the public
ThemeManager.Apply path in isolated startup-smoke mode. Resize, Data Merge and
Rename settings were rerun through that path; the corrected 98SE capture shows
the expected raised buttons. Earlier captures at the same evidence paths were
replaced by these verified captures.

Evidence is retained under
`C:\Users\steve\scratch\kp-bench-render\form-workflows-20261018`:
`resize-settings-*.png`, `resize-macro-preview.png`, unique `resize-copy-*.pdf`
files, `resize-cli.json`, `resize-cli-output.pdf`, and `resize-engine-final/*.trx`.
These checks use managed UI and routed events. Native input, assistive technology
and display scaling remain separate validation.
