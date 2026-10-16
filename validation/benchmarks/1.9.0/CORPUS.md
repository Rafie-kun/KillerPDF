# KillerPDF 1.9.0 corpus benchmark

Test date: 2026-10-13

In the 2026-10-13 run, KillerPDF 1.9.0 (development build) saved 108 more PDFs than KillerPDF 1.8.90 across the three normal collections: 109 inputs that 1.8.90 skipped or failed now saved, and one stress input that 1.8.90 saved failed. Its regression and standards timings were above the 10% investigation threshold. Both builds received one warmup and five measured passes per collection in alternating order.

A [follow-up run on 2026-10-15](#follow-up-run-on-2026-10-15) with a newer 1.9.0 build resolves both items. That stress input now saves, so 1.9.0 saves 109 more PDFs than 1.8.90 and loses none. Regression and standards are back under the threshold, and 1.9.0 used 38% to 57% less peak memory than 1.8.90. The damaged-file gate had zero crashes and zero timeouts in both runs, and its outcomes are identical to 1.8.90.

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
| Private stress, one pass | Seconds | 699.164 | 720.049 | 2.99% longer |
| Private stress, one pass | CPU seconds | 301.000 | 335.656 | 11.51% more |
| Private stress, one pass | Peak working set MB | 2,155.5 | 1,068.5 | 50.43% less |

- Regression and standards are both under the 10% threshold. Regression pass times overlap between the builds (1.8.90 from 314.470 to 360.820 seconds, 1.9.0 from 288.683 to 357.053), so its 1.74% difference is within session noise. Host CPU load was about 56% before the run because other work was running, so the host was not certified idle.
- Stress ran once per build, so its timing is not a series. Its 11.51% higher CPU time for 1.9.0 needs a full stress series before it can be confirmed or ruled out.
- All five measured passes of each build produced identical per-file outcomes. Regression and standards outcomes match the 2026-10-13 1.9.0 run. One regression input, `pdfjs/test/pdfs/poppler-395-0-fuzzed.pdf`, still fails, and it now reports its broken cross-reference instead of the signature date.
- In stress, 1.9.0 saves 66 inputs that 1.8.90 fails and loses none: 25,081 saved against 25,015. Across the three normal collections 1.9.0 saves 40,672 inputs against 40,563 for 1.8.90.
- The damaged-file gate ran all 80 files for each build, alternating per file with a 30-second timeout. Both builds produced 78 skips and the same 2 failures, with no crashes or timeouts. The slowest run took 0.589 seconds.
- Peak working set is the largest value sampled every 200 ms during a pass, so a spike shorter than that can be missed. One 1.8.90 regression pass peaked at 2,318.6 MB and the other four between 1,419.0 and 1,671.8 MB. All five 1.9.0 regression passes peaked between 689.6 and 691.7 MB.
- The committed [follow-up run data](benchmark-followup.csv) holds every pass. Full per-file logs remain outside the application repository at `C:\Users\steve\scratch\kp-bench-render\release-1.8.90-vs-1.9-rerun-20261015`.

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
