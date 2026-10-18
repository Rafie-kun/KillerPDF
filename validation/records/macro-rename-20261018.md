# Macro output naming

Date: 2026-10-18. Scope: local 1.9 Rename macro support and output protection.

Rename was listed as a host operation but had no desktop or CLI implementation.
The first actual-shell check failed because the picker did not show a planned,
fixed output name. The new planning and execution regressions also failed before
their respective APIs and report propagation were implemented.

## Behavior

- A Rename step stores a file-name template. `{name}` is the current stem and
  `{index}` is the input's one-based position. PDF extensions are appended.
- Every final file name is planned before execution. Names are checked for
  duplicate targets, Windows reserved devices, path separators, unknown tokens,
  invalid characters, trailing dots or spaces, and excessive length.
- Rename writes copies. All original and live working source paths are protected.
- The desktop save picker fixes the planned name while the folder remains
  navigable. Selecting another file or changing the filter cannot replace it.
- Preview shows the final paths and existing-output decisions. Canceling it
  writes nothing.
- Execution carries the chosen name through later steps and includes it in
  readable and JSON reports. Hosts verify it against the approved plan.
- The command line uses the supplied output path's folder and the macro's
  template. It prints the final target before processing.

## Verification

- Nine focused Rename tests and 28 Rename/general macro tests passed.
- Full engine suite: 6,507 passed, zero failed or skipped.
- App suite: 833 passed, zero failed or skipped.
- App and engine builds: zero warnings or errors with existing references reused.
- Actual-shell single-document picker, preview, execution and source preservation
  passed. Preview cancellation wrote no output.
- Actual-shell two-file batch selection, input ordering, fixed-name picker,
  preview and outputs passed. Each result matched its respective source bytes.
  Selecting an actual PDF row left the planned save name unchanged.
- Settings acceptance, restoration and cancellation passed in Dark, Light, SE98
  and Malaise. Dark, Light and SE98 captures were visually inspected.
- Actual `KillerPDF.exe --macro` execution returned 0 and wrote the planned
  filename with unchanged PDF bytes. An invalid template and original-source
  collision returned 2. The unused picker-style output path was not written.

The executable had to be recreated after an earlier build without an app host;
the initial missing-executable attempt did not exercise the CLI. The subsequent
actual executable checks above passed.

Evidence is retained in
`C:\Users\steve\scratch\kp-bench-render\form-workflows-20261018`:
`rename-settings-*.png`, `rename-batch-preview.png`, `rename-cli*.json`,
`recognition-source-cli-1.pdf` and unique renamed desktop/batch PDFs.
These are managed UI and routed-event checks. Native input, display scaling,
assistive technology, Data Merge macro fan-out, Resize and Color Conversion
remain separate work.
