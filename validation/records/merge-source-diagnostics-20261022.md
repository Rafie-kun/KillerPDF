# Merge source diagnostics for issue #455

Date: October 22, 2026. Branch: `dev/1.9-overkill`. Version: 1.9.0, unreleased. Starting HEAD: `e99fe84`.

## Scope and confirmed cause

The three compatibility corrections in [issue #455](https://github.com/SteveTheKiller/KillerPDF/issues/455) already existed. This follow-up addresses the request to identify the failing input in merge errors. The live issue and its comment were read before implementation. The reporter's original PDFs remain private and were not available for testing.

The diagnosis considered three explanations:

1. The service discarded the source path when rethrowing engine errors. A failing integration test confirmed the missing name.
2. The UI or command-line handler stripped existing context. The service itself lacked the name, ruling out this explanation for the reproduction.
3. Validation happened after individual inputs were queued. Wrapping input reads and imports alone left the test failing. The reversed stitching-function bounds were rejected during `BuildImportedPage`, confirming this deferred validation path.

The fixture is a generated PDF with a real shading resource and reversed stitching-function bounds. It exercises the same resource validation path as the report without using private customer documents. Equal boundaries remain supported by the existing compatibility tests.

## Changes

- Merge and desktop insertion identify failed input reads, preparation and immediate imports, preserving the original exception as `InnerException`.
- The engine records the source document when an imported-page build fails. The original engine exception type, message and stack remain unchanged. The marker resets at the beginning of every build.
- The application maps that source back to the input filename. Repaired PDFs and converted drag/drop files retain the original selected name.
- Combined catalog/form errors and final output replacement failures do not inherit an arbitrary source filename. Cancellation and memory exhaustion are not relabeled as input failures.
- Existing merge ordering, leading-PDF byte preservation, image handling, rotations and drag/drop skipping behavior remain unchanged.

## Verification

| Check | Result |
| --- | --- |
| Initial merge regression | Failed because the actual rejected PDF's name was absent. Passed after source tracking covered deferred page building. |
| Desktop insertion regression | Failed because the rejected source's name was absent. Passed after integration. Target and source bytes stayed unchanged. |
| App diagnostic tests | 12 passed: rejected middle input, unreadable first/later inputs, repaired names, destination preservation, combined XFA failure, output-write failure and editor reuse. |
| Existing merge/insertion tests | 4 passed, including mixed PDF/image order, JPEG preservation and insertion/rotation handling. |
| Engine merge compatibility and page editor tests | 427 passed, none skipped. |
| Application payload build | Passed with zero warnings and zero errors. |
| Actual application CLI, rejected middle PDF | Exit 1; output named `report with spaces.pdf` and retained the `/Bounds values are not ordered within /Domain` reason. All three inputs and the existing output retained their SHA-256 hashes. |
| Actual application CLI, valid merge | Exit 0; reported two input files and two output pages. |
| Independent scoped review | No blocking findings. Confirmed attribution boundaries and retry reset. |

Builds used an isolated artifacts directory. The CLI was run through `KillerPDF.App.exe` from a terminal, with generated fixtures. A preliminary `dotnet KillerPDF.App.dll` launch had no attached console and yielded no captured message; that attempt was not used as message verification.

## Remaining limits

Catalog-level failures outside individual page building still have generic messages. Some combine multiple inputs, while others may eventually support more specific provenance. Issue #455 stays partial in the triage record so this remaining diagnostic scope is visible.

No private reporter PDF, interactive merge dialog, repair prompt or native drag/drop sequence was replayed. Desktop preparation call sites were reviewed and the application compiled; repaired-name behavior was tested through the service. This is local implementation and automated/CLI evidence, not release or reporter acceptance. No issue comments, closures, pushes or releases were performed.
