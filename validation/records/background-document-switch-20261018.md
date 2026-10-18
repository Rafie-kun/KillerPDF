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
`--macro-race` passed after their respective reproductions failed. Other document
editing handoffs remain under review.
