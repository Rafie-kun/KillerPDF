# KillerPDF 1.9 adversarial remediation

Started October 19, 2026 on `dev/1.9-overkill`, initially at
`5c53614a0bb82628e9499c18fb465609d31c404f`.
This record tracks bounded fixes. It is not a release-readiness report.

## BF-009: split source preservation

Public test boundary: the actual Release executable's headless `--split`
command, using synthetic three-page PDFs and real filesystem destinations.
The regression is `build/Test-CliSplitSafety.ps1`, with an executable path and
a new evidence directory supplied explicitly. It opens no application windows.

Ranked hypotheses after reproducing the original symptom:

1. CLI publication lacks a complete destination preflight. Exact source-name
   collisions should overwrite the input before any rejection.
2. Engine name uniqueness only covers the generated output set. Unique names
   can still collide with the source or existing files.
3. Splitting mutates the source independently of destination names. A control
   with noncolliding names would also change the source.

The original command returned 0, changed the source hash, and published extra
parts in two fresh runs. The three-page noncolliding control returned 0 and
preserved the source. Source tracing confirmed hypotheses 1 and 2; the control
rejected hypothesis 3. An initial control accidentally reused the one-page
output of the destructive reproduction and encountered an existing
"The incremental page update is empty" rejection. That is not evidence against
the original source-preservation reproduction and was not repaired here.

The repair plans both default and named output paths before writing, compares
normalized paths case-insensitively, rejects source collisions regardless of
`--overwrite`, and refuses existing files unless that option is present.
All parts are staged beside the destination before publication. Explicit
overwrites use the existing atomic-replacement service. A read handle also
protects the source from replacement through filesystem aliases during the run.

Red/green sequence:

- Exact source collision failed before the repair and passed after preflight.
- A later existing output failed the preservation assertion before the shared
  preflight and passed afterward.
- Explicit overwrite failed while existing outputs were always refused and
  passed after the opt-in was implemented.
- Additional executable cases pass for case differences, normalized paths,
  a source collision later in the planned list, default naming with and without
  overwrite, and a locked first destination. The locked-destination failure
  preserves the old bytes and removes owned staging files.
- Independent Poppler `pdfinfo` inspection confirms all six named control and
  overwrite outputs contain one page with the expected 100 by 200, 200 by 300,
  or 300 by 400 point geometry.
- Release app builds pass. The final focused build reports zero errors and
  zero warnings; the initial full build reported 671 existing codec warnings.
- All nine engine split regressions and four atomic-replacement service tests
  pass against the current source.
- The executable regression passes in both Windows PowerShell 5.1 and
  PowerShell 7. The first 5.1 attempt exposed missing exit-code capture in the
  test launcher; using an explicitly owned Process fixed the harness failure.

Current executable acceptance assembly SHA-256:
`9D2BCFB6E7C46156B029809321C378BC39D11338E15E2CDD10A27E15DEA96AD8`.
The apphost EXE hash alone does not identify the application implementation;
the regression records the companion DLL hash as well.

This is per-file publication, not a filesystem-wide transaction. An unexpected
failure after earlier files have been published can leave those complete parts.
Known path collisions are rejected before publication. Disk exhaustion,
network-share behavior, adversarial concurrent path replacement, and crash
recovery have not been certified by these local checks.

## BF-001: recoverable vector redaction

The public engine regression reproduced the original rectangle beneath its
cover. Separate failing tests confirmed that stroke width was ignored, the
verifier accepted retained vectors, and fully covered paths retained their
coordinates with a nonpainting operator. Each passed after its bounded repair.

Every partly intersecting path now requires explicit raster approval. Stroke
bounds include width, cap, join, miter, transforms, and saved graphics state.
Fully covered paths are removed, including their coordinates. Intersecting
clipping paths require rasterization. Verification inspects source paths after
excluding only the exact appended cover instructions.

Acceptance covers straight paths, outlined geometry, short and long curves,
compound paths, clipping, wide and transformed strokes, restored stroke state,
and all quarter-turn rotations. Test copies remove only the appended covers:
the original paths are absent, the selected area remains black, and pixels
outside a two-pixel boundary margin match the original at 72 dpi. These checks
inspect content and rendered pixels independently of the production verifier.

All 33 focused engine redaction cases and 25 app redaction geometry tests pass.
The full engine suite passed all 6,590 cases before the final five acceptance
cases were added; production code was unchanged afterward. Evidence logs are
in `C:/Users/steve/scratch/kp-remediation-20261019`, including
`vector-engine-suite-01.log`, `vector-acceptance-02.log`, and
`vector-app-tests.log`. No UI interaction was used.

Stroke bounds are intentionally conservative and can request rasterization
when exact subtraction might have preserved editable geometry. These synthetic
checks do not replace corpus parity, hands-on UI checks, or final release gates.

## BF-003: all-or-nothing automated redaction

The executable regression `build/Test-CliRedactionSafety.ps1` reproduced six
false-success cases: mixed standalone redaction and mixed or fully blocked
macros, each with a new and an existing destination. The existing standalone
all-blocked refusal and explicitly approved raster controls already passed.
The failing commands returned 0 and published incomplete or unchanged data.
The source branches confirmed skipped-region filtering as the cause.

Standalone redaction now refuses any unapproved raster page before publication.
The macro handler throws a step failure instead of filtering regions or
returning unchanged success. A macro containing Redact also withholds generated
merge outputs when the run fails. Usage and failure text describe the complete
output contract and report affected page numbers.

All 16 executable cases pass in Windows PowerShell 5.1 and PowerShell 7. They
check exit status, source hashes, new-output absence, existing-output hashes,
and approved success. The final script also checks the macro JSON failure and
failed-step fields. Independent pypdf extraction confirms both source secrets
are present in the fixture and absent from all eight approved outputs;
pypdfium2 renders both pages of every output with black marked centers.
The final Release build reports zero warnings and errors. Evidence lives in
`C:/Users/steve/scratch/kp-remediation-20261019/redact-cli-red-01`,
`redact-cli-green-01`, and `redact-cli-green-02` beneath the same task folder.
The checked application DLL SHA-256 is
`18690A8BFD31C12A8565B8BCDB5956C529ED12F7F1432C3DBFAAB69B94E4F318`.

Generated merge publication was reviewed in source; the executable fixtures
exercise the single-output macro path. Crash recovery and filesystem failure
after successful redaction remain part of the separate publication work.

## BF-002 and BF-010: pending marks and document history

Structural edits now serialize pending marks into the temporary working copy
before editing, so the engine moves or copies the same page objects as their
content. The resulting mark state is published to the viewer only after the
edited document reopens. Delete, insert, move, reverse, duplicate, import, and
spread-division command paths opt into this transaction.

The headless service extraction initially preserved the old behavior. Four
runtime tests then reproduced stale indexes after delete, insert, and move,
and missing marks on duplicate pages. All four pass with the repair and check
the reopened redacted content, including preservation of unrelated text.
Additional tests cover removal of a marked page, unchanged pending state after
an injected edit failure, and spread division at all four quarter turns.

The first spread implementation clipped stored marks to each half. Content
extraction exposed selected text still recoverable beyond the crop of its
other copy. The final implementation retains the full rebased mark on each
copy, clipping only its displayed overlay. All four spread tests now apply
redaction and assert that neither selected source word survives on either page.

Document undo and inverse entries capture immutable mark collections, including
coordinates, labels, and colors. Restoring a document restores marks and their
count, and mark storage counts toward the history memory budget. Redaction
failures propagate through the reload instead of recording successful history.
Two snapshot tests check isolation from later edits and independent inverse
state. Their initial failure was the absent snapshot API at compilation;
this is not a runtime reproduction of the viewer's undo workflow.

All 12 new cases and 132 related app tests pass. The Release app builds with
zero errors and 11 existing third-party NTwain warnings. Evidence logs include
`remap-red-03.log`, `remap-divide-content.log`, `remap-green-04.log`,
`remap-related-tests.log`, and `remap-app-build.log` in the task scratch folder.
Earlier test compile failures were corrected fixture API usage, not product
regressions. Live UI apply/undo/redo, counts, repaint, and cross-pane behavior
remain unverified until the UI restriction permits hands-on acceptance.

## BF-006: completed-file publication

Signing, flattening, and structured export share `AtomicFileOutput`. It writes
an owned sibling file, flushes and closes it, checks cancellation, then uses
replacement or same-volume rename. Its publication policy matches the existing
replacement helper but consumes the completed sibling directly, avoiding a
second copy. The unrelated existing edits in PdfEngineIntegration are preserved.

Three real-filesystem tests reproduced old-destination corruption after a
partial synchronous write, a partial asynchronous write, and cancellation after
an asynchronous write. All pass after staging. Additional checks cover new and
existing destinations for both write modes, locked destinations, and staging
cleanup. The locked-file fixture initially expected IOException specifically;
Windows returned UnauthorizedAccessException, so the assertion now accepts
either filesystem failure while still requiring unchanged bytes and cleanup.

All 13 focused publication and signing checks pass, including the existing
cryptographic signature integrity test. The app build passes with 11 existing
NTwain warnings and no errors. Logs are `publication-red-01.log`,
`publication-green-03.log`, and `sign-publication-build.log` in task scratch.
Network shares, unsupported replacement filesystems, process termination, and
power loss remain unverified. No UI was driven.

## BF-005: current-state signing source

The source-confirmed selection of `_originalFile` as signing input has been
replaced with an owned temporary snapshot. Edited documents use the existing
`WriteSavedCopyToTemp` pipeline for current bytes, annotations, forms, pending
marks, measurements, and rotations. An unchanged document is copied without
rewriting existing signatures. The displayed filename and suggested output
location are supplied separately from the byte source. Snapshot failure aborts
before opening the signing dialog, and closing the dialog removes its snapshot.
The editor's saved path and dirty state are not reset by this operation.

The combined Release build passes and the signing service's cryptographic
integrity regression passes through staged publication. This is implementation
and service evidence only. UI reproduction and signing after unsaved forms,
annotations, page edits, and applied redaction, plus cancel and snapshot-failure
acceptance, remain pending until UI work is allowed. No claim of completed
end-to-end signing acceptance is made here.

## BF-013: JBIG2 allocation checks

A nine-byte truncated header reproduced 1,055,144 bytes of allocation before
rejection with a moderate reference count. The maximal count was not run against
the unfixed decoder. Retention flags are now skipped without allocation, the
document uses its actual encoded length, and reference data must fit before
either reference array is allocated. Reserved count formats are rejected.

Global parsing and page decoding share the cumulative allocation scope. Header
objects and references, symbol tables and imported lists, arithmetic contexts,
halftone grids, MMR row buffers, and Huffman tables reserve space before growing.
Count arithmetic uses wide intermediates before allocation. The per-bitmap
limit is unchanged; cumulative overhead has a 1 MiB floor for small images and
the existing 64 MiB ceiling. Nested scopes charge their total consumption back
to the parent rather than applying the per-bitmap limit to that total.

The original regression now passes. The full engine suite passed 6,604 tests
with the final production change. Five further header alignment/valid-input
cases were then added. All 14 header tests passed in a child process with a
256 MiB managed heap cap and a 30-second deadline. Both page and global maximal
truncated counts produce PdfFilterException and allocate less than 128 KiB;
many small headers exhaust the configured budget, while valid long reference
tables still parse. The first bounded run found missing referenced segments in
the new positive fixtures; those fixtures were corrected before the passing run.
This cap covers the managed heap, not total process memory.

Evidence: `jbig2-header-red01.log`, `jbig2-header-green02.log` (43 checks),
`jbig2-engine-full.log`, `jbig2-final-build02.log` (zero warnings/errors), and
`jbig2-bounded02.log` under the task scratch directory. Existing image hash
regressions passed; independent final-candidate corpus rendering remains open.

## BF-004 and BF-015: current-state flatten source

Source inspection confirmed that Save Flattened serialized the working document
and overlays but omitted pending form values and whole-page rotation. It now
uses the existing save-copy snapshot shared with signing and Reduce File Size.
The snapshot writes all pending field dictionaries and font sizes, annotations,
stamps, measurements, marks, and page rotations before rendering begins. A
rasterization option retains the existing raster overlay behavior. The editor's
ordinary save path and dirty flag are untouched.

The Release app build passes with 11 existing NTwain warnings. All 29 focused
form-write, rendered print-copy, and annotation-rotation regressions pass. These
check the reused components, not the Save Flattened UI call itself. Evidence is
in `flatten-state-tests.log` and `flatten-state-build.log`. Full field-type and
rotation acceptance through Save Flattened, independently rendered output, and
editor dirty-state checks remain pending until UI testing is allowed.

## BF-008: JPEG placement eligibility

Six eligibility regressions reproduced false passthrough for horizontal and
vertical reflection, half and quarter turns, and skew. Passthrough now requires
the effective image matrix to match canonical placement relative to the crop
origin. The CLI additionally rejects passthrough when its separate rotation
snapshot is nonzero. Upright JPEG payloads remain unchanged, including an image
placed exactly at a nonzero crop origin. All 13 focused engine checks pass.

A real asymmetric four-color JPEG was flattened through the CLI at matching
72 DPI, then source and output were independently rendered by PDFium. The old
app produced mean absolute channel error 191.167 for horizontal reflection.
With the fix, all 10 fixtures pass the 12/255 error threshold and exact rendered
dimension comparison. Errors: upright 0; reflections and half-turn about 2.29;
quarter turns about 2.20; skew 7.459; native 90/180/270 rotations 2.279 to 2.299.
Quarter-turn native pages retain their 48 by 96 dimensions. Outputs contain no
interactive form fields. The nonzero differences include JPEG re-encoding and
renderer sampling; this is a bounded fixture comparison, not corpus parity.

Evidence: `jpeg-placement-red01.log`, `jpeg-placement-green02.log`,
`jpeg-app-build.log` (11 existing warnings, no errors), and
`jpeg-external-green01.log`. The scratch `check-jpeg-flatten.py` owns the fixtures
and independent comparison. GUI acceptance still requires the actual Save
Flattened interaction; its shared eligibility check is covered by engine tests.

## BF-007: render completeness before publication

CLI fixtures with a missing image resource and an unsupported paint operator
both reproduced exit 0 and replacement of existing outputs. A standard-font
resource recovery fixture remained usable. After the repair, all six cases
pass: omissions return exit 1 without creating or replacing output; recovered
standard-font rendering succeeds. Evidence: `render-omissions-red01.log` and
`render-omissions-green01.log`.

The application boundary now carries structured information, recovery,
degradation, and omitted-content diagnostics alongside pixels. Existing engine
text diagnostics are adapted centrally; unknown messages remain blocking until
reviewed. Full-page publication, OCR base images, encoding, print preview,
printing, separation preview, and exact comparison require complete rendering.
JPEG passthrough is validated by rendering before reusing its encoded payload.
The fitted viewer path retains pixels and diagnostics. Warning metadata follows
cached bitmaps into primary, continuous, secondary, and refinement presentation,
using the existing page-error status and tooltip text.

All 38 focused boundary, completeness, render-dimension, retained-session, and
rendered form-copy checks pass. The source-boundary tests initially could not
find the repository from external build artifacts; their root discovery now
starts from the compiled source file location. The final app build passes with
no warnings or errors. Evidence: `render-final-tests.log` and
`render-final-build04.log`. Live viewer/preview warning acceptance and physical
printing/OCR workflow acceptance remain pending. This does not certify every
engine recovery message or final-candidate corpus rendering.

## Pending work

BF-016 requires the actual installer reproduction in a disposable VM. At task
start, no authenticated guest PowerShell session was available, and UI control
was deferred until 5:00 p.m. Pacific on October 19. No installer was run on the
development host. Continue this item when guest access is available.

The remaining accepted findings, the three investigation items, V-001, and the
exact final release gates remain open. Existing unrelated work is preserved.
