# Data Merge macro outputs

Date: 2026-10-18. Scope: KillerPDF 1.9 local macro execution.

Data Merge now creates one editable PDF per included record. Each generated
document passes separately through subsequent macro steps. Record failures stay
visible while other records continue. Names and record outcomes appear in text
and JSON reports without PDF bytes or full source records.

## Host behavior

- Add Step loads a reusable Data Merge profile through the family file picker.
  Profiles contain mappings, output templates and conditions, not source records.
- Desktop current-document and ordered batch runs select CSV, JSON or XLSX data,
  preview every final destination and existing-output decision, and protect source
  PDFs. Current-document runs use a saved snapshot including pending form edits.
- Rename steps after Data Merge use the generated document's name. `{index}`
  remains the original input's one-based position, including in batch runs.
- Both hosts check actual output names against the approved plan before writing.
  Desktop replacement is allowed only for outputs present during preview. Writes
  use temporary files, with cancellation checked before replacement.
- The CLI uses `--macro <input.pdf> <macro.json> <output.pdf> --data <records>`.
  The output argument selects the folder and supplies the default name when the
  profile uses the desktop default template. Missing data returns usage code 2.
- File-output hosts currently accept one Data Merge step per macro. More than one
  is rejected during planning, before any output is written.
- Adding Rename now opens its settings immediately, matching other configured
  steps.

## Verification

The public runner test first failed because multiple outputs were unsupported.
The record-report test then failed because child outcomes were omitted. A real
form test failed before the typed Data Merge macro operation existed. All pass
after implementation. An app planning regression also failed before planning
was implemented and now verifies later Rename steps and source protection.

- Full engine suite: 6,510 passed, zero failed or skipped on the final run.
- Full app suite: 834 passed, zero failed or skipped. Unrelated in-progress theme
  changes currently omit a test dependency on `Controls/SnapLayouts.cs`. These
  checks used a temporary test-only target that includes that actual source file;
  the existing project edits were preserved. The ordinary test build remains
  dependent on completion of those separate changes.
- Engine and app builds passed with zero warnings or errors, using existing
  dependency outputs. The final engine build included normal analyzers.
- Actual-shell current-document execution generated two independently editable
  records, applied Rename and Validate to both, and preserved the source bytes.
- Actual-shell ordered two-template batch execution produced four separate
  editable PDFs and previewed all destinations.
- Canceling the preview wrote no output. Switching documents during the data
  picker also stopped the current-document run without output.
- Actual executable CLI execution returned 0 and generated separately editable
  Katherine and Dorothy forms. It did not write the placeholder output path.
  Running without `--data` returned 2.
- Profile settings accepted and canceled correctly in Dark, Light and SE98.
  The output preview and Light/SE98 picker captures were visually inspected.

The first full engine run had one failure in the existing renderer cancellation
test. It passed alone and on the complete rerun. No rendering implementation or
cancellation test was changed; the initial failure is retained as an observation,
without claiming a confirmed cause.

Evidence is retained in
`C:\Users\steve\scratch\kp-bench-render\form-workflows-20261018`:
`merge-macro-preview.png`, `merge-macro-report.png`,
`merge-profile-picker-*.png`, `merge-macro-cli.json`, `macro-cli-records.csv`,
unique `macro-record-*.pdf` outputs and the final engine TRX report.
Checks used managed UI and routed events. Native input, assistive technology,
display scaling, editable Resize and editable Color Conversion remain separate.
