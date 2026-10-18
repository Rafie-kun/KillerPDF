# Rendering work safeguard, October 18, 2026

The first-page regression smoke found an acyclic Form XObject graph that expands
exponentially. Existing cycle and nesting guards did not bound its repeated work.
The source `itext/kernel/src/test/resources/com/itextpdf/kernel/pdf/PdfReaderTest/exponentialXObjectLoop.pdf`
is 256,788 bytes, SHA-256
`670EE4B15E1765CCD738CBDB05C09202465B3520681ED4C8801FFFC972F2E1E3`.

## Diagnosis and repair

1. Parser delay was ruled out: parsing completed before rendering stalled.
2. A missed cycle was ruled out: the shared graph is acyclic, drawing the same
   child twice at each level. A tiny graph without fonts reproduced the problem.
3. Font-only failure was ruled out by that no-text reproduction. Glyph work still
   needed counting because repeated long strings bypass an instruction-only cap.
4. Unbounded expansion was confirmed through public renderer regressions. An
   instruction-only prototype and then combined glyph/instruction budgeting still
   let the original source exceed the bounded process deadline.

The final renderer counts instructions and glyphs together, rejecting more than
1,000,000 work units per render. It separately rejects more than 1,024 Form visits
at depth 16 or greater. Existing depth and cyclic-recovery behavior remains.
Limits produce explicit errors rather than returning a silently incomplete page.
Counters reset for every uncached render.

The original source now exits with `Nested Form XObject work limit exceeded.`
in the bounded original-file check (17,078 ms, exit 1). This elapsed value records
termination evidence, not a performance benchmark. Six public test cases cover
strict/recovery expansion, repeated long strings, the deep visit guard, ordinary
shared forms and a deep single chain. The ordinary fixtures render all red pixels
correctly on two uncached runs. The tests failed before their corresponding fixes.
The rebuilt actual `KillerPDF.exe --batch-render` also returned exit 1 within its
30-second parent deadline, reported the same error in `render-original-cli/result.csv`,
and wrote no partial PNG. This exercises the application's installed-font resolver
and viewer render path in addition to the direct engine check.

## Verification

Normal Debug engine and app builds passed with no warnings or errors. The full engine
suite passed 6,561 tests; the app suite passed 834. Tested engine SHA-256:
`949ED09723C2B07A8C6CB130BBBB961906FE60EDDC0BAB3D17E6AF243BFF00E7`.
The complete Release app build also passed, with 671 warnings from the vendored
NTwain and CoreJ2K projects and zero errors. Release app SHA-256:
`8EFBDA45607B0A173232984DD7938BE2941B685330B87F7242EEA15084CD809E`.

Both corpus sweeps render the first page at 256 pixels, using eight workers and
a 30-second per-file limit. They are internal smoke checks, not full-page pixel
parity, standards certification, release gate A1 or a public benchmark.

| Set | Before | After |
| --- | --- | --- |
| Conformance, 649 | 574 clean, 54 diagnostic, 17 skipped, 4 failed, 0 timeouts | Identical counts and diagnostic/failure signatures |
| Regression, 16,696 | 16,072 clean, 444 diagnostic, 44 skipped, 135 failed, 1 timeout | 16,072 clean, 444 diagnostic, 44 skipped, 136 failed, 0 timeouts |

Grouped diagnostic/failure signatures differ only by replacing the original
timeout with the explicit nested-work error for the same source. Successful
render counts are unchanged. Representative file ordering within grouped log
messages is nondeterministic and is excluded from that signature comparison.

Credential skips remain in the denominator. Regression failures have not all been
individually classified. The four conformance failures independently show missing
page objects or unrecoverable cross-reference/trailer damage in Poppler. Bare
engine missing-font diagnostics do not automatically establish an application bug:
the actual app resolves the inspected vertical Japanese text through installed
fonts. The inspected embedded Wingdings source is truncated and remains a separate
diagnostic rather than being reconstructed by guesswork.

Evidence is retained under
`C:\Users\steve\scratch\kp-bench-render\form-workflows-20261018`:
`regression-render-sweep.log`, `regression-render-work-budget.log`,
`conformance-render-sweep.log`, `conformance-render-work-budget.log`,
`form-expansion-repaired-original-v3.json`, `form-work-budget-engine-final`,
and `form-work-budget-app-final`.

The release document's PDF.js count was corrected from 977 to 976 after comparing
all unique names in the accepted `triage-20261009-2153/pdfjs/v19.csv` with the
current source folder. The lists match exactly; this does not waive an input.
