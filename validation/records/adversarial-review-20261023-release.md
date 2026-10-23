# KillerPDF 1.9 adversarial release review, October 23, 2026

This continues the chained 1.9 release review. [The October 21 review](adversarial-review-20261021-release.md) covered `4df9b86..e0b9c6e`. This record covers everything after it in four rounds, plus the fixes they produced. Each round ran the three isolated stages from the harness contract (bug-finder, defender, referee). The defender received only the bug-finder's structured report, and the referee only the two reports, checking evidence against the pinned head with `git show`. All stages were read-only: no builds, test runs or app launches.

| Round | Range | Reviewed head | Findings | Valid | Invalid | Needs investigation |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| A: 1.9 delta | `e0b9c6e..f8cd0ba` (29 commits, 136 files) | `f8cd0bac61902f66d3f0bfe8db981420b39fe832` | 21 | 19 | 2 | 0 |
| B: #435 and CLI crash fixes | `f8cd0ba..cb4276a` | `cb4276a` | 8 | 8 | 0 | 0 |
| C: repairs from A and B | `cb4276a..5250c80` plus `40bd135` | `5250c80`, `40bd135` | 14 | 13 | 1 | 0 |
| D: repairs from C | `40bd135..7e84b84` | `7e84b84` | 13 | 13 | 0 | 0 |

Two invalid items came from round A. BF-005 is an accurate description of a deliberate, documented trade-off. BF-019 claimed state was never read, but a UI smoke test reads it. In round C, RF-006 was invalid: the comb-overflow message never decides a page's outcome, because every path that emits it also emits a blocking message. Round D had no invalid items. Referee health scores: 5 for round A, 6 for round B, 6 for round C, 5 for round D.

## Round A: valid findings and repairs

| Item | Severity | Finding | Repair |
| --- | --- | --- | --- |
| BF-001 | medium | Render recovery skipped saving the open tab list when no tab was dirty, so the restart reopened the last normal-close set. | 84f1596: settings are saved before any early return; recovered originals leave the restore list only after the manifest exists. |
| BF-002 | medium | One pane's capture failure aborted recovery for every tab. | 84f1596: each pane's capture is isolated. A recovery folder that cannot be created stops gracefully and retains the session temps. |
| BF-003 | medium | Opening wrote a complete copy and then parsed it a second time, keeping two parse trees. | fb9f271: the copy is registered with the parse made at open. A copy that cannot be written reports a disk problem instead of offering repair. |
| BF-004 | medium | A failed shortcut aborted setup after the payload was copied and before the uninstall entry existed. | 30b9eb3: the install and uninstall keys are written first. Whether an optional desktop link should abort setup remains a product call. |
| BF-006 | low | A declined or failed elevated machine uninstall exited 0. | 30b9eb3: the relaunch waits and returns the child's code, 1223 when declined, or 1. |
| BF-007 | medium | A damaged certificate catalog stopped every signature check. | e4bb234: inspection falls back to Windows trust. The dialog names the file to delete to start over. |
| BF-008 | low | One unreadable Windows store certificate emptied the catalog list. | e4bb234: such certificates are skipped. |
| BF-009 | low | A refused import cleared the displayed list, and load failures were described as update failures. | e4bb234: the list is kept, and load failures have their own message. |
| BF-010 | medium | Co-signing ignored prepared signature fields not named Signature1, so certified PDFs with an approval field could not be co-signed. | 5169e96: a prepared unsigned, writable signature field is chosen first. A field picker remains a product call. |
| BF-011 | medium | In High Contrast, hovered menu text used a theme accent on the system window color. | d05562c: mapped to the system window text color. |
| BF-012 | low | Retained recovery sessions were never cleaned up. | 84f1596: retention expires after thirty days. |
| BF-013 | low | The recovery fallback folder sat under the session temp root, so complete recoveries reopened as incomplete and clean. | 84f1596: fallbacks are the local data root and a system temp folder. |
| BF-014 | low | Unedited backups were left beside complete recovered copies. | 84f1596: deleted once the complete copy succeeds. |
| BF-015 | low | A recovered editor skipped the turned-box re-anchoring and page clamping a commit applies. | 84f1596: commit and recovery share one builder. |
| BF-016 | low | Every edit made the screen-reader model re-read and parse the file. | fe9a57b: facts are keyed to the working file's timestamp. |
| BF-017 | low | Members were made public only for reflection-based smoke tests. | 84f1596: restored to internal; the smokes bind non-public members. |
| BF-018 | low | Certificate import errors were English in every locale. | e4bb234: expected problems carry a kind, shown with localized text in 20 locales. |
| BF-020 | low | One PDF rejected while building pages aborted a folder or archive import. | c68859e: that source is skipped and the rest is rebuilt. |
| BF-021 | low | Visible-range queries walked the visual tree twice per character. | dcdba6e: one canvas-to-screen matrix per page. |

## Round B: valid findings and repairs

| Item | Severity | Finding | Repair |
| --- | --- | --- | --- |
| NF-001 | medium | Wrapped lines restarted bidi resolution, so numbers starting a wrapped Hebrew line could flip sides. A wrapped line with no Hebrew already matches WPF. | fef4c53: directions are resolved per hard paragraph and reordered per wrapped line. |
| NF-002 | medium | Fractions and circled numbers were classified as left-to-right letters, splitting Hebrew lines. | fef4c53: corrected classes, including Arabic-Indic digits. |
| NF-003 | medium | Mirrored brackets were saved as the mirrored character, so copy and search read the wrong bracket. | efb6123 and fef4c53: the mirrored glyph is drawn under a code that extracts as the typed character. |
| NF-004 | low | Only twelve characters were mirrored. | fef4c53: the bracket, quotation, comparison and set pairs from the Unicode mirroring data. |
| NF-005 | low | Letter spacing in right-to-left runs was counted per scalar but placed per text element. | fef4c53: counted per text element. |
| NF-006 | low | The overlay itself spaces right-to-left letters by logical column, so the overlay and the save differ when letter spacing is set. | Not changed. This predates 1.9's bidi work and lives in the overlay; recorded as follow-up work. |
| NF-007 | medium | A lone surrogate made save, print and export fail. | fef4c53: decoded as U+FFFD. |
| NF-008 | low | A command-line crash on a background thread exited with the runtime's crash code. | cd46b3a: every reported command-line crash exits 1. |

## Round C: valid findings and repairs

| Item | Severity | Finding | Repair |
| --- | --- | --- | --- |
| RF-001 | medium | With no writable recovery folder, the restart said the documents had reopened while their unsaved changes were lost. | 0f4addb: a manifest in the retained session folder names each document, so the restart shows the incomplete-recovery warning. |
| RF-002 | low | A full temp folder on the encrypted, password, certificate, read-only fallback and repair paths led to a repair offer or a wrong-password message. | 7e84b84: every open path reports the working-copy problem. |
| RF-003 | medium | Co-signing into a prepared field dropped the page and position the signer chose. Unmet seed values failed signing that used to add a new field. | 572a693, 2742053: a placed visible signature on an uncertified document gets a new field, and certified documents still use their prepared field. A seed or lock refusal falls back to a new field. |
| RF-004 | medium | A failed upgrade still advertised the new version, because the version was written before steps that can fail. | 80202cf: removability values first, version and size last. |
| RF-005 | medium | In High Contrast, highlighted menu items and the selected tool-search row looked like every other item. | adb8638: system Highlight and HighlightText; the row's shortcut text follows. |
| RF-007 | medium | The print warning was a muted label with no announcement and named pages outside the print range. | 5368675: announced as a live region, in the normal text color, naming only pages that will print. |
| RF-008 | low | Numeric blend names and comma lists such as /2 parsed as real modes. | 8258c28: only the ISO names are accepted. |
| RF-009 | low | Uninstall's single removal pass could leave files that a closing copy still held. | e69b2ba: the pass repeats for up to about thirty seconds. |
| RF-010 | low | Each folder-import rebuild re-read and re-parsed every file. | e1bdaa2: each PDF is parsed once. |
| RF-011 | low | `batch-render --allow-incomplete` gave no sign of incomplete pages. | 63d296b: console rows say INCOMPLETE and the summary counts them; CSV rows stay OK for gate baselines. |
| RF-012 | low | Public certificate inputs (openssl headers, TRUSTED CERTIFICATE, PKCS#7) were reported as private keys. | 366c039: accurate problem kinds. |
| RF-013 | low | Field choice walked the form once per field and ignored an inherited read-only flag. | 572a693: one pass with inherited flags. |
| RF-014 | low | The setup wizard missed a refused desktop shortcut when an older link already existed. | 5b53596: the wizard checks that the link opens the installed copy. |

Also from this round's runs: a load-sensitive cancellation test now has enough work to stay mid-render (8258c28).

## Round D: valid findings and repairs

Round D reviewed the round C repairs. The referee lowered SF-003 from high to medium, because the dispatcher shows a recoverable crash dialog and the temp folder must fail twice. It lowered SF-005 from medium to low. SF-005 and SF-007 predate the 1.9 range; the rest were introduced by round C repairs.

| Item | Severity | Finding | Repair |
| --- | --- | --- | --- |
| SF-001 | high | The new incomplete counter split the status chain, so every complete `batch-render` page also counted as failed and a clean run exited 1. | 808e13a: one tally counts OK, skipped, failed and incomplete pages, with behavioral tests. A window-free `batch-render` of two pages exited 1 before the fix and 0 after it. |
| SF-002 | medium | A page whose PNG could not be written counted as a kept incomplete page, so a run could exit 0 with a missing image. | 808e13a: a failed write counts as failed and the console says FAIL. |
| SF-003 | medium | Unlocking an owner-restricted PDF with a full temp folder failed again while putting the read-only original back, and that error escaped as a crash. | 7d7e321: the reopen reports the working-copy problem, and restoring the original handles the same failure. |
| SF-004 | low | A working-copy catch around removing the encryption could never run. | 7d7e321: removed; the source test now counts reachable sites. |
| SF-005 | low | With no recovery folder anywhere, the lost documents were named only by a manifest written beside the folder that had just refused one. | 4265a73: their names also go to the restarted process on its command line, and a manifest that cannot be written still points at the retained session folder. |
| SF-006 | low | An untitled lost document was listed by a placeholder file that was never written, and the restart asked to review recovered copies that did not exist. | 4265a73: lost documents are named by file, suggested name or as untitled. A restart that reopened nothing drops the review sentence, in 20 locales. |
| SF-007 | medium | Uninstall wrote its removal script as UTF-8, which cmd.exe reads in the OEM code page, so a profile folder with accented or non-Latin letters kept the whole payload. | 1215b93: the script switches its hidden console to UTF-8 first, tested with a non-ASCII folder. Changelog updated. |
| SF-008 | low | The removal retried for about thirty seconds whenever the folder kept a user file. | 1215b93: it stops once every owned file is gone. |
| SF-009 | low | The app's own silent and direct installs have no rollback, so writing the version last left a new copy named as the old version after a failed registration. | cf63046: those paths record the version first; the launcher, which rolls back, still writes it last. |
| SF-010 | low | A legacy encrypted OpenSSL key beside a certificate was skipped as outside text, so the file imported without the private-key warning. | c3cdd34: any private key block rejects the file. |
| SF-011 | low | The co-sign fallback retried failures that happened after signing, asking the signer to sign twice, and hid the first reason. | 9a33293, ed99651: the fallback runs only when nothing was signed, and the prepared field's reason is reported when the new field also fails. |
| SF-012 | low | A stale comment described a removed overload. | 91fd9cc: removed. |
| SF-013 | low | Typing a page range announced each intermediate print warning to screen readers. | 6857f93: the announcement waits until typing pauses. |

## Related work in the same range

- Render completeness: page-list thumbnails no longer go blank for pages that render with warnings (e149280). Twenty-two engine render warnings were reviewed as spec fallbacks or extraction-only, so they no longer block publication ([render diagnostics review](render-diagnostics-review-20261023.md); ad2f9ba, 0c4b63b, 9b919cd).
- The owner decided that printing and image export continue with a warning naming the affected pages. The command line stays strict unless `--allow-incomplete` is passed (ff87aa7, 016f2ed). Release-gate batch renders use the same option (d95d21c, 8065c4e).
- Command-line crashes report on the console instead of opening the crash window (cb4276a, reviewed in round B).
- A flaky timestamp test now has fixture windows that survive a full parallel run (5250c80).

## Owner decisions

Decided on 2026-10-23:

- Printing and image export continue with a warning that names incomplete pages. The command line refuses them unless `--allow-incomplete` is passed.
- BF-004: an optional desktop shortcut that Windows refuses finishes setup with a warning (40bd135, 5b53596). A Start menu shortcut failure still fails setup.

Still open:

- BF-005: whether to narrow the cross-session uninstall guard on multi-user hosts.
- BF-010: whether signing should offer a field picker and a `--field` command-line option. Until then, a visible signature goes where the signer places it and an invisible one fills a prepared field.
- NF-006: the overlay spaces right-to-left letters by logical column, so the overlay and the save differ when letter spacing is set on Hebrew.

## Verification

At `7e84b84`: the window-free app suite passed (1,245 tests). The engine suite passed (6,802 tests), and the app and launcher Release builds have zero errors. The launcher localization check passed for 20 languages. Load-sensitive timestamp and cancellation tests were fixed in this range (5250c80, 8258c28).

At `4265a73`, after round D: the window-free app suite passed (1,256 tests) and the engine suite passed (6,802 tests). The app, launcher and UI smoke Release builds have zero errors; the UI smoke project was compiled, not run. The launcher localization check passed for 20 languages. The uninstall removal tests run the real script through a windowless cmd.exe against a non-ASCII folder and a folder holding a user file. A window-free `batch-render` run confirmed SF-001 before the fix and its repair after it.

Not run, because they open windows: the UI smoke suites (including the render recovery, toolbar and certificate catalog smokes edited here), TabPlacementTests, DocumentTabLayoutTests, ComparisonBarLayoutTests and WindowFxTests. The following are verified by source-boundary tests only and still need a hands-on or VM check:

- the print preview warning and its screen-reader announcement
- High Contrast menu highlighting
- the certificate dialog
- the setup wizard's desktop shortcut warning
- installer registration order
- render recovery, including the restart with no recovery folder
- unlocking an owner-restricted PDF with a full temp folder
- uninstalling a real install from a profile folder with a non-ASCII name
