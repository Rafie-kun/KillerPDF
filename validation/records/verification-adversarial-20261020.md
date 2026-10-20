# KillerPDF 1.9 fix verification and adversarial review

Date: October 20, 2026. Checkout: `KillerPDF`, branch `dev/1.9-overkill`.

## Scope and result

Reviewed the 18 fix commits in `90df881..8354268`, plus the initially uncommitted
Reduce File Size replacement, selection-indicator alignment, and PDF 2.0 metadata
changes. This is a review of that bounded change set, not a new review of every
change since the last 1.8 release.

Separate Bug-finder, Defender, and Referee workers inspected the code. Final
result: four valid findings, zero rejected findings, and one item needing
investigation. Two valid findings were also reproduced by the coordinator.
The changes are not cleared for release.

Local commits created:

- `e7bd88b`: preserve existing optimized output when staging is denied (#433).
- `5ab0a2b`: center selection indicators on theme font capitals.

The PDF 2.0 metadata changes remain uncommitted because BF-003 and BF-004 prevent
calling that work verified. No product code was changed to remediate review
findings. No push, PR, release, installer execution, or external message occurred.

`Shell/SidebarLayout.cs` and its new changelog entry appeared during verification
and were excluded as concurrent work. Existing untracked October 19 review
artifacts and the October 20 speed report were preserved without committing them.

## Executed checks

| Check | Result |
| --- | --- |
| Release application build, `--no-restore` | Pass, zero warnings and zero errors in the final build |
| Release application suite | 960 passed, zero failed, zero skipped |
| Release engine suite | 6,673 passed, zero failed, zero skipped |
| UI smoke tooling Release build | Pass, zero warnings and zero errors |
| Synthetic PDF fixture through app services | Parse, render, save, and reopen passed |
| `tests/Test-CliOutputSafety.ps1` | All checks passed: same-path refusal, case-insensitive refusal, source preservation, successful extraction, temporary-file cleanup, export and form-data export |
| Output staging permission failure | Passed with a disposable directory ACL: existing-file overwrite was allowed, sibling creation was denied, and `ReplaceFileAtomically` retained the original bytes |
| Radio layout regression | All four scales passed in the app suite |
| Compiled radio template rendering | Inspected Dark, Light, and SE98 at 100%, 125%, 150%, and 200%; no obvious dot displacement or clipping |
| Corrupt compressed XMP probe | Failed as expected for BF-003: `PdfFilterException` escaped document information reading |
| Difference blend inside a tiling pattern | Failed as expected for BF-002: center BGRA was `255,255,0,255` (cyan), expected `0,0,255,255` (red); no renderer diagnostics |

The suites ran against the working tree including the uncommitted metadata
candidate. Passing suites did not cover the two counterexamples above. Test
compilation emitted existing third-party warnings; the final application build
was clean. Rendered radio inspection does not establish all-theme, checkbox,
language-fallback, physical-DPI, or interactive accessibility coverage.

Built application SHA256:
`EE5CC822A0F0E7C572FC7165EA9CFBB2ACE780E1EA2EB9AFE1633E8924D9F470`.

Built engine SHA256:
`6C85780A5B0EDBF931B1647B512AEB1FBEFACF852107168D0E3869B8954D8A12`.

## Findings

### BF-005: Uninstall paths can expand outside the owned directory

Severity: high. Defender: confirmed. Referee: valid. Evidence: source inspection.

`Services/InstallPayloadOwnership.cs:83-85` embeds literal full paths in `del`
and `rmdir` commands. `App.xaml.cs:2148-2155` executes the resulting batch.
Launcher path validation at `Packaging/KillerLauncher/Program.cs:425` does not
reject unresolved percent-delimited environment-variable names.

A literal directory such as `C:\Apps\%UNDEFINED_KPDF_NAME%\KillerPDF` can remain
unchanged during .NET environment expansion, then change during batch execution.
Quoting does not prevent percent expansion. The uninstall commands can therefore
target matching files in a sibling directory outside the validated owned root.
No destructive reproduction was attempted.

Recommended correction: use a deferred-deletion helper that handles literal paths
and revalidates ownership immediately before deletion. Verify shell-sensitive
directory names in a disposable VM.

### BF-002: Backdrop optimization misses patterns and Type3 glyphs

Severity: medium. Defender: confirmed. Referee: valid. Evidence: source inspection
and a synthetic tiling-pattern render.

`engine/KillerPdf.Engine/Rendering/PdfPageRenderer.cs:2777-2798` follows `gs` and
Form `Do` operators when deciding whether to isolate a page transparency group.
It does not inspect tiling-pattern or Type3 glyph programs, although those execute
against the current drawing surface near lines 1366 and 1456.

Reproduction: a 10-by-10 page with a DeviceRGB transparency group paints a
colored tiling pattern using `/Pattern cs /P1 scn 0 0 10 10 re f`. The pattern
sets `/BM /Difference` and fills the tile red. The resulting center pixel was
cyan instead of red because the optimization used white as the initial backdrop.
The Type3 path is source-confirmed but was not independently rendered.

Recommended correction: traverse invoked pattern and glyph streams with correct
resource context and cycle protection, or conservatively isolate those pages.
Add rendered regression fixtures for both paths.

### BF-003: Damaged optional XMP breaks document information

Severity: medium. Defender: confirmed. Referee: valid. Evidence: reproduced.

`engine/KillerPdf.Engine/Documents/PdfDocumentInformation.cs:58` catches only
`InvalidOperationException`. The new XMP reader calls `PdfStreamDecoder.Decode`
at `PdfXmpDocumentMetadata.cs:69`, which can throw `PdfFilterException`.

A valid one-page PDF with `/Info << /Title (Valid title) >>` and a catalog
metadata stream declaring `/Filter /FlateDecode` with the six bytes `broken`
now throws instead of returning the valid title and page count. The exception
was reproduced against the built engine.

Recommended correction: handle the decoder's expected failure at the optional
metadata boundary, and add this fixture as a regression without hiding unrelated
failures.

### BF-004: PDF 2.0 metadata migration loses trapping status

Severity: medium. Defender: confirmed. Referee: valid. Evidence: source inspection.

`engine/KillerPdf.Engine/Documents/PdfXmpDocumentMetadata.cs:12-49` has no trapping
field or migration. `Writing/PdfVersion2Upgrade.cs:74-81` removes all Info fields
except dates. Existing XMP without `pdf:Trapped` therefore loses an Info
`/Trapped /True` value during upgrade. `PdfDocumentInformation.cs:74` also reads
trapping only from Info, even when newly generated XMP contains the value.

Recommended correction: read and migrate XMP trapping status, use it as the
document-information fallback, and test existing/new XMP with all trapping values.

### BF-001: Office timeout ownership needs validation

Severity: high if reachable. Defender: disputed. Referee: needs investigation.

`Services/OfficeConversion.cs:149` records an Office PID before the document-count
checks at lines 154 and 169 can mark the instance unowned. That later ownership
decision does not clear the stored PID. The timeout path can still call
`EndOwnedProcess` and `Kill` at line 124.

The bookkeeping inconsistency is confirmed, but the proposed user-data-loss
scenario assumes the production `Activator.CreateInstance` call returns a
preexisting hidden instance with open user documents. That activation behavior
was not established. No real Office process was launched or terminated.

Required next check: use a disposable Office VM with hidden Word and Excel
instances containing unsaved documents, force a timeout, and record process
identity and document survival. Also verify an injected unowned instance cannot
be selected for termination. Record ownership only after every ownership check.

## Reported merge hang

The supplied screenshot reports a merge hang in v1.8.91, with no input PDFs or
exact reproduction steps. Only the corresponding 1.9 code was inspected.
`Shell/FileOperations.cs:1024-1068` performs validation, page inspection, and
save/import work synchronously on the UI thread for ordinary valid inputs.
Expensive files can therefore make the window unresponsive.

This does not establish the reporter's root cause, an infinite loop, or a
deadlock. Reproducing the report requires the input files, merge order, and exact
action. No 1.8 checkout was inspected or modified.

## Remaining acceptance boundaries

The existing `validation/RELEASE-GATES-1.9.md` remains authoritative. Full corpus
pixel disposition, viewer/headless parity, interactive speed and memory gates,
signed installer/update acceptance, and actual Office timeout behavior were not
certified by this run. The rendering regression specifically prevents accepting
the recent backdrop optimization as visually equivalent.

Harness cleanup passed with no outstanding items. Automatic approval review
rejected deletion of `C:\Users\steve\scratch\kp-verify-20261020` as "blocked by
policy." Temporary scripts, test logs, fixtures, and images remain there.
