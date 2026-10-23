# KillerPDF release review, October 21, 2026

**Disposition: hold the reviewed candidate.** The three-stage review found ten valid defects: five high, four medium, and one low. Later focused repair checks provide partial evidence, but they do not close the acceptance requirements below or establish release readiness.

## Scope and provenance

| Item | Recorded value |
| --- | --- |
| Repository | `C:\Users\steve\code\KillerPDF` |
| Branch observed during review | `dev/1.9-overkill` |
| Base | `4df9b86e727875163085bb1c4ff2aad01bd8b568` |
| Reviewed head | `e0b9c6e2cd700af893bbf41de270f09e6769b1e0` |
| Diff size | 128 files, 4,602 additions, 302 deletions |
| Review date | October 21, 2026, America/Los_Angeles |
| Worker route | Standard inherited worker |
| Record preparation checkout | `a63b2b7e91aece976f6e5c2d6475356fc1a3a7c4`, with concurrent uncommitted changes |

Every source line reference in the findings refers to the reviewed head. Later commits and current working changes are a different candidate. This record does not re-adjudicate them or treat the current checkout as the reviewed snapshot.

The Bug-finder inspected the changed production C#, XAML, workflow, script, and test code, tracing surrounding save, editor, session, signing, and output paths. It did not consume earlier reviews. The Defender received only the structured Bug-finder report and independently checked callers, guards, and state. Its production evidence matched the pinned head; concurrent edits to two test files were excluded. The Referee received only those two structured reports and checked its source evidence with `git show` against the exact reviewed head. It did not use the evolving live files or candidate source copy as adjudication evidence.

The original review stages performed no builds, tests, product launches, transport fixtures, screen-reader checks, installer checks, VM checks, corpus runs, or hardware checks. Their verdicts mean a failure path was established in source. Later executed repair checks are listed separately below. Writing this record involved reading those saved artifacts, without running new checks.

The three canonical inputs are in `C:\Users\steve\scratch\kp-release-20261021`. Their SHA-256 hashes at record preparation were:

| Input | SHA-256 |
| --- | --- |
| `bug-finder.json` | `EFC6211270BD43066A35B6E07445369E5D57D19DAF8C6BBB6DA506C975B9E7E7` |
| `defender.json` | `A961B3511BA7AB931E57912182CE2CC8773C0EED61AD1AAC79AC7BB5A92CBA4D` |
| `referee.json` | `8A56FB6B834ABB20DDD651795F33520534C127424272DF1D2ABA81FD811A2151` |

The existing `adversarial-review-20261020-speed.md` was preserved and was not used as review evidence. Changelog claims and older validation reports were also excluded from the three-stage verdicts.

## Verdicts and repair order

| Stage | Result |
| --- | --- |
| Bug-finder | 10 findings: 5 logic, 4 accessibility, 1 code quality |
| Defender | 10 confirmed, 0 false positives, 0 disputed |
| Referee | 10 valid, 0 invalid, 0 numbered findings needing investigation |
| Severity | 5 high, 4 medium, 1 low |
| False-positive rate | 0% of the ten numbered findings |

Three additional concerns remain separate investigations. They are excluded from all finding counts, severity totals, and the false-positive rate. The Referee's subjective health score was 2/10 under the review rubric. That score is not a measured reliability or release-readiness result.

Repair in this order:

1. **BF-002, BF-004, BF-005:** preserve pending edits, conflict history, and failed recovery data before restart or cleanup.
2. **BF-003:** preserve complete document bytes for unchanged copies and co-signing. Investigate external source replacement alongside this repair.
3. **BF-001:** enforce the timestamp deadline through response-body reads and verify signing-window responsiveness.
4. **BF-006, BF-007, BF-008, BF-009:** repair text-provider selection, cache invalidation, search endpoints, and viewport geometry, then exercise the real provider and screen reader.
5. **BF-010:** remove prohibited punctuation from the twenty new strings and verify placeholders and report formatting before shipping.

The recurring data-preservation failures involve treating temporary or newly captured state as authoritative document state. The accessibility failures involve plausible model output that does not satisfy the provider's observable selection, mutation, or geometry contract. Repairs need tests at those public boundaries as well as focused model checks.

## High-severity logic findings

### BF-001: Timestamp response-body reads can outlive the deadline

**Verdict:** valid. At `engine/KillerPdf.Engine/Signing/PdfTimestampAuthority.cs:108-130`, `ResponseHeadersRead` completes the request after headers, followed by synchronous `body.Read(buffer)` without cancellation. The cancellation check runs only after that read returns. A server can send valid headers and leave the body open, blocking the operation indefinitely. Signing is invoked synchronously at `Controls/SignDocumentDialog.cs:368`; publication follows signing at `Services/Signing/PdfSigner.cs:60-64`.

**Defender and Referee check:** the later cancellation check, byte cap, and disposal do not interrupt a read that has not returned. The original verdict established the source path without measuring a timeout.

**Repair:** make every body read honor the deadline, retain the response-size cap and disposal, and propagate failure through `PdfTimestampException`. Keep the dispatcher responsive and publish no failed signing output.

**Acceptance:** a real local HTTP endpoint sends valid timestamp-reply headers and then stalls. The real transport must cancel within a bounded deadline without waiting for peer closure. Verify size limits, failed-output behavior, and the actual signing dialog's responsiveness and error reporting. A memory-stream mock cannot establish network cancellation.

### BF-002: Recovery omits text still in the editor

**Verdict:** valid. `Shell/RenderRecovery.cs:26-39` captures session fields, filters `IsDirty`, and saves the model. `Controls/Viewer/PdfViewer.TabsApi.cs:70-72` and `PdfViewer.Tabs.cs:193-229` omit the live editor. `Shell/ReduceSize.cs:81-103` serializes stored state; text reaches that state through `PdfViewer.TextEditing.cs:997-1079`. A clean session with pending text can be skipped entirely, while a dirty session can recover without its latest visible text.

**Defender and Referee check:** normal save/tab paths commit text, but recovery bypasses them. Deferred LostFocus is insufficient and intentionally leaves the editor pending when the formatting bar has focus.

**Repair:** capture pending text and edit intent before the dirty-session filter, without requiring the failed renderer. Preserve new annotations, edits to page text, and re-edited annotations.

**Acceptance:** in an isolated application session, invoke controlled recovery with each pending editor type, formatting-bar focus, and both panes. Reopen the outputs and compare every typed character and edit intent. Prove capture can complete without drawing. A clean manifest test alone does not cover this boundary.

### BF-003: Unchanged signed copies can omit visible measurements

**Verdict:** valid. `Shell/FileOperations.cs:173-208` extracts measurements into overlays, replaces the working path with a stripped temporary PDF, clears the published-file stamp, and marks the session clean. `Shell/DirtyTracking.cs:65` then returns that working path. The signed exact-copy paths at `FileOperations.cs:1336-1348` and `1459-1478` copy it without restoring the overlay measurements.

**Defender and Referee check:** measurement removal is an incremental annotation update at `Services/PdfEngineIntegration.cs:1358-1370`. The restrictions at `engine/KillerPdf.Engine/Editing/PdfIncrementalAnnotationEditor.cs:1100-1105` concern certification permissions, so they do not exclude an ordinary approval signature. A signature field can remain while measurements are omitted. This finding does not assert that every such signature becomes cryptographically invalid.

**Repair:** retain immutable bytes for the complete loaded or last-published document separately from the editable working file. Use the complete source for unchanged Save As, Save Copy, and co-signing.

**Acceptance:** open an approval-signed PDF with supported distance and area measurements. Unchanged Save As and Save Copy must be byte-identical to the original and retain measurement and signature state. Co-signing must append only the intended signature while preserving prior content and measurements. Include certification-permission controls and external-source replacement checks.

### BF-004: Recovery replaces the original conflict baseline

**Verdict:** valid. `Controls/Viewer/PdfViewer.Tabs.cs:208` captures `LoadedRevision`, but `Services/RenderFailureRecovery.cs:10` omits it from the manifest. `Shell/RenderRecovery.cs:101` captures the destination's current revision during reopen. If edited A is recovered after another program saved B, `Shell/FileOperations.cs:1171` compares B with that fresh B baseline and misses the conflict.

**Defender and Referee check:** `Services/FileRevision.cs:31-34` protects an unknown baseline, but recovery supplies a non-null current baseline and bypasses that protection.

**Repair:** persist and restore the pre-crash revision. If it is unavailable, use an unknown baseline that requires a conflict decision. Never establish trust by recapturing the destination during recovery.

**Acceptance:** load A, edit A, externally replace the destination with B, recover A, and invoke each in-place save route. Require Replace, Save As, or Cancel before B changes. Cancel must preserve B. Repeat with deleted and recreated destinations.

### BF-005: Partial recovery can discard the failed tab's remaining data

**Verdict:** valid. `Shell/RenderRecovery.cs:42-45` catches a failed snapshot without recording the failure. `App.xaml.cs:415-436` proceeds to temporary-file cleanup and process exit even if recovery or restart fails. `Services/ProcessTempFiles.cs:62-69` attempts deletion of every registered path. A failed temp-backed tab can lose its remaining working bytes, while in-memory edits disappear on exit. The message at `RenderRecovery.cs:110-113` reflects successful entries without identifying failed tabs.

**Defender and Referee check:** continuing with other tabs is reasonable, but the failed tab has neither retention nor accurate reporting. The original user file may survive; that does not preserve its unsaved working state.

**Repair:** record an outcome for every document, preserve failed working bytes and available serialized edit state outside normal cleanup, and retain those assets across current and stale cleanup until recovery is acknowledged. Persist and report partial results even if manifest writing or restart launch fails.

**Acceptance:** separately fail one snapshot among two dirty tabs, manifest writing, and restart launch. Verify successful documents remain usable, failed bytes and state survive both cleanup paths, and the next launch accurately identifies incomplete recovery. Another tab recovering successfully is insufficient evidence.

## Medium-severity accessibility findings

### BF-006: Tagged reading order breaks reverse selection mapping

**Verdict:** valid. `Services/PageReadingText.cs:41-69` sorts structure groups while retaining visual caret IDs. `OffsetOf` at lines 94-98 assumes increasing caret order. In the fixture at `KillerPDF.Tests/DocumentTextModelTests.cs:94-108`, the first caret is 2, so both `OffsetOf(0)` and `OffsetOf(2)` return zero. `Controls/DocumentSurface.cs:107-116` therefore reports an empty selection for the visually selected span.

**Defender and Referee check:** the forward `Segments` mapping is correct for its purpose, but does not repair reverse selection endpoints.

**Repair and acceptance:** explicitly map visual selected spans to reading-order offsets and define disjoint selections consistently with `SupportedTextSelection`. Through the real provider, round-trip `GetSelection().GetText()` and `Select()` for every reordered span, boundaries, and cross-group selections. Verify screen-reader selection announcements.

### BF-007: Same-count text edits leave the reading cache stale

**Verdict:** valid. `Controls/Viewer/PdfViewer.FindReplace.cs:155-180` replaces annotation text without changing its count or working-file identity. `PdfViewer.Reading.cs:148-157` versions the cache only by path, file time, page count, and annotation count. The edit revision incremented at `Shell/DirtyTracking.cs:28` is not included.

**Defender and Referee check:** both reading caches retain their version. The `TextChanged` event at `PdfViewer.Reading.cs:163-170` depends on a later read observing a changed version; repaint and selection notifications do not invalidate this edit.

**Repair and acceptance:** invalidate both caches from a session text-content revision and raise UIA `TextChanged` when the mutation occurs. Read an annotation through the provider, replace its wording without changing count, and confirm immediate updated text and the event without switching tabs. Repeat undo/redo and verify real screen-reader announcements.

### BF-008: Linguistic matches use the wrong source length

**Verdict:** valid. `Services/DocumentTextModel.cs:192-209` maps source UTF-16 positions, then combines a culture-sensitive search index with the query's length. Linguistic comparisons can ignore characters such as soft hyphens, so the query length can differ from the matched source span. `Controls/DocumentSurface.cs:187-188` supplies no correction. Wrong endpoints or an out-of-range exception can result.

**Defender and Referee check:** a nonempty-query guard does not exclude ignorable characters. Exact culture/globalization behavior required execution, but the length assumption was invalid in source.

**Repair and acceptance:** use ordinal matching with the existing position map, or obtain the actual matched source length if linguistic semantics are required. Guard endpoints. Exercise ignorable characters, combining sequences, empty and boundary ranges, both directions, and both ignore-case modes through the actual `FindText` provider. Model checks alone do not establish provider behavior.

### BF-009: Text viewport methods return placeholder ranges

**Verdict:** valid. `Controls/DocumentSurface.cs:129-133` ignores `screenLocation` and returns current-page offset zero. `GetVisibleRanges` at lines 119-124 returns the full current page without viewport or other-page checks. `Controls/Viewer/PdfViewer.Reading.cs:193-206` converts rectangles to screen coordinates without clipping them to the viewport.

**Defender and Referee check:** no delegated implementation supplies the omitted geometry. These methods violate their provider contracts independently of a particular screen reader's navigation strategy.

**Repair and acceptance:** expose page geometry and hit testing through the reading source, locate the nearest appropriate caret, compute visible ranges from viewport intersections and partially visible lines, and clip rectangles consistently. Query real UIA at known rendered positions across scroll, zoom, DPI, continuous layout, and multiple visible pages. Compare results with the actual viewport.

## Low-severity code-quality finding

### BF-010: New report strings contain prohibited punctuation

**Verdict:** valid. The added-lines scan of the pinned diff found twenty introduced lines containing U+2013 or U+2014. All are `Str_CmpRpt_PageCount` additions in the twenty locale files, including `Strings/en-US.xaml:1880`. `Shell/PdfComparison.cs:432-433` uses the strings for both input page-count rows.

**Defender and Referee check:** this is a reachable violation in new content, separate from existing unrelated punctuation and from functional data-loss severity.

**Repair and acceptance:** replace the prohibited punctuation naturally while retaining both format placeholders. Require zero introduced prohibited characters, validate all twenty resource formats, and render comparison reports. This does not certify native-language translation quality.

## Three separate investigation notes

These notes remain outside the numbered verdict totals. Later evidence below can inform them without retroactively changing the pinned review's statistics.

| Concern | Evidence and next check | Priority |
| --- | --- | --- |
| Unseen external source replacement | `Shell/DirtyTracking.cs:60-66,82-90` returns a disk path without comparing the loaded revision before the first save, while `Services/PdfWorkingDocument.cs:12-18,43-54,67-70` retains loaded bytes. Open A, replace the original with distinguishable B, and inspect loaded, visible, selected-source, and output identities through unchanged signed Save As, Save Copy, and signing. An explicit decision must precede using unseen B. Coordinate with BF-003. | High investigation priority |
| Async open failure can clear another owner's pending state | `_asyncOpenPending` forwards to the active viewer. Exception paths at `Shell/FileOperations.cs:344-349,446-452` can run before the ownership checks used by success paths at lines 275-298,410-422,585-600. Pause a repair/protected-open worker, switch panes or start an independent open, then fail the original worker and inspect both owners. | Medium; reproduce the overlap and user-visible effect before promotion |
| Signature policy metadata shape | `engine/KillerPdf.Engine/Signing/PdfSignatureVerifier.cs:230-269` reads a leading OID without validating the rest of its nested sequence. `PdfSignatureVerificationResult.cs:59-67` describes declared identifiers and optional metadata errors separately from cryptographic integrity. Define the metadata contract, then test malformed nested fields with a valid leading OID if full shape validation is required. | Lower; no established signature-integrity bypass and no added numbered blocker |

## Later repair evidence, separate from the pinned review

The following files were present and read during record preparation. Paths in this section are relative to `C:\Users\steve\scratch\kp-release-20261021`. They describe the tested iterations, not a comprehensive pass of the current working tree. Filenames containing `green` do not override a recorded failure.

| Finding | Saved evidence | Verified result and remaining boundary |
| --- | --- | --- |
| BF-001 | `candidate-results/timestamp-body-timeout-red-runtime/timestamp-red.trx`; `candidate-results/timestamp-body-timeout-green-runtime/timestamp-green.trx` | The stalled-body test first failed with the one-second-deadline assertion. The later timestamp class passed 15/15, including that regression in 1.1599368 seconds. This supports the transport repair. The actual signing dialog, no-publication behavior at the application boundary, and real timestamp authority interoperability are not established by this result. |
| BF-002 | `recovery-bf002-red/` contains the input PDF but no completed result record | No completed application acceptance result was verified from the inspected saved artifacts. Pending-editor recovery remains open under the full acceptance matrix above. A fixture's existence is not a passing test. |
| BF-003 and external-source note | `signed-copy-red.log`; `signed-expanded-red.log`; `signed-expanded-green.log`; PDFs in their corresponding folders | Initial and expanded runs record missing measurements; the expanded red also records the external-source failure. The later log passes measurement Save As and externally replaced source, but fails the Save Copy/co-sign group because `Signature1` is already signed. Independent hashes show the later Save As and Save Copy outputs equal that run's signed input. Co-signing and the wider acceptance matrix remain open; the run is a partial pass. |
| BF-004 | `recovery-unit/results/bf004-red.trx`; `bf004-green.trx` | The original revision round-trip regression first failed, then the focused set passed 3/3. This verifies manifest/baseline behavior at the service boundary. It does not verify each real save route, the conflict dialog, or Cancel preserving B. |
| BF-005 | `recovery-unit/results/bf005-retention-{red,green}.trx`; `bf005-manifest-{red,green}.trx`; `bf005-state-only-{red,green}.trx` | Each new slice first failed: cleanup removed retained bytes, incomplete details were omitted, and a state-only failed record was dropped. Later sets passed 5/5, 6/6, and 7/7 respectively. These sets overlap and must not be added as unique test counts. Full multi-tab recovery, manifest-write failure, restart failure, next-launch reporting, and application cleanup orchestration remain open. |
| BF-006, BF-007, BF-009 | No completed acceptance artifact verified in this evidence set | Do not infer closure from source changes, unrelated tests, or successful text-model reading. Provider and assistive-technology acceptance remain required. |
| BF-008 | `candidate-results/reading-search-red/red.trx`; `candidate-results/reading-search-green/green.trx` | Four direction/case combinations first failed with out-of-range exceptions. The later `DocumentTextModelTests` set passed 14/14, including those four cases. This supports the model repair, not actual UIA provider or screen-reader acceptance. |
| BF-010 | No completed twenty-locale acceptance record verified in this evidence set | No closure is claimed for later locale work. Require the current-candidate scan, placeholder checks, and rendered report evidence. |

The recovery tests used a scratch project linking the real `RenderFailureRecovery`, `FileRevision`, `AtomicFileOutput`, and `ProcessTempFiles` sources with their test files. This is focused service evidence, not a full application build or restart test. The recovery application failure exercise has no completed acceptance result in the inspected artifacts. Recovery execution remained paused at this checkpoint; no new application or native failure checks were run to prepare this record.

For the partial signed-copy pass, `signed-expanded-green/measurement-signed.pdf`, `unchanged-save-as.pdf`, and `unchanged-save-copy.pdf` were each 66,983 bytes with SHA-256 `409DBB699610F303268BA4ADE77F81BF009D95445ADB6EAC6458D52A856A7C34`. That proves byte identity for those saved files. It does not turn the failed co-sign operation into a pass or prove every measurement type and certification policy.

Selected evidence hashes make the recorded checkpoint identifiable:

| Artifact | SHA-256 |
| --- | --- |
| Timestamp red TRX | `8073A318D5F554C9685908BC502D7EFA513E938119C00EACB7FD0E3329C0A843` |
| Timestamp green TRX | `9DDB979422F2D9D710795D0B7DE145145AF32AA0D0FBE32338F7ECF3E7DD1612` |
| BF-004 red TRX | `D7A5447F4C211AB2CF36F4F6451C1DB48D52A9D2F384C56969BEB03EF61CE5A1` |
| BF-004 green TRX | `40B67E3F7AAB0A5A8E95FDE33091C261668F514964392AB620B115168D1D6FC9` |
| BF-005 latest state-only green TRX | `2B718A2DFC9BA8C1780B9A61FE43282FA1D2F7E4B0204D3AE6B2E0FCD2179335` |
| Reading-search red TRX | `964D101FDB7683262E1EA792A32C71A44D503DB928E8AA8719082F57AE0B7182` |
| Reading-search green TRX | `6FD3A651E8CD7EFA4A10B2AC118DC2ACC9863068BC829EACBDEAF47661143876` |
| `signed-expanded-red.log` | `07C483E05BB5C71EBD475763B208D3AA76E9CE976606BBA6E5539B2CC331DEAC` |
| `signed-expanded-green.log` | `3A516AFB52D9CD67CC172F09272C615657668272866ADE1B24B40D3C45C53DC8` |

## Proof limits and remaining release gates

The review covers the specified diff and selected surrounding paths. It is neither a full audit of the repository nor proof that no other defects exist. The Defender rebuttal and Referee adjudication cover the supplied findings and separate notes; they do not expand that scope silently.

The record establishes no complete current-candidate pass, native crash recovery, real screen-reader behavior, rendered localization layout, native-language fluency, shader rendering quality, clean installation, signed installer behavior, VM acceptance, corpus parity, hardware behavior, package publication, remote-token access, or release readiness. Embedded shader source was inspected in the original review, without shader disassembly or visual verification.

Before a current release decision, pin the finished candidate, reconcile its repairs against every finding and acceptance check, and preserve the resulting evidence. Re-run only the checks justified by that candidate and its changed boundaries. Complete the application, UIA, screen-reader, rendering, signing, installer, and controlled-lab gates that apply. A build, focused unit pass, source comparison, or this document cannot substitute for those gates. The high-severity data-preservation and signing requirements remain release holds until the corresponding acceptance evidence is complete.

The original stages checked these official API references: [HttpCompletionOption](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-10.0) for response-body timeout scope; [String.IndexOf](https://learn.microsoft.com/en-us/dotnet/api/system.string.indexof?view=net-10.0) for linguistic matches and ignored characters; [RangeFromPoint](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.provider.itextprovider.rangefrompoint?view=windowsdesktop-10.0) and [GetVisibleRanges](https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.provider.itextprovider.getvisibleranges?view=windowsdesktop-10.0) for provider geometry. They are provenance from the adjudication, not a claim of new runtime validation during documentation.
