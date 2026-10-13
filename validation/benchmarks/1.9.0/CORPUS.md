# KillerPDF 1.9.0 corpus benchmark

Test date: 2026-10-13

KillerPDF 1.9.0 (development build) saved 108 more PDFs than KillerPDF 1.8.90 across the three normal collections, but it is not an exact outcome match. 109 inputs that 1.8.90 skipped or failed now save, and one stress input that 1.8.90 saved now fails. The one new failure is unresolved, and the regression and standards timings are above the 10% investigation threshold. Both builds received one warmup and five measured passes per collection in alternating order. The damaged-file gate had zero crashes and zero timeouts, and its outcomes are identical to 1.8.90.

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

The regression and standards changes are above the 10% investigation threshold. They have not been investigated. In every collection the slowest 1.8.90 pass is faster than the fastest 1.9.0 pass (regression 293.91 against 309.874, standards 6.479 against 6.94, stress 677.409 against 697.526), so the difference is not session noise. Stress is below the threshold.

Per-file outcome transitions from 1.8.90 to 1.9.0, computed from the per-pass logs:

| Collection | Same | Fail to save | Skip to save | Save to fail |
| --- | ---: | ---: | ---: | ---: |
| Public regression | 15,171 saved, 1,232 skipped, 250 failed | 42 | 1 | 0 |
| Standards and color | 377 saved, 235 skipped, 37 failed | 0 | 0 | 0 |
| Private stress | 25,014 saved, 2,957 skipped, 1,561 failed | 66 | 0 | 1 |

Three regression files and one stress file kept their status but have different diagnostic detail text.

The one input that regressed is `pdf-association/poppler/poppler-81748-0.pdf` in the private stress collection. 1.8.90 saved it, and 1.9.0 reports a save failure with the detail "The signature /M value is not a valid PDF date". It has not been classified or fixed. Pristine rendering takes precedence over performance, and an unresolved difference blocks a parity or release claim, so this stays open.

Damaged-file results match: both builds produced 78 skips and the same two reported failures, with no file in the 80-file set producing a different outcome between builds.

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
- Full per-file logs remain outside the application repository at `C:\Users\steve\kp-bench-render\release-1.8.90-vs-1.9-alternating-20261013`. The resumed work is in its `resume-20261013` folder.

The committed [run data](benchmark-runs.csv) and [summary](benchmark-summary.csv) preserve the measurements for both builds. The 1.8.90 record is [KillerPDF 1.8.90](../1.8.90/CORPUS.md).

This run tests batch opening and saving plus malformed-input process safety. It does not test rendering parity, memory use, or PDF conformance, and it does not replace interactive rendering checks or a separate qpdf/veraPDF before-and-after conformance sweep.
