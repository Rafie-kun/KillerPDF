# KillerPDF 1.9.0 corpus benchmark

Test dates: 2026-10-13, 2026-10-15 and 2026-10-28

The [release candidate run on 2026-10-28](#release-candidate-run-on-2026-10-28) is the current result. Against the released 1.8.91, the frozen 1.9.0 candidate saves 401 more PDFs and loses none, finishes standards and stress about 12% sooner and regression 3.4% sooner, uses 4% to 7% less CPU time, and peaks 37% to 67% lower in memory. The earlier runs below are kept for the record.

In the 2026-10-13 run, KillerPDF 1.9.0 (development build) saved 108 more PDFs than KillerPDF 1.8.90 across the three normal collections: 109 inputs that 1.8.90 skipped or failed now saved, and one stress input that 1.8.90 saved failed. Its regression and standards timings were above the 10% investigation threshold. Both builds received one warmup and five measured passes per collection in alternating order.

A [follow-up run on 2026-10-15](#follow-up-run-on-2026-10-15) with a newer 1.9.0 build resolves both items. That stress input now saves, so 1.9.0 saves 109 more PDFs than 1.8.90 and loses none. Every timed collection is now under the threshold, stress runs 7.62% faster with 6.20% less CPU time, and 1.9.0 used 38% to 57% less peak memory than 1.8.90. The damaged-file gate had zero crashes and zero timeouts in both runs, and its outcomes are identical to 1.8.90.

## Release candidate run on 2026-10-28

The frozen 1.9.0 candidate, source commit `63f0cc8ec132f18768c4c6767b62434961a53f6a`, was measured against the released 1.8.91, tag `v1.8.91` at `4856875`. Both were built on the same machine with the same Release publish flags.

| Collection | Inputs | Saved | Skipped | Save failures | 1.9.0 median seconds |
| --- | ---: | ---: | ---: | ---: | ---: |
| Public regression | 16,696 | 15,311 | 1,232 | 153 | 230.354 |
| Standards and color | 649 | 393 | 235 | 21 | 6.318 |
| Private stress | 29,599 | 25,260 | 2,957 | 1,382 | 573.531 |

Across the three normal collections 1.9.0 saves 40,964 inputs against 40,563 for 1.8.91: 400 inputs that 1.8.91 failed now save, one input that it skipped now saves, and no input that 1.8.91 saved fails. 1.8.91 produces the same outcomes as 1.8.90.

| Collection | Measured passes | Measure | 1.8.91 median | 1.9.0 median | Change |
| --- | ---: | --- | ---: | ---: | ---: |
| Public regression | 5 | Seconds | 238.395 | 230.354 | 3.37% shorter |
| Public regression | 5 | CPU seconds | 135.094 | 129.828 | 3.90% less |
| Public regression | 5 | Peak working set MB | 2,085.3 | 693.4 | 66.75% less |
| Standards and color | 5 | Seconds | 7.179 | 6.318 | 11.99% shorter |
| Standards and color | 5 | CPU seconds | 8.297 | 7.984 | 3.77% less |
| Standards and color | 5 | Peak working set MB | 1,128.8 | 706.8 | 37.38% less |
| Private stress | 1 | Seconds | 650.677 | 573.531 | 11.86% shorter |
| Private stress | 1 | CPU seconds | 293.844 | 274.609 | 6.55% less |
| Private stress | 1 | Peak working set MB | 1,992.0 | 1,165.0 | 41.52% less |

- Standards and regression received one warmup per build and five measured passes in alternating order. Stress received one measured pass per build, so its timing is a single comparison rather than a median of five.
- Every measured pass of each build produced an identical per-file outcome hash.
- Of the 401 newly saved inputs, 284 had a trailer `/Root` or other reference to a freed object, which 1.9.0 repairs while reporting that missing content was not recovered, and 110 had a signature field whose value dictionary omits `/Type /Sig`. The other 7 referenced objects missing from the file (6) or used a Brotli-compressed stream (1).
- The damaged-file folder was included, but its batch only selects files with a `.pdf` extension, which covered 2 of its 80 inputs; both builds skipped both. The 80-input gate with a per-file timeout last ran on 2026-10-15 and produced no crashes or timeouts.
- Two earlier series that day, on candidate commits `2a91dae` and `a8b4323`, produced the same per-file outcomes. Their regression timings were slower than this series, before later write-path changes; the committed run data is this final series.
- Host load was not certified idle, and no cache flushing was performed. Peak working set is sampled every 200 ms.
- The committed [run data](benchmark-freeze-20261028.csv) holds every pass. Full per-file logs remain outside the application repository at `C:\Users\steve\scratch\kp-bench-render\release-1.8.91-vs-1.9-freeze3-20261028`.

## Results

| Collection | Inputs | Saved | Skipped | Save failures | 1.9.0 median seconds |
| --- | ---: | ---: | ---: | ---: | ---: |
| Public regression | 16,696 | 15,214 | 1,232 | 250 | 315.899 |
| Standards and color | 649 | 377 | 235 | 37 | 7.079 |
| Private stress | 29,599 | 25,080 | 2,957 | 1,562 | 700.286 |
| Damaged-file safety | 80 | 0 | 78 | 2 | Not timed as a batch |

The three normal collections contain 46,944 inputs, with 40,671 saved, 4,424 skipped, and 1,849 save failures per pass. Including the separate safety collection gives 47,024 inputs.

These are hostile collections, not a zero-failure workload. All normal batch processes returned exit code 1 because their logs contain save failures. The runner completed normally; this result is based on the recorded per-file results, not on treating the batch exit code as success.

## Comparison with 1.8.90

| Collection | 1.8.90 median seconds | 1.9.0 median seconds | Change |
| --- | ---: | ---: | ---: |
| Public regression | 283.235 | 315.899 | 11.53% longer |
| Standards and color | 6.314 | 7.079 | 12.12% longer |
| Private stress | 649.38 | 700.286 | 7.84% longer |

In this run the regression and standards changes were above the 10% investigation threshold. The follow-up run below measures them again after the fixes. In every collection the slowest 1.8.90 pass is faster than the fastest 1.9.0 pass (regression 293.91 against 309.874, standards 6.479 against 6.94, stress 677.409 against 697.526), so the difference is not session noise. Stress is below the threshold.

Per-file outcome transitions from 1.8.90 to 1.9.0, computed from the per-pass logs:

| Collection | Same | Fail to save | Skip to save | Save to fail |
| --- | ---: | ---: | ---: | ---: |
| Public regression | 15,171 saved, 1,232 skipped, 250 failed | 42 | 1 | 0 |
| Standards and color | 377 saved, 235 skipped, 37 failed | 0 | 0 | 0 |
| Private stress | 25,014 saved, 2,957 skipped, 1,561 failed | 66 | 0 | 1 |

Three regression files and one stress file kept their status but have different diagnostic detail text.

The one input that regressed is `pdf-association/poppler/poppler-81748-0.pdf` in the private stress collection. 1.8.90 saved it, and 1.9.0 reported a save failure with the detail "The signature /M value is not a valid PDF date". The date ends in Z followed by an offset (`Z00'00`), which ISO 32000-1 allows. 1.9.0 now accepts that form in signature, document information and attachment dates and in the writer's date check, and the follow-up run saves the file.

Damaged-file results match: both builds produced 78 skips and the same two reported failures, with no file in the 80-file set producing a different outcome between builds.

## Follow-up run on 2026-10-15

Batch resave now opens each file once instead of twice, and full rewrites allocate about a fifth less. After those changes, 1.9.0 was rebuilt as a Release payload from source commit `c7bd0210aacaa568299bb76c08c79c7102cd7b8a` (executable SHA-256 `B94EF8A44F9FDB067B2708E484F6E6319ABCD68552FCD611A1210B614985ABA7`). It was measured against the same frozen 1.8.90 payload, whose 21 files still match their recorded hashes. Regression and standards received one warmup and five measured passes per build in alternating order. Stress and the damaged-file gate ran once per build.

| Collection | Measure | 1.8.90 median | 1.9.0 median | Change |
| --- | --- | ---: | ---: | ---: |
| Public regression | Seconds | 321.973 | 327.581 | 1.74% longer |
| Public regression | CPU seconds | 150.453 | 140.750 | 6.45% less |
| Public regression | Peak working set MB | 1,602.5 | 691.4 | 56.85% less |
| Standards and color | Seconds | 6.968 | 6.304 | 9.53% shorter |
| Standards and color | CPU seconds | 8.344 | 7.938 | 4.87% less |
| Standards and color | Peak working set MB | 1,136.9 | 705.2 | 37.97% less |
| Private stress | Seconds | 676.155 | 624.659 | 7.62% shorter |
| Private stress | CPU seconds | 328.109 | 307.781 | 6.20% less |
| Private stress | Peak working set MB | 2,116.9 | 1,071.7 | 49.37% less |

- Regression and standards are both under the 10% threshold. Regression pass times overlap between the builds (1.8.90 from 314.470 to 360.820 seconds, 1.9.0 from 288.683 to 357.053), so its 1.74% difference is within session noise. Host CPU load was about 56% before the run because other work was running, so the host was not certified idle.
- Stress first ran once per build, and that pass showed 11.51% more CPU time for 1.9.0. Profiling traced most of the slowest stress saves to object stream reads that scanned every cross-reference entry of every revision. Source commit `76848ffdcf8d5a8bf3fcf8bb9e7933fa28ee2a77` limits that scan, and the stress rows above come from a full series on that build: one warmup and five alternating passes per build, app DLL SHA-256 `E212E0AC63BCAC548B118760651431DF664966E5EB38F92B91DB55B5F6302B19` and engine DLL SHA-256 `BC0654FED696590509655E0C44421FC1FEA376BC137E89276EF5B1D5A84255EA`. Every 1.9.0 stress pass (620.156 to 644.205 seconds) was faster than every 1.8.90 pass (654.077 to 701.352 seconds).
- All five measured passes of each build produced identical per-file outcomes. Regression and standards outcomes match the 2026-10-13 1.9.0 run. One regression input, `pdfjs/test/pdfs/poppler-395-0-fuzzed.pdf`, still fails, and it now reports its broken cross-reference instead of the signature date.
- In stress, 1.9.0 saves 66 inputs that 1.8.90 fails and loses none: 25,081 saved against 25,015. Across the three normal collections 1.9.0 saves 40,672 inputs against 40,563 for 1.8.90.
- The damaged-file gate ran all 80 files for each build, alternating per file with a 30-second timeout. Both builds produced 78 skips and the same 2 failures, with no crashes or timeouts. The slowest run took 0.589 seconds.
- Peak working set is the largest value sampled every 200 ms during a pass, so a spike shorter than that can be missed. One 1.8.90 regression pass peaked at 2,318.6 MB and the other four between 1,419.0 and 1,671.8 MB. All five 1.9.0 regression passes peaked between 689.6 and 691.7 MB.
- The committed [follow-up run data](benchmark-followup.csv) holds every pass, with the source commit of each 1.9.0 build. Full per-file logs remain outside the application repository at `C:\Users\steve\scratch\kp-bench-render\release-1.8.90-vs-1.9-rerun-20261015` and, for the stress series, `C:\Users\steve\scratch\kp-bench-render\release-1.8.90-vs-1.9-stress-20261015`.

## Builds and method

- Baseline: KillerPDF 1.8.90 Release, win-x64, source commit `860646ee6336e29274a18cfb89740992a2cdec09`, executable SHA-256 `427BBC7DB4ADC9019A694EFC13831BBDDFA0BE6A84206D042347C4072320B927`.
- Candidate: KillerPDF 1.9.0 Release, win-x64, source commit `b50f1edf4157b9ee1941d673f9a57990d96a51b9` on `dev/1.9-overkill`, executable SHA-256 `14260C549A58F196319F79CCADC3FDDA0D1D6F1210EAD5931D497F9CBFD27D1E`.
- Mode: batch resave of each collection with a per-file CSV log and quiet output.
- One warmup and five measured passes per build and collection. Odd pairs ran 1.8.90 first and even pairs ran 1.9.0 first. Each pass used a fresh process, and the frozen build files were hashed before each pass.
- All five measured passes of each build produced identical per-file logs.
- The 1.9.0 stress pass 5 was interrupted partway through and was rerun after the series resumed, about 15 minutes after the 1.8.90 pass 5, using the same frozen build and inputs. Its log is identical to the other four 1.9.0 stress passes. The interrupted partial output is preserved with the raw logs.
- The damaged-file gate ran after the timed passes, with the two builds alternating per file and a 30-second timeout for each run. It covered all 80 files, 160 runs in total.
- No cache flushing. Memory was not measured. Host load was not certified idle: CPU samples around the resumed work ranged from 42% to 80%.
- Input identity: file counts and byte totals match the recorded corpus identity for all four collections. After the series every input was rehashed against the published manifests: 16,696 of 16,696 regression entries, 552 of 552 standards entries (97 local standards files are not in the manifest), and 80 of 80 damaged-file entries matched. The private stress set has no published manifest and was verified by file count and byte total.
- Full per-file logs remain outside the application repository at `C:\Users\steve\scratch\kp-bench-render\release-1.8.90-vs-1.9-alternating-20261013`. The resumed work is in its `resume-20261013` folder.

The committed [run data](benchmark-runs.csv) and [summary](benchmark-summary.csv) preserve the measurements for both builds. The 1.8.90 record is [KillerPDF 1.8.90](../1.8.90/CORPUS.md).

This run tests batch opening and saving plus malformed-input process safety. Only the follow-up run measures memory. Neither run tests rendering parity or PDF conformance, and it does not replace interactive rendering checks or a separate qpdf/veraPDF before-and-after conformance sweep.
