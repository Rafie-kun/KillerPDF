# Current 1.9 accuracy verification, October 19, 2026

This is an internal accuracy checkpoint, not a release pass or performance measurement. All development targets 1.9. No UI interaction was used.

## Pinned comparison

Run root: `C:/Users/steve/scratch/kp-bench-render/current-19-20261019-accuracy`.

The accepted frozen 1.9 baseline application SHA-256 is `21B1145BD7078C23B693E57D3DF123B5EF2E0F3D2414549885F3BEBF06BF0608`. The candidate application is the retained `kp-boundaries-20261019/combined/app-headless-final` payload, SHA-256 `E7331B0B1DEC048E12A24F315CE85AF036756C85941C0981ADC5FFEBB01D00EE`. Its separate engine DLL SHA-256 is `740A88A64C1BADB6EEC1C8904032439C74EB400114B928F5E832AF90367A6405`.

Before starting, tracked application and engine compilation inputs matched the retained source snapshot. The five untracked source/test additions also matched. The independently changed packaging launcher differs and is excluded from this rendering claim. This does not freeze final release packaging or authorize shipping concurrent WIP.

The existing `form-workflows-20261018/Check-ReleasePixels.py` comparison logic runs with a new output root, the explicit retained candidate and `--parallel 1`. It preserves accepted selections: page 1 for conformance and up to three pages for PDF.js, both at 512 and 2048. Its input manifest retains all 649 conformance files and all 976 PDF.js files. It compares decoded RGBA bytes, dimensions and CSV status/detail, including missing or newly produced page images.

The runner verifies four application identity files before and after every batch and input hashes after the comparison. `identity.json`, `source-manifest.json`, per-batch logs and incremental `results.json` are under the run root. A separate full payload manifest was captured after the first conformance pair; it must not be represented as a before-run measurement of every payload file.

## Completed comparisons

| Set and size | Baseline page outcomes | Candidate page outcomes | Changed image keys | Changed status/detail rows |
| --- | --- | --- | --- | --- |
| Conformance 512 | 634 OK, 15 skipped | 585 OK, 15 skipped, 49 failed | 584 | 49 |
| PDF.js 512 | 1098 OK, 17 skipped, 1 failed | 1037 OK, 16 skipped, 63 failed | 781 | 78 |
| Conformance 2048 | 634 OK, 15 skipped | 585 OK, 15 skipped, 49 failed | 585 | 49 |
| PDF.js 2048 | 1098 OK, 17 skipped, 1 failed | 1037 OK, 16 skipped, 63 failed | 780 | 78 |

Image-key differences include missing and new images, so they are not counts of shared pages with different pixels. Skips and failures remain in the denominator. All eight render batches and four comparisons completed. The runner's final input-hash verification passed. All 22 baseline and 31 candidate payload files also match the retained full manifest after completion.

At 512/2048, conformance has 535/536 changed shared images plus 49 missing images. PDF.js has 716/715 changed shared images, 63 missing and two newly produced images. These findings keep A1 open and require source-backed disposition.

## Diagnostic rejection investigation

An independent source/CSV review found that all 49 conformance failures have exactly the same diagnostic strings as the baseline's successful rows, after removing the new failure wrapper. This is not evidence of 49 newly emitted renderer errors.

The reviewed safety change in `3f45603` requires complete rendering before publication. `BatchRenderRunner` calls `PdfPageRenderSession.RenderPageForEncoding`, which classifies diagnostics through `PdfRenderCompleteness` and rejects omitted content and unreviewed diagnostics. That protection remains required.

Twenty failures share the exact page-tree recovery diagnostic. The engine emits it after validating and installing the recovered tree in `PdfDocument.PageRecovery.cs`. A narrow fix now classifies only that exact message as Recovery. Unknown diagnostics and actual omissions remain blocking. Unsupported operators, unknown filters, unresolved resources, substituted fonts and ICC fallback are not collectively waived.

The public-session regression fixture first failed for both missing-catalog and missing-page-tree cases: recovered preview pixels already matched the intact document, but encoding rejected the recovery diagnostic. After the classification fix, encoding and full-page pixels also match the intact document byte for byte. Real-session tests combining recovery with unsupported paint or a missing XObject still reject encoding, normal publication and base-page publication. All 26 focused completeness/boundary checks pass; the isolated Release build reports zero warnings and zero errors.

The original 20 corpus recovery inputs now produce 20 OK results through the actual CLI, exit 0. An unsupported image-filter negative still returns FAIL, exit 1, and preserves a preexisting output sentinel. Source and input hashes remain unchanged. Evidence is retained in `C:/Users/steve/scratch/kp-boundaries-20261019/page-recovery/`, including `red-02.log`, `green-final.log`, `corpus-20-green.csv` and `omission-negative.csv`.

The subsequent application hash is `64887713EA7CC8F59D3E219CA84254F6907B986D43B71F0E115402D7EE637525`. None of its 20 corpus outputs is pixel-exact against frozen 096a2cf. Their pixel differences still require disposition; successful export is not an A1 pass.

The recovery fix is committed locally as `ece3f9f`. It is a separate subsequent candidate and cannot retroactively change this pinned run's results. A2 strict three-oracle coverage, A3 difficult-page dispositions and A4 viewer/headless equivalence remain open. The next work is strict oracle disposition and verification of subsequent integrated candidates, before performance tuning.
