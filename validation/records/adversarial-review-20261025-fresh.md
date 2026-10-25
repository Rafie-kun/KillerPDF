# KillerPDF 1.9.0 fresh adversarial review and release plan, October 25, 2026

## Verdict

1.9.0 is not releasable today, and two days of work gets the code there only if three things outside the code also land: the SimplySign signing path is repaired, the release-blocking speed gates are either re-measured and passed or explicitly waived by Steve, and the Explorer preview handler is either fixed on a lab VM or pulled from the release. The code itself is in better shape than the gate paperwork suggests: both test suites pass on the current head, the Release build is clean, and the dependency audit is clean. The review found 22 release blockers, of which 14 are code or script fixes that fit inside two days, 4 are text that must be rewritten before anything ships, and 4 are decisions or lab work.

This review was run fresh, not as a continuation of the chained reviews from October 21 and 23. It covered the whole tree on `dev/1.9-overkill` at `36436c9` (one later commit, `2680e6e`, only adds benchmark tooling), split into six slices, each run through the harness's three isolated stages (bug-finder, defender, referee). All stages were read-only against the working tree. No app window was opened on SHREDDER.

| Slice | Scope | Findings | Valid | Invalid | Blockers | Should fix | Defer |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| A | App shell and viewer (MainWindow, App, Shell/, Features/, Controls/) | 45 | 44 | 1 | 3 | 31 | 10 |
| B | Services/ and Models/ | 27 | 22 | 5 | 2 | 7 | 13 |
| C | Engine read side (parsing, filters, fonts, rendering, decryption) | 27 | 27 | 0 | 3 | 20 | 4 |
| D | Engine write side (writer, editing, redaction, encryption, signing) | 26 | 25 | 1 | 2 | 16 | 7 |
| E | Release pipeline, packaging, installer, docs, licensing, gates | 26 | 24 | 2 | 8 | 11 | 4 |
| F | Feature wiring: changelog, site, help, Store, CLI, macros, locales, milestone | 42 | 39 | 3 | 4 | 24 | 11 |
| Total | | 193 | 181 | 12 | 22 | 109 | 49 |

Nothing was ruled needs-investigation. False-positive rate after adjudication: 6 percent. The raw bug-finder, defender and referee JSON for every slice is under `C:\Users\steve\scratch\kp-adversarial-20261025\` (bugfinder-X.json, defender-X.json, referee-X.json, merged-ABCDE.json); each valid item there carries a concrete fix. Model and effort for every launch: general-purpose subagent on the session's selected model, default effort, 18 launches.

## Evidence gathered on the current head

| Check | Result |
| --- | --- |
| Engine suite, `KillerPdf.Engine.Tests`, Debug | 6,919 passed, 0 failed, 0 skipped, 2 min 13 s |
| App suite, `KillerPDF.Tests`, Debug, window-free filter | 1,320 passed, 0 failed, 0 skipped, 14 s. Excluded the 12 window-opening tests (ComparisonBarLayoutTests, WindowFxTests, BottomTabFaceFlipsWhileItsLabelAndCloseTargetStayUpright, TabTraversalEntersAndLeavesTheReadingDocument) |
| Release build, `KillerPDF.csproj` | 0 errors, 671 warnings, all from the vendored CoreJ2K decoder, 0 from first-party code |
| `dotnet list package --vulnerable --include-transitive` | App and engine both clean; no deprecated packages |
| `tests/KillerPDF.LatencySmoke` (uncommitted M3 tooling) | Built clean and committed as `2680e6e` |
| Working tree | Clean apart from the untracked `KillerPDF_Redacted.pdf`, which the open goal says to preserve |

Logs: `C:\Users\steve\scratch\kp-adversarial-20261025\engine-tests.log`, `app-tests.log`, `release-build.log`.

## Blockers

Each item names the slice id so the JSON can be consulted for the full trace and fix.

### Data loss, crashes and security in the app

1. **A-001. Closing either window of a torn-out pair deletes the other window's working files.** `MainWindow.xaml.cs:439` runs `App.CleanupSessionTemps()` when `Application.Current.Windows.Count <= 1`, but WPF removes the closing window from that collection before `Closed` fires, so with two windows the count is already 1. Every open document lives in a process temp (`CompleteDocumentSource.Capture`), so the survivor can no longer save, print, search or render new pages, and Save hits a crash dialog outside the try at `Shell/FileOperations.cs:1289`. Fix: count other live MainWindows instead, or move the cleanup to `App.OnExit`. Four more items share this root cause (A-002 external open dropped after the first window closes, A-012 update check ignores torn-out windows, A-019 saved tab list, A-020 tear-out geometry).
2. **A-003. Uninstall cannot close the app when Close to System Tray is on.** `App.xaml.cs:329` answers the uninstall close command with `mw.Close()`, and `OnClosing` lets `_tray.HideInstead` swallow it. The mutex is never released and the uninstaller fails after 60 seconds with 1618. Fix: route the uninstall branch through `CloseToTray.Exit()`.
3. **A-016. Cancelling Print Preview mid-job still prints, then crashes.** `Controls/PrintPreviewWindow.cs:1485`: nothing reads `Canceled` in the render loop or spooler, then `DialogResult = ok` on the closed window throws, the catch sets a closed owner and throws again out of the async void into the crash dialog. Fix: thread a cancellation token through DoPrint and the spooler, and guard the post-await code with `IsLoaded`.
4. **B-004. Signing with a .pfx leaves a persisted copy of the private key in the Windows key store.** `Services/Signing/PfxFileCertificateProvider.cs:16` loads with `PersistKeySet`. Fix: use `EphemeralKeySet` (the engine corpus already does) and dispose the certificate after signing. Long-standing since 1.5.6, but it is a one-line fix to a key exposure.
5. **B-007. Extract Pages, Protect and CLI decrypt publish through a path with no flush and no backup**, contradicting the changelog's "recovery copies retained after failed replacements". `Services/PdfEngineIntegration.cs:2697` and `Services/PdfEngineBurn.cs:733` use WriteAllBytes plus Move. Fix: replace both bodies with `AtomicFileOutput.Write`.

### Engine

6. **C-001. Unlocked Type 1 glyph cache races under concurrent rendering.** `engine/KillerPdf.Engine/Fonts/PdfType1GlyphReader.cs:14-15` caches in plain dictionaries while `PdfExtractionFont.GetGlyphOutline` calls it concurrently from viewer, prefetch and thumbnail threads. It is the exact sibling of the CFF race fixed in `e514661`, and every LaTeX PDF hits it. Fix: the same `System.Threading.Lock` pattern as `PdfCffGlyphReader.cs:19` plus a concurrency test.
7. **C-002. A missing or malformed color space blanks the whole page.** `Rendering/PdfPageRenderer.cs:3558` throws from the `cs`/`CS` handlers and nothing between the operator and `Render` catches it, so a stripped `/CS0` resource gives the user an empty page where PDFium fell back to gray. Fix: compatibility catch in the color-space handlers with a DeviceGray fallback and a diagnostic. The referee's structural recommendation covers this and six more whole-page failures (C-003, C-004, C-005, C-008, C-010, C-018): a per-instruction catch in `Process` and a per-annotation catch in `RenderAppearances` when compatibility recovery is on.
8. **C-014. A hostile JBIG2 symbol dictionary pins a render thread forever.** `Filters/Jbig2/SymbolDictionary.cs:371` makes no progress when every height class is immediately out of band, and `ArithmeticDecoder.ByteIn` folds end-of-data into the register without ever throwing. No cancellation reaches the decoder. Fix: end-of-data termination in `ByteIn`, bound the height-class loop, and thread a cancellation token into `PdfJbig2Decoder`.
9. **D-002. The raster redaction fallback leaves the redacted text bytes in the file.** `Documents/PdfContentRedaction.cs:130` marks a page Raster when a font is unreadable or undecodable; `Rewrite` then runs the rewriter in text-only mode (`:258`), `Show` returns null for that font (`:1439-1456`), and `ShowAndEmit` re-emits the original `Tj`/`TJ` under `Tr 3` (`:1401-1403`). The covered glyph codes stay in the content stream under the black box, and Verify cannot see them because it decodes with the same font. A deeply nested Form XObject is likewise emitted unchanged. Reached from Redact, the Privacy starter and the CLI redact verb after the raster-approval prompt. Fix: in text-only mode drop any text-showing operator whose string could not be decoded and drop the `Do` when FormNesting fires; harden Verify for raster pages; add a fixture with a broken-CMap font under a redaction box.
10. **D-014. Signature verification reports a timestamped signature with an expired certificate as Invalid.** `Signing/PdfSignatureTrustOptions.cs:13` verifies at `DateTime.Now`, never at the RFC 3161 token time, and revocation defaults to NoCheck. The sidebar shows "Invalid" for a signature Acrobat shows as valid. Fix for 1.9.0: map expiry alone to Incomplete, verify at the timestamp token time when a verified timestamp exists, and have `CertificateCatalog` request offline revocation.

### Release pipeline

11. **E-001. release.ps1 aborts at its own landing-page translation gate.** Five pages use `nav_engine`, `nav_corpus` and `mcp_release_loading`, and `pdf-landing/kp-i18n.js` defines them only in the first (Slovenian) dictionary. I confirmed this with grep, and the defender and referee each reproduced the gate logic read-only: 15 locales report missing=3 and the script throws at `ru` before building anything. Fix: add the three keys to the other 15 dictionaries.
12. **E-002. Code signing is broken.** SignTool cannot use the SimplySign private key (`validation/records/release-installer-20261024.md`). `release.ps1:48` forbids unsigned publishing, the launcher enforces Authenticode before installing, and the Store rejects unsigned packages with 1625. Nothing ships until this works. This is Steve's machine and card, not code.
13. **E-003 and E-011. The Explorer and Outlook preview handler crashed in its only real acceptance run and is still built, offered in setup and advertised in the changelog (#410).** It also registers `DisableLowILProcessIsolation=1` (`Services/PreviewHandlerRegistration.cs:54`), so a brand-new parser runs Outlook attachments at medium integrity. Either reproduce and fix on SN01 or SN02 with the exported dump, or pull it: hide the wizard option, drop the #410 line, remove the four payload entries and the publish block at `build/build-portable.ps1:89-104`, and move #410 to 1.9.1. If it ships, it ships at low IL with an in-memory font index.
14. **E-004 and F-003. The README that will ship on the tag says "Development build. This branch is not a stable release."** It has no feature list and no GPL corresponding-source link, so release.ps1's README rewrite at line 570 is a silent no-op and the Chocolatey docsUrl points at it. Fix: write the 1.9.0 README before the merge, with the `releases/download/v1.9.0/KillerPDF-1.9.0-src.zip` line the script expects, and add a preflight that fails on "Development build".
15. **E-005. Accuracy gate A1 is open.** 572/573 conformance and 770/769 PDF.js hashes changed against the accepted baseline at 512 and 2048 and are undispositioned. The gates file says "This is not a release pass." Fix: rerun A1 on the candidate commit and disposition every change, or have the gates file say explicitly that 1.9.0 ships with accepted differences.
16. **E-006 and E-013. Speed gates S1, S3 and S4 are labelled release blocking and fail at the only recorded measurement**, taken at `096a2cf` against v1.8.80 on October 10: S1 +10 to +30 percent on four of five documents, S3 580 to 730 ms versus about 360 ms, S4 +12.7 percent at 512 and +15.9 percent at 2048. M2 and M3 fail likewise (+61 percent at 512 on the difficult set, +87 percent on the JBIG2 scan). No measurement exists for the current candidate. This is a decision for Steve, below.
17. **E-008 and F-002. The Microsoft Store listing says "PDFium rendering" in all 20 columns**, and `store/Submit-StoreRelease.ps1:102-109` pushes that CSV verbatim on every submission. Fix: rewrite row 8 of `store/KillerPDF-Store-listing.csv` in English, regenerate the 19 translations, and grep the CSV for "pdfium" as a preflight.

### Public text that misleads

18. **F-004. The only help topic on editing existing text describes the pre-1.9 cover pair** and says the underlying text is not removed. `pdf-landing/help.html:158`, with `kp-site-info.js:279/307` re-injecting the sentence in every language. There is no help topic for paragraph editing or Find and Replace. Fix: rewrite ph_50 to ph_59, add the two topics, drop the injection.
19. **F-016. The Technical page calls the shipped product "The 1.9 development branch" in 20 languages**, and `kp-site-info.js` flags every 1.9.x version as development through a `/^1\.9\./` regex. Fix: reword `tech_19_pipeline` and replace the heuristic with an explicit released flag.

Items 12, 13, 15 and 16 are the four that are decisions or lab work rather than edits.

## Features that still need wiring

These are places where 1.9 promises something and the code does not quite deliver it, or where a feature is reachable but dead-ends. Hours are the referee's estimates.

| Id | What is missing | Hours |
| --- | --- | ---: |
| F-001 | Paragraph editing refuses tagged PDFs, marked content, clipped pages, Form XObjects and signed documents, and the UI only says "horizontal text in one style". Surface `PdfParagraphInspection.Reason` when no paragraph is found. | 3 |
| F-009 | The Validate macro step is inert: its result never affects the file outcome, so the Archival, Sharing and Privacy starters report Succeeded on a failing preflight. Make it fail the file or surface the result. | 4 |
| F-010 | The Automatic folders dialog lists macros the watched-folder service refuses (Redact, Data Merge, Export) and only rejects them on Save with a generic message. | 2 |
| F-011 | Watched-folder failures drop the engine's reason, and an OCR step whose language model is not downloaded fails every file. Keep `result.Error` and check models at configure time. | 3 |
| F-012 | Export Document promises a loss review but shows only a count. List the findings. | 3 |
| F-013 | Form scripts outside the safe subset are skipped silently, and computed Format and Validate results are never displayed. | 3 |
| F-019 | Fifteen of the 25 macro operations show English CamelCase names in every locale. Share one localized name table between MacroDialog and WatchedFolderDialog. | 2 |
| F-026 and B-008 | One corrupt macro JSON hides the whole library in the dialog, the CLI and watched folders. Parse per file. | 1 |
| F-023, F-027 | Paragraph dialog drops the error message; the Validate step editor resets the profile to General on Apply. | 1 |
| F-006 | "Portable copies run directly" for the CLI is false: both the launcher and `KillerPDF.App.exe` are WinExe, so PowerShell cannot wait on the portable exe or read its exit code. Text fix now (help ph_317 and the changelog line), real console shim in 1.9.1. | 1 |
| A-013, A-014 | Two CSV outputs (`--batch-render` log and the measurement report) lack the formula guard the changelog claims for "batch CSV reports". | 2 |
| A-017 | Collapsible option sections in Print Preview and Transform are mouse-only Borders with no UIA role. | 2 |
| A-022 | No `SessionEnding` handler, so Windows logoff or shutdown discards unsaved work without a prompt. | 2 |
| A-008, A-009, A-010 | Deleting an annotation never marks dirty; re-editing a text box has no undo entry; annotation undo entries remove by reference and go stale after a snapshot undo. | 4 |
| A-006, A-007, A-023 | Transform, Straighten and Rotate clear or remap annotations before `SaveTempAndReload` with no rollback on failure. One shared backup helper. | 3 |
| D-006, D-025 | Form flattening paints Hidden and NoView widgets, and refuses any form with a check box lacking an appearance dictionary. | 3 |
| D-007, D-008, D-009 | Co-signing refuses forms with `/NeedAppearances`; `PdfSignatureReader` aborts every full rewrite on tolerated AcroForm shapes and on a missing optional `/Type /Sig`. | 4 |
| D-010, C-019 | AES-256 documents without a top-level `/Length` cannot be opened. | 1 |

Deferred wiring items (F-021, F-022, F-024, F-028, F-029, F-030, F-035, F-036, F-037, F-039, F-040, about 16 hours) are in `referee-F.json` and belong in 1.9.1.

## Things we are missing

- **The 1.9 marketing surface does not exist.** None of the 1.9 Added features has a feature card on `index.html` or a help topic: redaction, protection, signature inspection, Office export, Convert Scan to Text, form tools, guided tasks, accessibility review, repair, scanning, production tools (F-018, about 8 hours of English copy plus translation). The site still says the app "runs OCR on Tesseract" and is a "light editor" (F-033). Eighteen translated about-page paragraphs and the Russian and Hungarian feature cards still say PDFium (E-009).
- **Third-party notices are incomplete.** `engine/THIRD-PARTY-NOTICES.txt` omits Tesseract (Apache-2.0), Leptonica (BSD-2), ONNX Runtime (MIT), SixLabors.Fonts (Apache-2.0) and CommunityToolkit.Mvvm (MIT), all of which ship in the payload (E-010).
- **Hosted CI has not seen 592 commits.** `origin/dev/1.9-overkill` is at `dd7cbcc` from October 12. The PR build workflow has not run on the candidate (E-012). Pushes need Steve's approval.
- **Two remaining functional acceptance items**: persistent power-loss recovery and sleep/resume (E-024). Both SN guests report no Windows sleep state; power loss can be done with a hard VM stop.
- **Help documents 9 of 64 CLI verbs**, none of them 1.9 (F-008), exit code 3 is undocumented (F-007), and `--help` omits options the handlers accept (F-034).
- **Milestone hygiene.** 48 open v1.9.0 issues: 36 done, 6 partial (#430, #423, #420, #415, #413, #338), 3 not started or declined (#417, #414, #390). #390 is labelled wontfix and #414 was declined in a comment, yet both sit on the milestone. #338 is done in code but uncited in the changelog and its last comment is stale. #413's reporter says "still same issue" and 1.9 does not cite it.
- **Forward ports are fine.** The bug-finder claimed 1.8.4 to 1.8.91 fixes were absent from 1.9; the defender and referee both disproved it (every named issue has a same-day port, and `Check-ForwardPorts.ps1` reports only README and landing bookkeeping as unmatched). No action.

## Should-fix items worth the hours

The full list of 109 is in the JSON. The ones I would take inside the two days, because each is small and user-visible:

- Engine robustness in compatibility mode: C-003 font width defects, C-004 bad operands, C-005 oversize image declarations, C-008 malformed annotation Rect, C-010 sub-pixel dashes, C-011 short AES strings, C-013 bad hex strings and name escapes, C-015 salvaged Flate rows, C-016 truncated baseline JPEG (the most common real-world defect on the list, and the changelog claims damaged JPEG filters were corrected), C-017 page referenced twice, C-020 trailer-only recovery, C-021 missing EOD markers, C-023 CCITT `/Rows 0`. Most are one to five lines.
- C-006 reversed annotation Rect mirrors the appearance; C-007 NoView annotations are drawn on screen; C-009 soft masks that are not literally DeviceGray drop the image.
- C-012 uncached whole-file scans for every dangling reference in compatibility mode (performance on damaged files).
- D-001 incremental update writes `/Prev` pointing at the header after a scan rebuild. No shipped path writes it to a user file today, which is why it is not a blocker, but the engine library does.
- D-003 full rewrite refuses any dangling indirect reference, which blocks Save As, Reduce Size, encrypt and redact on ordinary real-world files. D-004 Reduce File Size compresses the XMP metadata stream and breaks PDF/A. D-005 resource pruning drops resources used by resource-less Form XObjects and inline images.
- B-001 and B-006 cancelling Flatten or Export Images mid-page shows an error dialog. B-002 Flatten holds every page's BGRA buffer until the end (OOM at high DPI). B-003 macro names over 123 UTF-8 bytes cannot be saved. B-018 WebP and HEIC placed images vanish from burned output.
- A-004 Export Images burns outside its try; A-005 Delete key form-field branch is hard-wired to pane A; A-015 the file picker probes Quick Access and drives synchronously before appearing; A-037 file picker fade re-entry; A-042 and A-043 CLI exit-code contract and decrypt in-place guard.
- E-010 notices, E-014 release-day dating (both changelogs and `ReleaseDate`), E-015 exercise `/silent` on the signed Store exe before publish, E-022 engine tests run twice in release.ps1, E-023 the site language menu offers nb, pt and uk with no dictionaries.

## Gate status as of this review

| Gate | Status | What closes it |
| --- | --- | --- |
| A1 regression | Open, undispositioned hash changes | A1 rerun on the candidate plus dispositions, or an explicit accepted-differences statement |
| A2 three-oracle | Closed at 5a6ef60 | Nothing |
| A3 difficult set | Closed at 5a6ef60 | Nothing |
| A4 viewer equals headless | Closed at e514661 | Nothing |
| S1, S3, S4 (blocking) | Fail at 096a2cf vs 1.8.80; no candidate measurement | LatencySmoke run on SN01 against v1.8.91, then pass or a dated waiver |
| S2 | Passes at 096a2cf | Re-measure in the same run |
| M1 | Passes | Re-measure in the same run |
| M2, M3 | Fail at 096a2cf | Same run; the new `--live-viewer-memory` mode is now committed |
| Build, suites, vulnerabilities | Pass on 36436c9 (this review) | Nothing |
| Signed installer, Store 1625 strict path, A009 | Open, signing broken | SimplySign repair |
| Preview handler native acceptance | Failed (CoreCLR crash) | Fix on VM or pull from release |
| Power loss, sleep/resume | Open | SN01 hard stop test; sleep needs a host that supports it |
| Hosted CI on candidate | Not run since October 12 | Push |

## Plan: two days to releasable

The plan assumes Steve makes the three decisions below on day one morning, and that the signing repair runs in parallel on his side. Code work runs in separate sessions on separate files so nothing collides; every fix gets a failing test first and a `v1.9.0:` commit. I would not merge to main or tag until the gates table above has a row for the candidate.

### Decisions needed from Steve (ask once)

1. **Speed and memory gates.** Re-measuring S1 to S4 and M1 to M3 on SN01 against v1.8.91 takes most of a day of lab time and may still fail by 10 to 30 percent. Options: run it and ship only if it passes; run it and ship with a dated waiver if the numbers are within the October 10 range; or relabel S1, S3, S4 as informational for 1.9.0 and drive them down in 1.9.x. My recommendation is the second: run it, publish the real numbers in the corpus report, and ship with a waiver, because 1.9 was never going to beat native PDFium on cold launch in its first release and the accuracy gates are closed.
2. **Preview handler.** Pull it from 1.9.0 and ship it in 1.9.1 once it survives Explorer on a VM at low integrity. Shipping a shell extension that crashed in its only real test is worse than shipping without it.
3. **A1.** Accept the current renderer output as the new baseline after the A2 and A3 dispositions (which already cover the outliers), and record that in the gates file. The alternative is a day of hash triage that the A2 and A3 work has largely already done.

### Day 1

Morning, in parallel:

- **Steve:** repair signing. With SimplySign Desktop running and the card unlocked, check the key provider with `certutil -user -store My E478E4940DFD3547DAF2199494B399214FB3E0FD`, then `signtool sign /debug /fd sha256 /sha1 <thumbprint> /tr <tsa> /td sha256 <scratch exe>` to see which filter drops the certificate. If the CSP is the problem, re-register it or sign from Windows PowerShell 5.1. Then approve a push of `dev/1.9-overkill` so hosted CI runs on the candidate.
- **Session 1, app shell (A-001 group, A-003, A-016):** fix the torn-out window lifecycle (cleanup in `App.OnExit`, a `PrimaryWindow()` helper replacing `Application.MainWindow`, dirty check across all windows), route the uninstall close through `CloseToTray.Exit()`, and make Print Preview cancellable. Add a `SessionEnding` handler (A-022). About 6 hours.
- **Session 2, engine read (C-001, C-002 structural, C-014):** lock the Type 1 glyph cache with a concurrency test; add the compatibility-mode per-instruction and per-annotation catch with the DeviceGray fallback and diagnostics (this also closes C-003, C-004, C-005, C-008, C-010, C-018); bound the JBIG2 symbol dictionary and thread cancellation into the decoder. About 6 hours.
- **Session 3, engine write (D-002, D-014):** drop undecodable text and nested forms in the raster redaction path and harden Verify with a broken-CMap fixture; make expiry map to Incomplete and verify at the timestamp token time, with expired-with-TSA and expired-without-TSA fixtures. About 6 hours.
- **Session 4, services and release script (B-004, B-007, E-001, E-010, E-022, E-023):** `EphemeralKeySet` plus dispose; `AtomicFileOutput.Write` in both replace paths with a source test; the three landing keys in 15 dictionaries; third-party notices; remove the duplicate engine test run; drop nb, pt, uk from the language menu. About 4 hours.

Afternoon:

- **Session 5, public text (F-002, F-003, F-004, F-016, F-006, F-007, F-015, F-020, F-033, F-038, F-042, E-008, E-009, E-014 preflight):** the 1.9.0 README with feature list and the GPL source line; Store CSV row 8 in English and 19 translations; help topics for Text-tool editing, paragraph editing and Find and Replace; Technical page reworded and the development heuristic replaced with a released flag; PDFium references purged from the site dictionaries; the small help corrections; a release.ps1 preflight that fails on "Development build" and on "pdfium" in the Store CSV. This is a full afternoon of writing plus translation.
- **Lab, SN01 (if decision 1 says run):** build the Release candidate from the day's head, claim SN01, run `tests/KillerPDF.LatencySmoke` S1 to S4 against v1.8.91 with one warmup and five runs, then the new `--live-viewer-memory` mode for M3 and the headless raster sums for S4, M1 and M2. Record every sample in the gates file with the commit hash.
- **If decision 2 says pull:** remove the preview handler from the wizard, the payload inventories, `build-portable.ps1:89-104` and the changelog, and move #410 to 1.9.1.

End of day 1: full engine and window-free app suites green, Release build clean, commits in.

### Day 2

Morning:

- **Wiring batch (F-001, F-009, F-010, F-011, F-012, F-013, F-019, F-023, F-026, F-027, A-013, A-014):** about 24 hours of referee estimate, so split across three sessions by area (macros and watched folders; paragraph and export and forms; CSV guards). Each is small and self-contained.
- **Engine should-fix batch:** C-016 truncated JPEG, C-011 short AES strings, C-013 tokenizer leniency, C-015 and C-021 salvaged and unterminated streams, C-017 duplicate page refs, C-019 and D-010 AES-256 `/Length`, C-006, C-007, C-009, D-003 dangling references, D-004 metadata compression, D-005 pruning, D-006 and D-025 flattener, D-007 to D-009 signature reader tolerance. Two sessions, read side and write side.
- **App should-fix batch:** A-004 to A-010 annotation dirty and undo bookkeeping and page-op rollback, A-017 collapsible sections, A-037, A-042, A-043, B-001, B-002, B-003, B-006, B-018.
- **Changelog accuracy pass:** narrow the "editable resize, color conversion" claim (F-005), fix the portable CLI sentence (F-006), add #338, drop or move #410, and re-verify every Added bullet against the day's code.

Afternoon:

- Run the full engine suite, the window-free app suite and the `build/Test-*.ps1` runner tests. Run the window-opening tests and the UI smoke suites on SN02, not on SHREDDER.
- Milestone cleanup: close #390 and #414 with a note, re-milestone the reporter-pending issues (#417, #420, #423, #430) to 1.9.1, comment on #338 and #413.
- Second three-stage review of only the day 1 and day 2 commits (the chained-review pattern that produced the October 23 record), then fix anything valid.
- Signed candidate rehearsal once signing works: `release.ps1 -DryRun` and the signed installer smoke, strict Store 1625 path, `/silent` on the Store exe, and the power-loss hard stop on SN01.

End of day 2: candidate built, signed, rehearsed; gates file updated with real numbers or dated waivers; README, site, Store and changelog describe the product that ships.

### Release day (Steve, with approval at each push)

1. Date both changelog headings and `ReleaseDate`.
2. Make main the 1.9 branch per BACKLOG.md: `release/1.8` from main, `git merge -s ours origin/main` on the 1.9 branch, fast-forward main, point PR CI at main and `release/1.8`.
3. `release.ps1`, then drag `pdf-landing/` into Cloudflare Pages, then `StoreUpdate.ps1 KillerPDF`.
4. Delete `C:\Users\steve\code\Check-ForwardPorts.ps1` and update CONTRIBUTING.md to target main (E-019).

## Scope limits

- Static review plus the builds and suites listed above. No app window was opened, no VM was claimed, no benchmark ran, nothing was pushed, signed or published.
- Not audited: CoreJ2K internals, the MMR and refinement region decoder bodies beyond loop structure, ICC transform math, the KillerLauncher installer UI code paths beyond the registration and payload scripts, and the 20 app locale files for translation quality (parity was verified: all 20 have the identical 1,832 keys with no empty values).
- Window-opening tests and the UI smoke suites were not run. They last passed on SN01 and SN02 on October 24 (`window-tests-steevnet-20261024.md`).
- The 12 invalid findings are listed in `merged-ABCDE.json` and `referee-F.json` for the record: A-039, B-017, B-019, B-020, B-022, B-025, D-022, E-007, E-025, F-017, F-031, F-041.
