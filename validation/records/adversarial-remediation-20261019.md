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

## Pending work

BF-016 requires the actual installer reproduction in a disposable VM. At task
start, no authenticated guest PowerShell session was available, and UI control
was deferred until 5:00 p.m. Pacific on October 19. No installer was run on the
development host. Continue this item when guest access is available.

The remaining accepted findings, the three investigation items, V-001, and the
exact final release gates remain open. Existing unrelated work is preserved.
