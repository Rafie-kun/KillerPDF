# KillerPDF 1.9 release gates

Adopted 2026-10-10. Priority order: accuracy, then speed, then memory. A memory
increase of up to 10% on one workload is acceptable when it buys 5% or more on a
user-facing speed gate (S1 to S3).

Reference: the last published 1.8 release, currently v1.8.80 (3e090fa). The
frozen reference and candidate payloads, their hashes, the third-party oracle,
and every measured value live under `C:/Users/steve/scratch/kp-bench-render/release-19-baseline/`.
Working material for experiments stays under `C:/Users/steve/scratch/kp-bench-render/`;
only finished public reports belong in `validation/benchmarks/<version>/`.

1.8 renders with PDFium (native, via Docnet.Core). 1.9 renders with the managed
KillerPdf.Engine rasterizer. Gates compare the product the user experiences, not
raw rasterizer throughput on stress pages. Missing measurements are unverified,
not passes. Passing suites and completed batches do not establish visual or
interactive equivalence.

## Measurement rules

- Noise floor first: measure the candidate against itself (same payload, two
  full runs) for every speed and memory gate. A difference inside two sigma of
  that floor is "no effect."
- 1.8 and 1.9 alternate in the same session, one warmup then five measured runs
  each, nothing else running, under the `Local\KillerFamilyBuildVerification` mutex.
- Report means and medians with all samples. Never exclude a sample. Report
  speed and memory separately.
- Speed hypotheses must show 3% or better on a targeted microbenchmark before
  they earn a corpus or in-app run.
- Accuracy commits and performance commits never mix.

## Accuracy (release blocking)

| Gate | Requirement |
| --- | --- |
| A1 Regression | All 649 conformance inputs and all 976 PDF.js inputs are pixel-exact against the accepted 1.9 baseline at 512 and 2048 after every engine change. The accepted conformance CSV records fifteen skips: eleven protected inputs without supplied credentials, two missing page objects and two without recoverable indirect objects. They stay in the denominator. |
| A2 Three-oracle triage | Every shared page is rendered by 1.9, PDFium (the 1.8 payload) and pdftoppm. A page where 1.9 disagrees with both oracles beyond threshold (more than 2% of pixels with any channel delta over 32, after a 256-pixel thumbnail prefilter) is an open item. Each open item gets a source-backed disposition in `validation/records/`: defect fixed with a regression test, or accepted difference citing the PDF specification or the test suite's own documentation. Release requires zero undispositioned items. 1.9 agreeing with one oracle against the other is not an outlier. |
| A3 Difficult set | All 76 pages of the 41-file difficult set are visually dispositioned at 2048. |
| A4 Viewer equals headless | For 20 representative documents, the bitmap the viewer displays matches the headless render of the same page at the same size. |

## Speed (release blocking, measured in the built application)

Measured by `tests/KillerPDF.LatencySmoke` driving the actual Release exe of
each version through UI Automation.

| Gate | Requirement |
| --- | --- |
| S1 Cold launch to first paint | Five representative documents (text article, scanned JBIG2, CMYK brochure, form, 200-page manual). Time from process start to the first page fully painted. 1.9 at or below 1.8. |
| S2 Scroll to final quality | Scroll from page 1 to page 20 at fit-width. Time until every visited page is at final quality. 1.9 within +10% of 1.8. |
| S3 Zoom to refined | Zoom to 200% on a difficult page. Time to refined output. 1.9 at or below 1.8. |
| S4 Broad corpus raster | Headless raster sum over the 600 shared conformance pages at 512 and 2048. 1.9 within +10% of 1.8. |

## Speed (informational)

| Gate | Requirement |
| --- | --- |
| S5 Difficult set raster | Headless raster sum over the 76 difficult pages at 512 and 2048. Reported in the public benchmark as measured. Target for 1.9.0 is +50% or better at 2048, driven down in 1.9.x. Not release blocking. |

## Memory

| Gate | Requirement |
| --- | --- |
| M1 Broad corpus peak | Fresh-process peak working set over the conformance batch at or below 1.8. |
| M2 Difficult set peak | Within +25% of 1.8 at 512 and within +10% at 2048. |
| M3 Live viewer working set | After opening five documents and scrolling each to the end, within +15% of 1.8. |

## Other release requirements

- Existing 1.8 functionality and all maintenance fixes carried forward and verified.
- Release build zero errors; engine and app suites pass; dependency vulnerability
  check clean; portable and installer payloads pass their inventories.
- Release checklist from the harness: nuspec, landing copy, README, corpus pages
  refreshed from verified results.

## Status

The PDF.js count was corrected on 2026-10-18: the accepted
`triage-20261009-2153/pdfjs/v19.csv` contains 976 unique file names, exactly matching
the current source list. The previous 977 count did not represent a missing input.
The accepted conformance CSV and a fresh render of the hash-verified frozen
096a2cf application also agree on 634 rendered inputs and fifteen identical skips.
The earlier prose count of 639 and ten skips below is retained as historical
wording, not the verified denominator. This reconciliation adds no exclusions.

The October 18 candidate comparison completed at both required sizes. A1 remains
open: after the stencil, profile-cache and page-group repairs, conformance has
572/573 changed hashes at 512/2048, while PDF.js has 770/769, including two
additional renders. Source lists and render outcomes remain unchanged. See
[the accuracy checkpoint](records/release-pixels-20261018.md) for identities,
status changes and the remaining disposition work. This is not a release pass.

Current gate values are tracked in
`C:/Users/steve/scratch/kp-bench-render/release-19-baseline/BASELINE.md` and summarized
here only at release checkpoints.

| Gate | Status at 096a2cf versus v1.8.80 (2026-10-10) |
| --- | --- |
| A1 | Baseline accepted (639 of 649 exact, ten expected failures). PDF.js baseline pending. |
| A2 | Not yet run. |
| A3 | Partially dispositioned; list pending. |
| A4 | Not yet run. |
| S1 | Fails on four of five documents: painted +10% to +30% (article 2276 vs 1747 ms). Scan is 9% faster. First paint only +3% to +10%; the gap is the final-quality pass. |
| S2 | Passes on all five (within 1% on three, +7.5% on 210260, 23% faster on the scan). |
| S3 | Fails on four of five: 1.9 needs 580 to 730 ms after the last key, 1.8 about 360 ms. Altona 10% faster on 1.9. |
| S4 | Fails narrowly: shared-file raster sum +12.7% at 512, +15.9% at 2048. 1.9 renders 634 files to PDFium's 600. |
| S5 | Informational: +98% at 512, +85% at 2048. |
| M1 | Passes: 25% below 1.8 at both sizes. |
| M2 | Fails: +61% at 512 (246 vs 153 MiB), +18% at 2048 (322 vs 274 MiB). |
| M3 | Fails on the JBIG2 scan (+87%, 1208 vs 646 MiB) and altona (+21%); +5% to +8% on the other three. |

| Gate | Status at 8a93ac5 (2026-10-23) |
| --- | --- |
| A2 | No undispositioned items on either corpus; no new outlier since the dispositioned 8065c4e run. See [the A2 record](records/A2-triage-2026-10-23.md). |
| A3 | All 76 pages visually dispositioned at 2048: 48 equivalent, 23 accepted differences, 5 defects fixed. 44 strict outliers, each dispositioned. Open: spot data through image painting (two-spot palettes, DeviceN images and spot-colored image masks reaching their spot plates). See [the A3 record](records/A3-difficult-2026-10-23.md). |

The other gates keep their 096a2cf status above.

Measurement detail, noise floor and per-file contributors:
`C:/Users/steve/scratch/kp-bench-render/release-19-baseline/BASELINE.md`.

The gate log kept in this file through 2026-10-05 is preserved verbatim in
`validation/records/RELEASE-GATES-1.9-history-2026-10-05.md`.
