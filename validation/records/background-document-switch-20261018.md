# Background document switching checks

Date: 2026-10-18. Scope: local 1.9 desktop workflows, not release certification.

The isolated actual-shell fixture in
`C:\Users\steve\scratch\kp-bench-render\form-workflows-20261018` loads the
compiled MainWindow and application resources with startup and settings isolation.
It invokes existing workflow entry points and reads saved PDFs through the engine.
Public `OpenFromExternal` reproduces a second launch arriving during work.

## Recognition

Before the correction, opening a blank second PDF while recognition awaited its
worker and accepting the first PDF's fields modified the second PDF. The saved
second PDF failed the assertion that it contained no form widgets.

The hypotheses were stale active-document selection after the await, missing
origin binding during review, and history targeting the active tab. Reproduction
confirmed that the workflow used the active document after asynchronous work.
The workflow now checks the originating viewer, tab session and document before
showing review and again before applying accepted results.

Both worker-time and review-time external-open cases pass. The replacement PDF
has no form widgets. Existing recognition acceptance, rename, rejection, cancel,
undo and redo checks also pass in the combined form workflow fixture.

## Transform

Opening another PDF at Transform's first UI yield reproduced an error dialog.
Source inspection showed that subsequent rendering, history and replacement
would use the new active tab. The workflow now checks its origin after each
yield and protects its deferred page selection. Its own annotation-baking
document reload is retained as the expected source for subsequent checks.

An ordinary one-page transform then exposed an independent failure: the merge
helper called Build without edits when given one replacement document. The
new `ReplaceAllPagesAndCompact_AcceptsSinglePageDocument` regression failed with
`The incremental page update is empty` at MergeDocuments before the fix. A
single valid document now returns its bytes without building an empty update.

The regression passes. The actual-shell 90-degree transform completes with one
raster page at 201 by 240 points, allowing the existing one-pixel raster rounding.
The interrupted case retains the blank replacement PDF at 400 by 300 points,
with no image and no error dialog.

## Export source protection and cancellation ownership

The production layout fixture accepts N-up settings for the first document,
opens another PDF from the layout preview, then selects the original source as
the export destination. Before the fix, the original source bytes were replaced.
The overwrite check had read paths from the newly active tab. It now retains the
source paths and suggested file name before background work. A source switch
during the initial settings dialog also stops the export.

The macro fixture switches documents from the confirmation dialog. Before the
fix, a Save-only macro exported the second document under the first document's
plan. Source checks now surround the output picker, confirmation, OCR readiness
and redaction preparation. Cancellation is checked before writing the output.

A separate actual-shell case starts a newer cancellable operation while the
older layout preview is open, closes that preview, then raises the actual Esc
key event and accepts its cancellation prompt. Before the fix, the older
workflow's finally block cleared the newer registration and Esc could not cancel
it. Completion now requires the originating token across every shell caller and
the OCR host interface. The same fixture now observes the newer token canceled.
This checks routed key handling, not a physical keyboard.

## Form, repair and scan handoffs

The following actual-shell reproductions failed before correction:

- Accepting form flattening after an external open removed the second PDF's
  editable field. It now retains the field and its Grace value.
- Accepting XFA conversion after an external open continued against the second,
  non-XFA PDF and showed an error. It now leaves that PDF intact without error.
- Accepting document-language repair after an external open applied the language
  to the second PDF. Input and final review now retain their originating source.
- Accepting a form-description edit after an external open changed the second
  PDF's matching field. Target, input and final repair review now check origin.
  Reading-order, table-header, metadata and output-intent handoffs use the same
  origin check; their ordinary workflows remain covered by the combined fixture.
- Accepting XFDF import after an external open changed the second PDF's matching
  field value. Import now checks its source after file selection and review.
- Orientation correction rotated the second PDF. This required checking the
  actual saved-copy path, because an exchange snapshot omits viewer rotations.
  The corrected interrupted case leaves its rotation at zero. The ordinary
  sideways-text case rotates to 90 degrees, undoes to zero and redoes to 90.
- Straightening replaced the second PDF's synthetic tilted scan. The corrected
  interrupted case preserves its exact bytes. Ordinary straightening replaces
  the tilted image; undo restores the original 800-pixel image and redo restores
  the corrected image.

Source checks use viewer, tab session and document identity. The guarded editing
paths stop after an external open. Their own annotation-baking reload remains
the expected source for subsequent processing.

## Verification and limits

- App build: zero warnings and errors with existing references reused.
- App tests: 832 passed, zero failed or skipped.
- Four isolated cases passed: `--recognition-race`,
  `--recognition-review-race`, `--transform-race`, `--transform-normal`.
- The combined desktop form, XFA, accessibility, ordered macro and data merge
  fixture passed after the recognition change.

The shell race cases are retained diagnostic fixtures, not private-method unit
tests added to the suite. A stable public workflow automation seam is still
needed for permanent shell concurrency coverage. These checks do not establish
native keyboard behavior, screen-reader support, all themes or display scales.
Additional isolated cases `--production-race`, `--operation-race` and
`--macro-race` passed after their respective reproductions failed. Further cases
passed: `--flatten-race`, `--xfa-race`, `--repair-race`, `--repair-input-race`,
`--description-race`, `--import-race`, `--orientation-race`,
`--orientation-normal`, `--straighten-race` and `--straighten-normal`.
## Model-backed OCR handoffs

The installed English Tesseract model was copied into the isolated fixture's
data directory. No model was downloaded and normal application settings were
not changed.

The first blank-target repro exposed a separate empty-update failure, so it did
not establish source binding. With readable text in the second PDF, the actual
Searchable PDF workflow recognized two words and saved the second PDF under the
first PDF's suggested source name. That failed the expected source geometry.

The OCR host now captures document origin before model readiness and checks it
after readiness and the output picker. Searchable PDF, Scan to Text and text
export stop without output when the picker changes the active document. Page
and region OCR use the same origin check around model readiness.

The zero-word failure was independently reproduced at the public
PdfOcrSearchableTextWriter.Write seam. A blank page attempted to build an empty
incremental update. With no written words, the writer now returns the original
document bytes after validating the inputs.

The following actual-shell checks passed with the real English model:

- `--ocr-source-race`, `--scantext-source-race` and `--extract-source-race`:
  changing the document in the picker produces no output or completion dialog.
- `--ocr-normal`: one page and two recognized words saved successfully.
- `--ocr-empty`: one blank page and zero recognized words saved with exact
  original bytes.
- `--ocr-worker-race`: opening another PDF after the worker starts preserves the
  first PDF's output geometry and the replacement PDF's live geometry.
- `--ocr-cancel`: a routed Esc event opens the actual cancellation prompt during
  processing. Accepting it preserves a preexisting destination's exact bytes.

The first cancellation driver ran before the busy operation existed, then a
revised driver blocked its own modal handling. Those were fixture errors. The
passing driver waits for the actual busy overlay, posts Esc, and handles the
prompt on a subsequent dispatcher tick. This is routed key verification, not
physical keyboard input.

Final regression checks after the zero-word correction: 10 focused OCR writer
tests, 6,498 full engine tests, and 832 app tests passed. App and engine builds
reported zero warnings and errors with existing references reused.
