# KillerPDF 1.9 adversarial review and speed check, October 20, 2026

Checkout: `C:\Users\steve\code\KillerPDF` on `dev/1.9-overkill` at 90df881 plus the uncommitted edits listed below. Comparison payload: a detached worktree at c77f01a, the October 12 closeout and the last engine that had a retained speed measurement. Both were built as ordinary Release builds with SDK 10.0.402 on SHREDDER and measured in the same session, alternating, with `--batch-render --parallel 1`. Reference numbers for v1.8.80 are quoted from `release-19-baseline\BASELINE.md` and were not re-run; this review is 1.9 against 1.9.

**Result: one real speed regression, concentrated and already bisected to a single commit; the broad corpus is unchanged; the working tree does not build; the three-agent review accepted 64 of 97 candidate defects, two of them high severity in deployment paths and one high in redaction.** Interactive gates S1 to S3 could not run because the desktop was locked; the command is recorded below.

## 1. Speed

### 1.1 Headless, eight copies of the first six pages at 1920 px, per-copy milliseconds, five measured rounds per side

| Document | c77f01a median (range) | Current median (range) | Change | Two-sigma floor |
| --- | ---: | ---: | ---: | ---: |
| 210260 (CalRGB JPEG manual) | 155.8 (152 to 164) | 158.0 (145 to 184) | +1.4% | about 12% |
| 3253 article (text) | 200.0 (199 to 218) | 215.0 (198 to 227) | +7.5% | about 12% |
| altona technical (CMYK press, 1 page) | 514.1 (482 to 600) | 531.2 (511 to 555) | +3.3% | about 7% |
| Montenegro2021 (Word export, RGB page groups) | 125.9 (115 to 130) | 282.4 (273 to 292) | **+124%** | about 12% |
| 42828 JBIG2 scan | 528.4 (511 to 601) | 537.0 (511 to 557) | +1.6% | about 12% |

The October 11 retained numbers were 142 ms (210260) and about 198 ms (article), so 210260, the article, altona and the scan are where they were. Montenegro is 2.3 times slower with pixel-identical output (one pixel differs by one level across six pages).

### 1.2 Broad corpus (S4 workload), 649 conformance files, page one, three rounds per side

| Size | c77f01a raster sum | Current raster sum | Wall | Peak working set |
| --- | ---: | ---: | ---: | ---: |
| 512 | 14,057 ms | 14,336 ms (+2%) | 22.9 s vs 22.8 s | 343 vs 346 MiB |
| 2048 | 18,382 ms | 18,063 ms (-2%) | 56.5 s vs 54.0 s | 405 vs 400 MiB |

Both inside the noise floor. S4 and M1 are not affected. The regression is confined to pages that carry a page `/Group`: 46 of 649 conformance first pages and 115 of 977 PDF.js first pages in the corpus, but in user documents it is the common case. Word, PowerPoint, LibreOffice, Chrome and most InDesign exports put a transparency group on every page.

### 1.3 Bisect, Montenegro, three rounds

| Payload | Per copy |
| --- | ---: |
| 1875da5 (profile color cache lock, the commit before) | 119 to 146 ms |
| cf6d2c3 "Blend RGB page groups before adding white paper" | 268 to 286 ms |

cf6d2c3 is the cause. The commit's own record (`validation/records/page-group-backdrop-20261018.md`) ran pixel comparisons and oracle checks but no timing, and says so.

### 1.4 ICC-profiled RGB page group (Arakawa-2025 paper, two pages, 1920 px, eight copies), three rounds

| Payload | Per copy |
| --- | ---: |
| c77f01a | 365 to 381 ms |
| 1875da5 (color cache lock only) | 441 to 450 ms (+20%) |
| cf6d2c3 (lock plus isolation) | 453 to 462 ms |
| Current | 440 to 467 ms |

Output identical (2,545 pixels differ by one level on page one, none on page two). So there is a second, smaller cost: on ICC-profiled RGB surfaces the lock added in 1875da5 to `RasterSurface.GetRgb` and `ColorFromRgb` costs about 20% on its own. The Referee ruled that finding (BF-E004) invalid on the strength of the Montenegro measurement, where no profile is involved; the Arakawa measurement overrides that verdict. It is real on profiled pages.

### 1.5 Mechanism

`PdfPageRenderer.RenderUncached` now sets `isolatePageGroup` for any page with a non-CMYK `/Group`, renders the page onto an alpha-zero backdrop instead of white, and then runs a scalar per-pixel loop that composites the result over white. The composite loop is a few milliseconds; the bulk of the 150 ms goes to rendering on a transparent backdrop, which disables the opaque fast paths (`PaintDirectCoverageRows`, bulk alpha runs, the opaque interior fills in Raster.cs) for every fill, glyph and image on the page. The commit fixed a genuine defect (Difference blending over the page backdrop in `transparency_group.pdf` and `issue19360.pdf`), but it applied the expensive path to every grouped page rather than the pages that need it.

The isolation only changes output when page content blends against the backdrop: a non-Normal `BM`, a `SMask` in an ExtGState at page level, or a knockout group. A page whose content uses only Normal or Compatible blending composites identically on white and on transparent-then-white.

### 1.6 Fix (performance commit, separate from any accuracy commit)

1. Gate `isolatePageGroup` on need, not on presence. Before rendering, scan the page's cached instruction list and its ExtGState resources (recursively through Form XObjects already parsed by `ReadStreamInstructions`) for `BM` other than Normal or Compatible, `SMask` other than None, or a group with `/K true`. Only then take the transparent path. Also require `/Group` to be a dictionary with `/S /Transparency` (BF-E010). Target: Montenegro back to the c77f01a value, `transparency_group.pdf` and `issue19360.pdf` unchanged at both sizes.
2. If a cheaper universal path is wanted later, teach the opaque fast paths to run on a constant-alpha-zero surface and vectorize the final composite. That is a larger change and not needed for 1.9.0.
3. Replace the lock in `GetRgb` and `ColorFromRgb` with a per-worker cache. The original race (`profile-color-parallelism-20261018.md`) was row workers sharing one last-color slot. A `[ThreadStatic]` or per-row-range cache removes both the race and the lock. Then also cover `ColorFromInk` and `GetInk`, which still share the slots without the lock (BF-E005, valid).
4. Re-run the five-document A/B and the Arakawa A/B after each of the two changes, then A1 at 512 and 2048, then the S1 to S3 interactive run.

### 1.7 Interactive gates (not run)

The harness builds from the current tree and starts, but `CopyFromScreen` fails with "The handle is invalid" because the SHREDDER desktop is locked (the one captured frame is the lock screen). Run with the desktop unlocked and nothing else open:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:\Users\steve\scratch\kp-codex-speed-20261020\Latency-Run2.ps1
```

It alternates c77f01a and the current build over the five S1 documents, five measured runs each, and writes `latency3\summary.md`. Expect the Montenegro painted and scroll times to show the same regression until 1.6 lands; the other four should be flat.

## 2. The working tree does not build

`dotnet build KillerPDF.csproj` fails in the real checkout. The untracked folder `validation\records\adversarial-review-20261019-fresh\` holds throwaway probe projects (`features\Program.cs`, `features\UiReplay\Program.cs`, `layout\Program.cs`, `layout\ShotProgram.cs` and four csproj files). `KillerPDF.csproj` removes `engine\**`, `third_party\**`, `tests\**`, `tools\**` and a few others from the compile glob but not `validation\**`, so these files are compiled into the app and fail with CS0101 and CS0246. The benchmark builds above removed that folder from a scratch copy only; the repository is untouched.

Fix: either move the probes out of the repository (they are evidence, and the harness says evidence lives under scratch) or add `<Compile Remove="validation\**" />` to `KillerPDF.csproj`. Do both: the glob exclusion prevents a repeat.

Also note that c77f01a no longer builds on this machine without `-p:NuGetAudit=false`: `System.Security.Cryptography.Pkcs` 9.0.10 now trips NU1902 and NU1903 as errors. The branch already moved to 9.0.21 (3c11e1f), so this only affects old worktrees.

## 3. Uncommitted edits in the tree

| File | State | Note |
| --- | --- | --- |
| `Services/PdfEngineIntegration.cs` | modified | Removes the `UnauthorizedAccessException` fallback in `ReplaceFileAtomically` that copied straight over the destination when a sibling temp file could not be created. After this, Save fails on folders that allow modifying a file but not creating one (some shares and restricted ACLs). If the fallback was removed to avoid a torn write, keep a user-visible error that says why, or write the temp file to the user's temp folder and copy once. |
| `engine/.../PdfDocumentInformation.cs` plus untracked `PdfXmpDocumentMetadata.cs`, `PdfVersion2Upgrade.cs` and its test | modified and untracked | /Info fields fall back to XMP. The catch only handles `InvalidOperationException`; confirm the XMP reader cannot throw `XmlException`, `PdfFilterException` or `FormatException` on damaged packets, or widen the catch. |
| `MainWindow.xaml`, `CHANGELOG.md`, `Controls/CapCenterMargin.cs`, `KillerPDF.Tests/RadioDotCenterTests.cs` | modified and untracked | In-flight UI work from another session. Not reviewed. |

None of these were discarded or touched.

## 4. Adversarial review

Scope: everything between c77f01a and 90df881 (343 commits, 542 files), split into the engine render and document path, the viewer and shell, and services, CLI, installer and release automation. Three Bug-finders, three Defenders and three Referees ran on the standard tier (Jev route `standard`, confidence 1.0). Source snapshot and diffs were copied to the review container; the Windows checkout was read-only throughout.

| Area | Candidates | Valid | Invalid | Needs investigation | High | Medium |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Engine | 26 | 17 | 8 | 1 | 1 | 3 |
| Viewer and shell | 31 | 13 | 18 | 0 | 0 | 4 |
| Services, CLI, installer, release | 40 | 34 | 5 | 1 | 2 | 19 |
| Total | 97 | 64 | 31 | 2 | 3 | 26 |

False-positive rate after adjudication: 32%. The viewer findings were the noisiest (18 of 31 rejected, mostly because the Bug-finder missed existing guards such as `CancelRenderWork`, `WorkAreaFit.Fit` and `HideSignaturePopup`). The services findings were the cleanest and the most serious.

### 4.1 High severity

| ID | Location | Finding | Fix |
| --- | --- | --- | --- |
| BF-S001 | `App.xaml.cs` Uninstall, about line 2131 | `/uninstall` runs `rmdir /s /q` on the running exe's folder without checking the registered InstallPath or a payload manifest. `/uninstall-silent` skips the only confirmation. Running the exe from any folder deletes that folder. | Require the folder to match the registered InstallPath or contain a valid `payload.manifest`; delete manifest-listed files, then a non-recursive rmdir. |
| BF-S002 | `Features/About/AboutController.cs` about line 326 | The self-update batch is written to `%TEMP%` and run with `Verb = runas` after the Authenticode check. A same-user process can edit the batch or swap `newExe` between the check and the elevated run. The uninstall batch uses a fixed name. | Stage into an ACL-restricted folder, or have the elevated launcher verify and copy the exe itself instead of running a batch. |
| BF-E001 | `engine/.../PdfContentRedaction.cs` about line 1695 | The `paint.Operator != "n"` guard was removed, so a clip-only `re W n` overlapping a region now adds a "Vector" reason. Nearly every producer emits clip rectangles, so ordinary pages fall back to raster or fail `VectorInRegion` verification. | Restore the clip-only exemption; add a regression test with `re W n` over a redaction region. |

### 4.2 Medium severity, engine

- BF-E003 `PdfContentRedaction.cs` about 862: verification accepts leftover text whose letters are a sub-multiset of the overlay's letters, order-insensitive. "RED" under "REDACTED" passes. Verify per overlay box by position or require no original glyph run in the region.
- BF-E005 `PdfPageRenderer.Surface.cs` about 324: `ColorFromInk` and `GetInk` write the same cache slots the new lock protects, without the lock, and `ColorFromInk` is reached from the parallel compositor. Torn (key, color) reads on ink surfaces. Combine with the per-worker cache in 1.6.
- BF-E008 `PdfImageDownsampler.cs` about 242: `checked(width * height * components)` throws `OverflowException`, which the per-image catch does not list, so one malformed image aborts Reduce File Size. Add it to the filter or compute in long.

### 4.3 Medium severity, viewer and shell

- BF-V001 `Shell/TempReload.cs` about 77: `SaveTempAndReload` clears annotations, remaps rotations and closes the engine session before the save and reopen succeed; only XRef exceptions are caught. Any other failure leaves a closed document with mutated state and no undo entry. Defer the mutations until the reopen succeeds or restore in a catch-all.
- BF-V004 `Shell/Signing.cs` about 157: `_signedFields` is window-level, keyed by object number, never cleared. After a tab switch or page edit a stale entry shows the change/remove menu instead of the picker. Scope per document session.
- BF-V007 `MainWindow.xaml.cs` about 597: closing in F11 full screen persists the monitor rectangle as the normal window bounds. Save the pre-full-screen bounds when `_fullScreen` is true.
- BF-V018 `Shell/DragDrop.cs` about 504: `AppendFilesToCurrentDoc` awaits repair and import, rechecks only `_doc is null`, then shifts annotation page indexes before `SaveTempAndReload`. A tab or pane switch during the await imports into the wrong document; a reload failure leaves annotations shifted. Capture `CaptureDocumentOrigin()` first, as Split does, and make the shift transactional.

### 4.4 Medium severity, services, CLI, installer and release

- BF-S003 `App.xaml.cs` about 1534: Authenticode checks accept any valid signature; publisher and thumbprint are discarded; revocation is cache-only despite the comment. Pin the publisher.
- BF-S004 `App.xaml.cs` about 1026 and 889: `RemovePerUserInstall` and `RemoveMachineInstallConflict` recursively delete the registry-derived path with none of the ownership validation Install now applies; the machine variant does it elevated.
- BF-S005 `Packaging/KillerLauncher/Program.cs` about 208: in the install rollback, `DeleteDirectoryWithRetries(staging)` can throw before the backup is restored, stranding the previous version in `.previous-<guid>` and losing the original exception.
- BF-S006 `Program.cs` about 339: with KillerPDF running, `CloseInstalledCopies` throws a plain `InvalidOperationException`, which maps to exit 1, not 1618, so deployment tools do not retry; 98e6213 only covers the Win32 and sharing-violation cases.
- BF-S008 `InstallerWizard.xaml.cs` about 218: the elevated all-users install accepts any empty user-writable folder and registers it machine-wide. Restrict machine installs to non-user-writable locations.
- BF-S010 `AboutController.cs` about 304: update scope is derived from folder writability, so an elevated copy under Program Files hands off `/install-user` and the launcher throws AlreadyMachine.
- BF-S014 `Features/Cli/CliRunner.cs` about 642 and `CliRunner.Content.cs`: `--extract-pages`, `--export`, `--convert-office` and `--decrypt` have no input equals output guard and write with `File.WriteAllBytes`; `--extract-pages in.pdf 1 in.pdf` destroys the source.
- BF-S015 `Services/PdfDataMergeWorkflow.cs` about 198: CLI data merge passes no approved-overwrite set, and a null set means overwrite everything, the opposite of the new split policy.
- BF-S016 `CliRunner.Redact.cs` about 165: when nothing matches, the source is copied to the output and exit 0 is returned before `--sanitize` runs.
- BF-S019 `Services/PayloadIntegrityVerifier.cs` about 97: `KillerPDF.cmd` written by registration is not in the manifest, so `--verify` reports "Unexpected file" after every normal install; the MCP verify-install tool depends on that exit code.
- BF-S020 `Services/OfficeConversion.cs` about 57: `thread.Join()` has no timeout and `Close` is not in a finally, so a hidden Office prompt hangs the conversion and an export failure leaves the document open in the user's Word or Excel.
- BF-S021 `Services/Signing/PfxFileCertificateProvider.cs` about 17: `PersistKeySet | Exportable` writes the PFX private key into the user's key store and never removes it. Use `EphemeralKeySet`.
- BF-S022 `CliRunner.cs` about 2276: `--lang` is not validated before use in a path and URL; `..\..\x` escapes TessDataDir. Validate against a pattern or a known list.
- BF-S025 `Services/SearchService.cs` about 76: phrase search matches when the query is contained in a later word of the combined string, producing a union rectangle that includes an unrelated leading word and a duplicate hit.
- BF-S026 `Services/SvgSignatureImporter.cs` about 95: unbounded recursion on nested groups; a hostile SVG overflows the stack, which cannot be caught.
- BF-S027 `Services/AppDataPaths.cs` about 84: any read failure of `settings.json` returns an empty dictionary, and the next `SetPortableSetting` writes it back, wiping every portable setting. Fixed temp name and process-local lock.
- BF-S030 `.github/workflows/chocolatey-release.yml` about 23: the dispatch tag input is interpolated into pwsh with the Chocolatey key in the environment; no `permissions:` block; checkout not pinned.
- BF-S031 `.github/workflows/winget-release.yml` about 49: Komac is fetched from `releases/latest` unpinned and unverified, then run with `WINGET_TOKEN`; the retry loop can open duplicate PRs.
- BF-S034 `release.ps1` about 582: README and landing-page commits are pushed before the tag-exists check and the unit tests, so a failed release leaves pushed commits or a pushed tag with no release.

### 4.5 Low severity, valid (for the backlog)

Engine: BF-E002 hairline stroke bounds equal the whole clip; BF-E006 over-limit pages parse twice; BF-E010 `/Group` presence rather than `/S /Transparency` triggers isolation; BF-E012 `NoteSpotUnits` lock per spot paint in plate previews; BF-E014 `{` and `}` in real file names fail the rename macro; BF-E015 structured export flattens every page for a partial export; BF-E016 HashSet per `Resolve` in the measurement and redaction mark readers; BF-E017 TSA response unbounded and token ignored; BF-E018 text-line edit rewrites every other page in the font fallback; BF-E022 `DroppedKeys` lists "DecodedLength" instead of `DL`; BF-E023 repeated images re-encoded per `Do` during redaction; BF-E024 `Overlay()` substitutes `?` without checking `SupportsOverlayText`; BF-E026 form and pattern streams still have the hard 1M instruction limit.

Viewer: BF-V006 `ApplyFullScreen` has no cleanup on exception; BF-V010 `CloseSplit` applies a stale slide-back without clamping; BF-V017 undo pops before applying, so a reopen failure loses the entry; BF-V019 rotate remaps annotations before the reload with no restore; BF-V021 unbounded `Dispatcher.Invoke` in the crash handler; BF-V023 unguarded `File.Delete` in the macro `finally`; BF-V025 hard-coded English in export filters and macro exceptions; BF-V026 signature-tile delete has no accessible name and tiles are mouse-only; BF-V031 F1, F5 to F8, F11 and F12 ignore modifiers.

Services: BF-S007 test-only environment variables honored by the shipped launcher; BF-S009 manifest hashing on the wizard UI thread, three times; BF-S011 single-instance pipe without `CurrentUserOnly` and with an unbounded `ReadLine`; BF-S012 forwarded launch argument not made absolute and dropped if the main window is not ready; BF-S018 split publishes one file at a time after a non-atomic pre-check; BF-S023 GUI OCR download skips the new deadline and size cap; BF-S024 `BatchRenderRunner.Csv` lacks the formula neutralization added to `BatchRunner`; BF-S028 measurements that fail to re-add are dropped silently on save; BF-S033 the signed Store `/silent` path is never smoke-tested on released bits; BF-S035 `GC.Collect()` inside `Parallel.For` in batch render; BF-S036 plate PNG names collide for similar ink names; BF-S038 exe hash and signer computed on the UI thread in About, update download unbounded; BF-S040 raw English exception text in conversion dialogs.

### 4.6 Needs investigation

- BF-E020 `Filters/Jbig2/CX.cs` and `Jbig2Document`: the new allocation budget is charge-only with a 1 MiB floor for small streams. Run the JBIG2 corpus samples under 16 KB decoded that carry many segments or symbol-dictionary copies and log `ReserveAuxiliary` totals against the budget; raise the floor if any legitimate file is rejected.
- BF-S032 `store/Submit-StoreRelease.ps1` about 120: whether `isReady` can still be true from before the commit depends on Partner Center behavior. Try it against a test submission; add a size or hash comparison to the HEAD check regardless.

### 4.7 Rejected candidates worth knowing about

Thirty-one candidates were ruled invalid. Two are worth recording because they will come up again: BF-E004 (the color-cache lock) was ruled invalid on the Montenegro evidence and is overridden by the Arakawa measurement in 1.4; BF-S013 (the removed save fallback) was ruled invalid only because the removal is uncommitted and absent from the reviewed snapshot, and it is listed in section 3 instead. The rest were guards the Bug-finders missed (`CancelRenderWork` stops the rerender timer on tab switch, `WorkAreaFit.Fit` clamps restored windows, undo entries are popped and deep-cloned, `PdfDocument` is not disposable, the deep-form counter is a per-render local).

## 5. Plan

Priority order is the one you set: accuracy, then speed, then memory, then everything else. Each item is a separate commit with its own test, performance and accuracy commits never mixed.

### Phase A: unblock the tree (same day)

1. Move `validation\records\adversarial-review-20261019-fresh\` probe projects out of the repository and add `<Compile Remove="validation\**" />` to `KillerPDF.csproj`. Confirm `dotnet build KillerPDF.csproj` passes in the real checkout.
2. Decide the three uncommitted edits in section 3: commit the XMP fallback with a widened catch and its test, decide the save fallback with a user-visible error, and let the owning session finish the XAML and margin work.

### Phase B: speed back to the October 12 level (one to two days)

3. Need-gated page-group isolation (1.6 item 1) with BF-E010 folded in. Gate: Montenegro per copy within the c77f01a noise floor; `transparency_group.pdf` and `issue19360.pdf` byte-identical to the current output at 512 and 2048; A1 run shows no other changed page.
4. Per-worker profile color cache replacing the lock (1.6 item 3), covering BF-E005. Gate: Arakawa per copy within the c77f01a floor; the `transparency_group.pdf` serial-versus-parallel pixel check from `profile-color-parallelism-20261018.md` still passes.
5. Interactive S1 to S3 and M3 on the unlocked desktop, c77f01a versus the result of 3 and 4, then the current build versus v1.8.80 for the gate table.
6. Add timing to the engine change checklist: any commit that touches `Rendering\` runs the five-document A/B before it lands. The page-group commit would have been caught in five minutes.

### Phase C: redaction and data safety (two to three days)

7. BF-E001 clip exemption, BF-E003 verification tightening, BF-E024 overlay encoding check, BF-E022 `DL`, BF-E023 memoized image re-encoding. Redaction is the feature where a quiet defect does the most damage.
8. BF-S014, BF-S015, BF-S016, BF-S018: one shared "safe output" helper for the CLI (full-path case-insensitive input equals output check, temp write, atomic replace, refuse existing outputs without `--overwrite`) and route every writing command through it.
9. BF-V001, BF-V018, BF-V019, BF-V017: make `SaveTempAndReload` and its callers transactional. Capture origin, mutate only after the reopen succeeds, restore on failure.
10. BF-S027 settings wipe, BF-S020 Office join timeout and finally-close, BF-S026 SVG depth cap, BF-S022 `--lang` validation.

### Phase D: installer, update and release hardening (two days, lab VM for acceptance)

11. BF-S001, BF-S004: one ownership check (registered InstallPath plus manifest) shared by uninstall, per-user removal and machine-conflict removal; delete manifest-listed files only.
12. BF-S002, BF-S003, BF-S008, BF-S010: elevated update path without a user-writable batch, pinned publisher, machine installs restricted to protected locations, scope from the registry.
13. BF-S005, BF-S006, BF-S019, BF-S033: rollback order, 1618 for the running-copy case, `KillerPDF.cmd` in the manifest exemption, signed `/silent` smoke test in `Test-ReleaseArtifacts`.
14. BF-S034, BF-S030, BF-S031: all release gates before any push; workflow inputs through env vars with a version pattern; pin Komac and actions by SHA; `permissions:` blocks.

### Phase E: finish the release gates

15. A1 and A2 dispositions remain open per `RELEASE-GATES-1.9.md` (572 to 784 changed hashes after the October 18 repairs). After Phase B the page-group change should move zero further pages; the remaining dispositions are accuracy work and are not made easier or harder by this review.
16. A3 difficult-set visual review and A4 viewer-equals-headless still need the desktop.
17. Rerun the full gate table on the final candidate, both sizes, with the v1.8.80 column, and publish the Montenegro story honestly: 1.9 composites page groups per the specification and, after Phase B, at the same speed as before.

### What "best PDF editor" needs beyond this review

The engine is in good shape on accuracy (zero failures on both corpora, better than PDFium on JBIG2, CJK and PDF/A cases) and competitive on ordinary documents. What separates it from the editors people pay for is now mostly in the application layer, and the valid findings cluster there: document-state transactions in the shell, deployment safety, and CLI output safety. The medium viewer findings (V001, V004, V007, V018) are all the same pattern, live state read or mutated across an await or before a save succeeds, and fixing them with one origin-capture discipline is worth more than any single bug. After that, the next capability gaps are the ones already in the backlog: measurement calibration, Scan to Text follow-ups, and the 42828 viewer memory peak (M3, +20% over the gate).

## 6. Evidence

- Task folder (delete when done): `C:\Users\steve\scratch\kp-codex-speed-20261020\`. Builds: `out-cur-src`, `out-wt-c77f01a`, `out-wt-1875da5`, `out-wt-cf6d2c3`. Scripts: `Build-Both.ps1`, `AB-Versions.ps1`, `Bisect-PageGroup.ps1`, `S4-AB.ps1`, `Arakawa-AB.ps1`, `Latency-Run2.ps1`. Results: `logs\ab-summary.csv`, `logs\bisect-summary.csv`, `logs\s4-summary.csv`, `logs\arakawa-summary.csv`, `logs\build-*.log`. Rendered pages used for the pixel checks: `work\Montenegro2021\out-old-5` and `out-new-5`, `work\arakawa\out-c77f01a-3` and `out-current-3`.
- Review inputs and outputs: `review\src.tar`, `review\diff-c77f01a-HEAD.patch`, `review\uncommitted.patch`, `review\commits.txt`, and the nine agent reports (`bf-*.json`, `def-*.json`, `ref-*.json`) copied beside them.
- Git worktrees added for the comparison: `wt-c77f01a`, `wt-1875da5`, `wt-cf6d2c3` under the task folder (`git worktree remove` when the folder goes).
- Nothing in `C:\Users\steve\code\KillerPDF` was modified, reset or discarded. No commit was made.

## 7. What was verified and what was not

Verified by running it: both Release builds, the five-document A/B (five rounds), the bisect (three rounds), the broad corpus at 512 and 2048 (three rounds), the Arakawa four-way A/B (three rounds), the pixel comparisons on Montenegro and Arakawa, the build failure of the real tree's project file, and the corpus page-group counts.

Verified by reading: every valid finding was independently re-read by the Referee against the current source; the three high-severity findings and the page-group mechanism were additionally read by the coordinator.

Not verified: interactive S1 to S3 and M3 (desktop locked), any A1 or A2 effect of the proposed fixes (not implemented), the two needs-investigation items, and the in-flight XAML and margin edits.
