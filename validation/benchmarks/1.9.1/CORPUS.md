# KillerPDF 1.9.1 corpus report

Test date: 2026-10-31

The published KillerPDF 1.9.1 application saved 41,505 of the 46,944 inputs in the three normal collections. That is 541 more than the previous published 1.9.0 candidate corpus record, with no previously saved input becoming unsavable. A separate gate tested all 80 damaged inputs, including files without a `.pdf` extension.

Each normal collection received one warmup and five measured passes. Every pass returned the same outcome for every input. The damaged-file gate ran once with a 30-second limit per file.

## Results

Counts are per pass, not the sum of repeated passes.

| Collection | Inputs | Saved | Skipped | Save failures | Median seconds |
| --- | ---: | ---: | ---: | ---: | ---: |
| Public regression | 16,696 | 15,478 | 1,069 | 149 | 261.725 |
| Standards and color | 649 | 399 | 230 | 20 | 6.692 |
| Private stress | 29,599 | 25,628 | 2,674 | 1,297 | 510.649 |
| Damaged-file safety | 80 | 0 | 78 | 2 | Separate timeout gate |

The normal collections total 41,505 saves, 3,973 skips, and 1,466 save failures. These collections include hostile and unsupported inputs, so this is not a zero-failure result. A normal batch returned exit code 1 when it contained save failures; completed logs and their individual outcomes determine the result.

The damaged-file gate recorded **0 crashes and 0 timeouts**. Its complete status counts are in [fuzz-summary.csv](fuzz-summary.csv).

## Changes from the previous published record

The comparison uses the [1.9.0 candidate run from October 28](../1.9.0/CORPUS.md#release-candidate-run-on-2026-10-28), source commit `63f0cc8ec132f18768c4c6767b62434961a53f6a`. The retained individual-file logs were checked against the outcome hashes in its published [run data](../1.9.0/benchmark-freeze-20261028.csv). This is a comparison of recorded file outcomes, not a new performance comparison against an older executable.

| Collection | Previously failed, now saved | Previously skipped, now saved | Previously saved, now unsaved |
| --- | ---: | ---: | ---: |
| Public regression | 4 | 163 | 0 |
| Standards and color | 1 | 5 | 0 |
| Private stress | 90 | 278 | 0 |
| Total | 95 | 446 | 0 |

Six stress inputs that previously stopped during opening now reach the save path but still fail there. One previous save failure is now refused during opening because an encryption dictionary is present without a trailer `/Encrypt` entry. None of those seven inputs saved in the previous record. [outcome-transitions.csv](outcome-transitions.csv) includes those changes rather than counting only improvements.

The four regression save failures that now succeed involved deeply nested catalog or bookmark graphs and repeated AcroForm field references. The newly readable regression inputs had previously stopped at cross-reference generation checks. The standards gains include nested Type 3 fonts, text in patterns, and damaged-file recovery cases.

## Build and input identity

- Application: [published v1.9.1](https://github.com/SteveTheKiller/KillerPDF/releases/tag/v1.9.1), source commit `03ca7b6d45c09fb5cc4b9479e9290405051c00f9`.
- Tested executable: `KillerPDF.App.exe`, file version `1.9.1.0`, extracted from the published signed `KillerPDF.exe`. Its Windows signature was valid before the run. The installer SHA-256 matches the release asset digest.
- Installer SHA-256: `8F7A224994E75660E0CAABA26D14C773422DD96ACB22E2252D63E9C6FE39DCB8`.
- Tested executable SHA-256: `888BBCE204D99D05948AD9E707227D72E22D99B20AB4BC7BF840571010818090`.
- Application assembly SHA-256: `2CCD36FB6B0A871E543B2D18B43804690234C1EF0AAF2DD8D1393355DDDA08F6`.
- Engine assembly SHA-256: `051343A65F037B3416208F369B325B5571048A8B6778350B66F190D4F0B3E62E`.
- Corpus repository commit: `208cdc043c12d5abd4e0545215d8a0744f479a36`. Public inputs use its `v1.8.1` manifests, alongside the existing restricted stress and color collections.
- All 47,024 input SHA-256 fingerprints were recorded. All 17,425 files covered by the public or color-import manifests matched them. The other 29,599 fingerprints identify the local stress inputs.
- Retained input inventory SHA-256: `1BB123C85A9166DC3DACDDE325093790D72578CD17BA7A3DC1D2EE4B14B2E013`.
- Runner: the corpus repository's `scripts/benchmark_corpus.ps1`, SHA-256 `4B2A601038494F58B79CAF6539ECC99EEB5DFE3F477B9368AEF7D61843BE74E8`.

There are 17,328 downloadable public inputs and 29,696 locally held inputs, including the 97 color files. Restricted PDFs and generated output PDFs are not included in this report.

## Method and limits

The run used the main Windows computer, SHREDDER: AMD Ryzen 5 3600 (6 cores, 12 logical processors), 31.9 GiB RAM, Windows 11 Pro build 26300, and .NET 10.0.14. Collections ran serially. Input hash verification finished during the stress warmup, before the measured stress passes. The host was not certified idle, and caches were not flushed.

The normal command was `KillerPDF.App.exe --batch-resave <collection> <output> --log <log.csv> --quiet`. The safety gate invoked the same command separately for each damaged input and enforced its timeout. Generated output PDFs were removed after each pass; logs and fingerprints were retained outside the application repository.

This measures opening and resaving. It does not establish rendering fidelity, editing behavior, or PDF standards compliance. Elapsed time is reported for this run; CPU time and peak memory were not collected, and no cross-session speed or memory improvement is claimed.

- [Summary by collection](benchmark-summary.csv)
- [Warmups and measured runs](benchmark-runs.csv)
- [Damaged-file status summary](fuzz-summary.csv)
- [Outcome transition counts](outcome-transitions.csv)

The complete local evidence is retained under `C:\Users\steve\scratch\kp-bench-render\release-1.9.1-20261031`, including individual-file logs, the input inventory, executable identity, and analysis.
