# Macro Export verification, October 18, 2026

Export saves an extra file alongside the editable PDF in current-document,
ordered batch, Data Merge and CLI macros. Later steps continue against the PDF.
Each export captures its own step; later Rename steps determine the final stem.
Multiple exports receive numbered suffixes. Previews include every extra target
and existing-file replacement.

## Reproduction and repair

- A public dispatcher test reproduced Export replacing PDF bytes: a following
  Validate step failed. Exports now travel separately from PDF data.
- A two-record merge test reproduced blank exports despite populated fields.
  Export reads a flattened copy for visible field text. Saved PDFs keep their
  original editable fields.
- Changing the export format retains imported page selections.
- Later failures return neither PDF nor earlier artifacts for that failed input.
- Target plans protect PDFs, merge data, CLI macro files and term files. A JSON
  export targeting its own macro file returned exit 2 before writing.

## Verification

- Normal engine and app builds passed without warnings or errors.
- Full engine suite: 6,555 passed. Full app suite: 834 passed.
- Actual desktop current-document and ordered two-file batch runs saved PDF/TXT
  pairs. TXT contained page text and the filled value. PDFs matched source bytes.
  Preview cancellation wrote neither target.
- Actual desktop merge plus Rename plus Export saved independently editable
  PDF/TXT pairs for two records, with the correct value in each TXT.
- Actual executable CLI passed TXT, HTML, Markdown, JSON, DOCX, XLSX and PPTX.
  Text or package XML contained Invoice and Ada. Every paired PDF matched source
  SHA-256. The three Office packages opened as ZIP archives.
- Actual executable CLI passed two exports followed by Rename and merge with
  two record-specific PDF/TXT pairs.
- Settings restored, accepted and canceled in all 13 default themes. All 13
  rendered settings windows were inspected after compact sizing. Existing
  localized title/button resources are reused; format names use product names
  and file-format abbreviations.

## Evidence and limits

Evidence root:
`C:\Users\steve\scratch\kp-bench-render\form-workflows-20261018`.
Retain `export-engine-final`, `export-app-final`, `export-settings-*.png`,
`export-preview-*.png`, `export-cli-*`, `export-result-*`,
`export-source-ready*`, `merge-export-*` and generated record files.

These are native WPF workflows driven by an isolated diagnostic fixture, not
physical input, assistive technology, display scaling, Office interoperability
or full release corpus parity. Existing format-specific layout limits remain.
The editable PDF is kept even when another format cannot represent every PDF
feature. Files are replaced atomically one at a time. A filesystem failure can
leave a saved PDF beside a failed extra export; the failed target is reported.
