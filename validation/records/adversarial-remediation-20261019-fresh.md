# Fresh review remediation

October 19, 2026. Local remediation checkpoint against the fresh review in `adversarial-review-20261019-fresh.md`.

This record distinguishes repaired behavior from missing acceptance evidence. It does not authorize a release. Existing unrelated working changes were preserved.

| Finding | Current result | Evidence or prerequisite |
| --- | --- | --- |
| BF-E001 | Repaired and committed in f1a21b2 | A public save-repair regression failed before the change. Partially overlapping crop boxes, including valid sub-point boxes, now remain byte-identical; before/after render bytes match. |
| BF-E002 | Repaired and committed in f1a21b2 | Three strict-reader catalog/page generation cases failed before repair and pass afterward. Repair plans preserve object generations. |
| BF-B004 | Repaired and committed in d913da4 | Actual CLI previously returned success and one output for two distinct same-name inputs. It also overwrote source inputs when the destination matched their directory. Four regression scenarios pass in Windows PowerShell 5.1 and PowerShell 7. |
| BF-L002 | Repaired in KillerShot commit 96d44b9 | Eight public-control states now measure equal 8px gaps; collapsed renders remain identical and the splitter hit target remains 6px. See [Shot evidence](C:/Users/steve/code/KillerShot/validation/records/dock-spacing-20261019/README.md). Real drag and full-window acceptance remain unverified. |
| BF-F001 | Repaired and committed in 8f8f2f5 | Two synthetic documents, distinct embedded marks, right pane active, actual macro dialogs and output inspection. SECRET is removed while KEPT-RIGHT survives. |
| BF-F003 | Repaired and committed in 4cec84c | Save-only and Data Merge macro outputs preserve a pending 90-degree rotation; Data Merge also writes its selected record. |
| BF-F004 | Repaired and committed in 36c3734 | Extraction retains the edited Updated value and exactly one 90-degree rotation. |
| BF-F002, BF-F006 | Repaired and committed in 87ce884 | Rotated print preview is landscape, 1536x921. A queued source-document switch produces a visible cancellation status and no stale preview. |
| BF-F007 | Repaired and committed in b0f9796 | Six actual-control workflows cover dismissal, document and pane switching, direct successful field selection, creator cancellation, and removal from the original visual parent. See [signing evidence](signature-picker-20261019/README.md). |
| BF-L001 | Repaired and committed in df0bf44 | All 48 representative theme, side, scale and expansion cases pass. Overflow retains original rail and header controls; bounded strip height preserves document space and fullscreen hiding. All 13 wide-window command bounds remain identical. See [rail evidence](rail-overflow-20261019/README.md). |
| BF-L003 | Repaired and committed in 08ec7e2 | All 24 representative renders pass; every complete footer row remains pixel-identical. Twelve paired boundary differences are at most 0.7042. Live Dark/98SE/Light switching passes. See [shadow evidence](rail-shadow-20261019/README.md). |
| BF-B001 | Partially repaired in 5c0dfa9 | Word and Excel disable VBA during Open and restore security on success or failure. Twenty focused tests pass through the external COM boundary. Excel 4.0 macros and real Office trust-policy acceptance remain unresolved. See [Office evidence](office-automation-20261019.md). |
| BF-B002 | Guard committed in 7716824; lab acceptance pending | Twelve production destination-validation cases pass. Unknown contents, altered payloads and junctions refuse replacement. Legacy or modified installations can also refuse replacement. Real registration and rollback remain untested. See [installer evidence](installer-destination-20261019.md). |
| BF-B003 | Handoff committed in 6e8c95e; lab acceptance pending | Real batch commands deliver the custom destination to an external capture process for both scopes, ampersands and Unicode. Actual installation and relaunched-version checks remain pending. See [update evidence](update-handoff-20261019/README.md). |
| BF-B005 | Scoped forwarding committed in 89cf2b1 | Two real named-pipe subprocess checks pass: logical session isolation and nonzero failed-forward result. Actual distinct Windows sessions, elevation and old-version coexistence remain unverified. See [session evidence](session-temp-boundaries-20261019/README.md). |
| BF-B006 | Ownership repair committed in f6d9ea8 | Six filesystem/subprocess cases preserve active files, reclaim abandoned owned files, avoid junctions and release shutdown leases. Isolated actual application startup/shutdown also passes with no leftover smoke root. Normal startup against concurrent CLI still needs lab acceptance. See [temp evidence](session-temp-boundaries-20261019/README.md). |
| BF-B007 | Deadline repair committed in 3184d41 | Eight public transfer checks pass, including a real socket body stall canceled at 272ms for a 250ms deadline. Actual HTTPS GUI and fixed OCR endpoint stall acceptance remain unverified. See [network evidence](network-csv-20261019/README.md). |
| BF-B008 | Confirmed and repaired in b9acda4 | Actual original CLI CSV evaluated =1+1 as formula 1+1, cached 2, in Calc with formula evaluation enabled. Repaired actual CLI output imports as text; seven filename cases pass without modifying inputs. Calc visibly retains the neutralizing apostrophe. See [CSV evidence](network-csv-20261019/README.md). |

The macro window now includes the existing current document and selected PDFs choices. Commit f1639a3 removes the separate chooser after Run. Both source-choice tests pass. [Document-state evidence](document-state-20261019/README.md) includes all nine workflows, exact source hashes and retained screenshots.

## Verification

- Seven focused sanitizer checks pass after observed failures.
- Prior full engine suite: 6,652 passed, zero failed, zero skipped. No engine source changed in this continuation.
- Final application suite: 896 passed, zero failed, zero skipped.
- Initial full application run passed 895 and failed one localization scanner assertion on synthetic smoke-test form values. Commit f80211b excludes test tooling, matching the product source glob; the complete rerun passes.
- All nine document-state and macro-source control workflows pass.
- All six signature-picker workflows pass.
- KillerShot Debug build: zero warnings and errors.
- Bates candidate build and actual CLI regressions pass.
- Final combined v6 candidate builds with zero warnings and errors. Its source manifest records 1,657 inputs with zero copied-source mismatches.
- Horizontal rail matrix: 48 cases pass at 620x800, covering Dark, Light and 98SE, Top and Bottom, open and collapsed, and app scales 0.7, 1.0, 1.5 and 2.5. These are representative scale values, not every intermediate setting.
- Broad corpus, native DPI, physical printing, hardware and release gates remain unverified by this work.

## Boundary continuation

Three standard workers handled network/CSV, session/temp ownership, and Office/installer; the coordinator handled update arguments. All six previously remaining accepted findings now have bounded local changes, with BF-B001 still partial. Other findings retain the acceptance limits in the table. Guest access was explicitly confirmed unavailable in this continuation.

The combined Release application build passes. A fresh snapshot build reported 671 warnings and zero errors; the final incremental build after lease lifecycle integration reported zero warnings and errors. Eleven task-owned application sources match the final snapshot byte for byte. Final KillerPDF.dll SHA-256 is BE232EAC0F84918D4C2FD338471EB75EC49553DEA5C33501784C1C76F84FF837. The final combined application also passes the actual CLI/Calc report replay and isolated `--startup-smoke`, including theme picker open and graceful exit. Smoke exit is 0 and its generated temporary root is removed.

Continuation commands and snapshots are under `C:/Users/steve/scratch/kp-boundaries-20261019`. Final test results are `combined/test-results/app-tests-final.trx`; source comparisons are `combined/source-hashes.json`; startup output is `combined/startup-final/result.txt`. These cited artifacts remain for the unfinished lab work. They contain synthetic inputs only. No full installer, adversarial Office fixture, registry cleanup, or second-session creation ran on the host.

The original review evidence includes diagnostic `.cs` recipes under validation. A normal application build included those untracked recipes and failed on duplicate entry points and Shot-only types. Worker builds use isolated source copies excluding the exact historical evidence directory; the product project was not changed to hide source errors. Source manifests record the copied inputs.

Temporary runs are under `C:/Users/steve/scratch/kp-fresh-remediation-20261019`. Automatic approval review rejected removal of the generated evidence copy in the first scratch snapshot with the reason "blocked by policy". That copy was preserved and a second snapshot was created instead.

Automatic approval review also rejected removal of the owned generated `tests/KillerPDF.RailSmoke/obj` folder with the same reason. It was left untouched. Subsequent test builds redirect intermediate output into scratch. Scratch remains available for the unfinished lab investigations.

The smoke host's settings override does not redirect its separate temporary-file root. Early diagnostics inherited the system temporary directory. Retained runners now set process-only TEMP/TMP before loading the application and record the exact generated smoke root under their scratch output. Unknown earlier roots were not broadly deleted because ownership could not be established.

No push, branch creation, release, issue creation, or external message was performed.
