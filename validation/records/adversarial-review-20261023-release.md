# KillerPDF 1.9 adversarial release review, October 23, 2026

This continues the chained 1.9 release review. [The October 21 review](adversarial-review-20261021-release.md) covered `4df9b86..e0b9c6e`. This record covers everything after it in seven rounds, plus the fixes they produced, and four more rounds (H to K) on the engine changes made for the A3 gate. Each round ran the three isolated stages from the harness contract (bug-finder, defender, referee). The defender received only the bug-finder's structured report, and the referee only the two reports, checking evidence against the pinned head with `git show`. All stages were read-only: no builds, test runs or app launches.

| Round | Range | Reviewed head | Findings | Valid | Invalid | Needs investigation |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| A: 1.9 delta | `e0b9c6e..f8cd0ba` (29 commits, 136 files) | `f8cd0bac61902f66d3f0bfe8db981420b39fe832` | 21 | 19 | 2 | 0 |
| B: #435 and CLI crash fixes | `f8cd0ba..cb4276a` | `cb4276a` | 8 | 8 | 0 | 0 |
| C: repairs from A and B | `cb4276a..5250c80` plus `40bd135` | `5250c80`, `40bd135` | 14 | 13 | 1 | 0 |
| D: repairs from C | `40bd135..7e84b84` | `7e84b84` | 13 | 13 | 0 | 0 |
| E: repairs from D | `104f047..29cd072` | `29cd072` | 7 | 7 | 0 | 0 |
| F: repairs from E | `29cd072..e5e4e10` | `e5e4e10` | 5 | 5 | 0 | 0 |
| G: repairs from F | `e5e4e10..a44cbe1` | `a44cbe1` | 7 | 6 | 1 | 0 |
| H: A3 rendering fixes (engine only) | `c6e13fd..4d82d0c` | `4d82d0c` | 5 | 5 | 0 | 0 |
| I: repairs from H | `4d82d0c..0ba31e0` | `0ba31e0` | 3 | 3 | 0 | 0 |
| J: repairs from I, and cc4c502 | `0ba31e0..7e04a4b` | `7e04a4b` | 4 | 4 | 0 | 0 |
| K: repairs from J | `7e04a4b..28216a5` | `28216a5` | 4 | not adjudicated | | |

Two invalid items came from round A. BF-005 is an accurate description of a deliberate, documented trade-off. BF-019 claimed state was never read, but a UI smoke test reads it. In round C, RF-006 was invalid: the comb-overflow message never decides a page's outcome, because every path that emits it also emits a blocking message. Rounds D, E and F had no invalid items. In round G, GF-003 was invalid: a failed lease rename leaves the session no worse off than before round F, because the single-instance lock covers the gap and every later retention call and the restart retry the rename. Referee health scores: 5 for round A, 6 for round B, 6 for round C, 5 for round D, 7 for round E, 6 for round F, 8 for round G.

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

## Round E: valid findings and repairs

Round E reviewed the round D repairs. Four findings predate the range and were left unfixed by it (TF-001, TF-002, TF-004, TF-005); three were introduced by round D (TF-003, TF-006, TF-007). The referee rated TF-001 medium, not high: the loss needs every recovery root to refuse a folder and the session folder to refuse a small write.

| Item | Severity | Finding | Repair |
| --- | --- | --- | --- |
| TF-001 | medium | On a disk full enough to refuse the recovery copies, the session's retention marker failed too, so the restart's stale-temp cleanup deleted the working files its message pointed at. | 5c0ac8d: the restart is told which session it replaces, keeps that folder and marks it for later launches, with a behavioral test. |
| TF-002 | medium | A failed owner-password unlock put the read-only original back without the form entries and annotations made on it, and marked the tab clean. | 41fd7c2: that work carries over to the restored original, as it does to an unlocked copy. |
| TF-003 | low | Two lost untitled documents were merged into one line, so the restart reported one loss for two. | 5c0ac8d: each document keeps its own line. e5e4e10 adds a headless test. |
| TF-004 | medium | When a recovery folder existed but its manifest could not be written, the restart named a placeholder under the user data folder and not the folder holding the copies. | 5c0ac8d: the restart lists the copies and points at their folder; the originals stay in the restored tabs. |
| TF-005 | low | A fresh setup install that failed a required step stayed registered with no version, because setup restores a previous copy only on an upgrade. | ecdb589: setup passes `/no-rollback` when there is nothing to restore, so the version is recorded first. |
| TF-006 | low | The app's own installs measured their size before the icon and command shim were written. | ecdb589: version and size are written again once every file is in place. |
| TF-007 | low | The new recovery and unlock tests only scanned source, so they passed with TF-003 present. | e5e4e10: lost-document naming moved into the recovery service with a headless round-trip test; TF-001 has a behavioral test. The unlock path is window code and keeps a source test. |

## Round F: valid findings and repairs

Round F reviewed the round E repairs. XF-001, XF-002 and XF-003 predate the range: TF-001's repair narrowed XF-001 without closing it, and the two setup defects date from 1.8. XF-004 and XF-005 were introduced by round E.

| Item | Severity | Finding | Repair |
| --- | --- | --- | --- |
| XF-001 | high | The restart's retention marker needs new disk space, so on the full disk TF-001 targets it failed as well, and the next ordinary launch deleted the working files the recovery message pointed at. | 72ed630: when a marker cannot be written, the session's lease is renamed into the marker, which needs no new space; an expired one is cleaned up without a lease. Behavioral tests make every marker write fail. |
| XF-002 | medium | After a successful upgrade registration, a previous copy that could not be deleted threw into the rollback, which deleted the registered new copy and restored a partly removed old one. In 1.8.91 too. | 5c44049: nothing rolls back once registration commits, and the old copy's removal is best effort. Changelog updated. |
| XF-003 | medium | An empty destination folder was treated as a previous copy, so a failed first install there deleted the payload its uninstall entry named. In 1.8.91 too. | 5c44049: an empty folder is replaced, not backed up, so registration gets `/no-rollback` and the payload stays. Changelog updated. |
| XF-004 | low | A blank or malformed retained session on the restart's command line threw out of startup cleanup. | 72ed630: ignored, with tests for blank and invalid paths. |
| XF-005 | low | The untitled-documents test built its lines with a helper the restart message did not use. | a44cbe1: the restart builds and joins its lines with the tested helpers. |

## Round G: valid findings and repairs

Round G reviewed the round F repairs. Every valid finding is low. GF-001, GF-002, GF-004 and GF-007 came from round F's setup changes; GF-005 and GF-006 predate the range.

| Item | Severity | Finding | Repair |
| --- | --- | --- | --- |
| GF-001 | low | A previous copy that could not be deleted after an upgrade was left beside the install with nothing to remove it later. | e815749: the next install removes staging and previous-copy folders found by their exact generated names, with a test that look-alike folders survive. Uninstall does not sweep them. |
| GF-002 | low | On a machine install, a per-user removal that could not start reported the completed install as failed. | e815749: that step is best effort, like its exit code. |
| GF-004 | low | An empty destination folder was deleted once, where a rename used to wait out a brief lock. | e815749: the delete is retried the same way and is never recursive. |
| GF-005 | low | The cross-process temp-cleanup probe no longer built. | 2a64439: it compiles the atomic writer too and passes all seven checks. |
| GF-006 | low | A copy with several backup errors listed the later ones as separate lines, and a copy that also failed to reopen was listed twice. | a296267: each document is one line, with its errors joined. |
| GF-007 | low | The setup commit-point test could pass with the commit moved inside the failure branch. | e815749: the test pins the commit as its own statement after the failure throw. Setup's swap and rollback still have no behavioral test; that needs the VM. |

## Round H: valid findings and repairs

Round H reviewed the engine changes made for the A3 difficult-set gate ([A3 record](A3-difficult-2026-10-23.md)): `c6e13fd..4d82d0c`, limited to `engine/`, which excludes another session's tab styling commit 40e88a5. The defender confirmed all five findings; the referee ruled all five valid, lowered HF-003 to low, and corrected HF-003's trigger. Health score 6. Each repair has a test that fails without it.

| Item | Severity | Finding | Repair |
| --- | --- | --- | --- |
| HF-001 | medium | Smoothing sent enlarged 8-bit gray, RGB and CMYK images on CMYK pages to the converted plane, which holds at most four million cells. A larger image, such as a full-page scan printed at 600 dpi, was averaged to half resolution and drawn unsmoothed, blockier than before the range. | 8e543d4: gray and RGB images keep their full-resolution copies unless the plane can hold every sample. Narrowed in round I (IF-001). |
| HF-002 | medium | The one-pixel minimum for thin filled rules applied only to ordinary fills. Opaque DeviceCMYK fills on CMYK pages and pattern fills rasterized the rule at exact coverage, so CMYK rules on PDF/X pages stayed faint beside solid gray ones. | 1e39a5a: every fill route uses the rule. |
| HF-003 | low | For indexed spot palettes on CMYK pages (over a process-and-spot DeviceN) or on RGB spot-shadow pages, the plane of spot samples still divided the source evenly while 4d82d0c aligned the color plane, so the two disagreed by up to half a pixel. | 62f47ef: both planes use the same spans. The difference only reached the page once cc4c502 (below) let these images create their spot plates; with that in place, the test fails without the repair on both the Cyan and the spot plate. |
| HF-004 | low | A smoothed Separation image took its spot tint from the nearest sample while its ink blended four, so a blank nearest sample cleared the spot plate and cut the ramp on that side of each edge. | 0ba31e0: the tint blends from the same samples and weights. Indexed spot palettes are not smoothed. |
| HF-005 | low | An image edge exactly on a pixel center: the plane counted that pixel as a cell, the paint loops skip it, and the edge content folded into it vanished. | cc403cc: centers on the edge are left out of the count, so the content lands in the last painted pixel. |

Found while testing HF-003, and fixed in cc4c502: on a CMYK page with no spot plates yet, images took a direct ink write that skips spot bookkeeping. Only opaque single-spot images were kept out of it, so an image whose indexed palette uses a spot, over a Separation or a process-and-spot DeviceN, or a reduced spot image, painted its process ink but never created its spot plate. The separation preview showed nothing for it, and later overprints could not see that spot. The composite was right. The test fails without the fix for both palette kinds, reduced and unreduced. This is also why HF-003 first showed no effect: its spot samples were dropped before they reached a plate.

## Round I: valid findings and repairs

Round I reviewed the round H repairs. The defender confirmed all three; the referee ruled all three valid. Health score 7. Each repair has a test that fails without it.

| Item | Severity | Finding | Repair |
| --- | --- | --- | --- |
| IF-001 | medium | 8e543d4 opened all four direct image copies to enlarged images too large for the plane, but only the gray and RGB copies read every sample. The sampled copies read the plane's reduced grid, so a large image with a soft mask lost its smoothing and was drawn from every second sample. | b11951b: the sampled copies are closed to smoothed images again. |
| IF-002 | low | A 1-bit indexed spot palette builds its color plane through the binary sampler, which has no area spans, so 62f47ef left its spot samples on the even split. | d7e82d2: the spot samples take spans from the aligned ranges whichever sampler builds the color plane. |
| IF-003 | low | cc403cc left on-edge centers out of the cell count, so an image exactly one pixel across with both edges on centers had no cell: both axes fell back to the even split, the painted pixel showed half the width, and the other axis lost its first rows. | 7e04a4b: such an axis keeps one cell holding the whole source. |

The referee also noted, as before the range: an 8-bit CMYK image too large for the plane, without a soft mask, on a CMYK page is drawn from the half-resolution averaged plane. 8e543d4's message claimed full resolution for CMYK images too; that holds for gray and RGB only. Open, not a regression.

## Round J: valid findings and repairs

Round J reviewed the round I repairs and cc4c502, which no earlier round covered. The defender disputed all four as predating the range; the referee ruled all four valid and recorded the scope of each. Health score 8 for the range itself.

| Item | Severity | Scope | Finding | Repair |
| --- | --- | --- | --- | --- |
| JF-001 | medium | Predates the range | A non-indexed DeviceN image with a named spot (process plus spot, or two spots), or a reduced Separation image that is not opaque, never reaches its spot plate: the image pipeline carries no spot data for them. cc4c502's message overstated its reach ("any image that carries a spot"); it covers spot palettes and single-spot images. | Open. It needs per-sample spot data through the image plane, the same work as two-spot palettes (A3 open item). |
| JF-002 | medium | Predates the range | On CMYK pages an image mask's fill color lost its spot when converted to ink, so spot-colored stencils and Type 3 bitmap glyphs put the alternate on the process plates, left their plate blank and cleared any spot under them. | 28216a5, reverted in 8a93ac5 after round K (below). Open, with JF-001. |
| JF-003 | low | Made reachable by cc4c502 | A reduced palette over one spot took the full path but never got the prepared, lock-free plate write. | bf6ff79: the spot name comes from the palette. Output unchanged; no speed-up was measurable on a 512 by 512 render. |
| JF-004 | low | Made reachable by b11951b | Smoothing from a plane reduced to fit placed each cell at an even share of the image and weighted it by a neighbor's mask value: hidden colors bled into soft-mask edges as a fringe. | 8592911: each cell sits at the source sample it holds and weighs by that sample's mask value. |

## Round K: the JF-002 repair, reverted

Round K's bug-finder reviewed the round J repairs. It found JF-003 (bf6ff79) and JF-004 (8592911) correct and complete. All four of its findings concern JF-002's repair (28216a5):

- KF-001 (medium): partly covered spot-stencil pixels took a new branch that skipped the transparency group's alpha and shape bookkeeping, so inside a non-isolated or knockout group they were dropped. Introduced by the repair.
- KF-002 (medium): a spot stencil painted with an opacity below 1 now kept its spot, which moved it off the path that preserves existing spot plates, so it cleared them. Introduced by the repair.
- KF-003 (low): every fully covered spot-stencil pixel locked the spot plates, where it had been lock-free.
- KF-004 (low): on RGB pages with a spot shadow, a spot stencil's fully covered pixels are written straight to RGB and skip the shadow. That part predates the repair.

Each repair of the spot-plate machinery in rounds H to K surfaced new interactions with groups, opacity, the RGB shadow and threading. 28216a5 was therefore reverted (8a93ac5) instead of extended, and its findings were not sent to the defender and referee, since the code they concern is gone. Spot-colored image masks return to their behavior before round J. JF-001, JF-002 and KF-004's earlier part stay open as one piece of work: carrying spot data through image painting (DeviceN images, two-spot palettes, spot stencils, the RGB shadow), with its own tests and review.

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

At `e5e4e10`, after round E: the window-free app suite passed (1,259 tests). The engine is unchanged since `4265a73`, where its suite passed. The app, launcher and UI smoke Release builds have zero errors, and the launcher localization check passed for 20 languages.

At `a44cbe1`, after round F: the window-free app suite passed (1,264 tests), the engine is still unchanged since `4265a73`, the app, launcher and UI smoke Release builds have zero errors, and the launcher localization check passed for 20 languages.

At `a296267`, after round G: the window-free app suite passed (1,266 tests), the cross-process temp-cleanup probe passed all seven checks, the engine is still unchanged since `4265a73`, the app, launcher and UI smoke Release builds have zero errors, and the launcher localization check passed for 20 languages.

The chained review stopped at round G for the app and installer work. Findings fell from 13 in round D to 6 low ones in round G, and the round G repairs are small and covered by tests or the probe.

The engine chain (rounds H to K) stops at round K with its last repair reverted. Round H found five valid findings, round I three, and round J four, two of them older than the range. Round K found the JF-003 and JF-004 repairs sound. At 8a93ac5 the engine is the round J state without JF-002's repair; the engine suite passed on that code (6,871 tests, with the known load-sensitive timestamp test passing alone), and the window-free app suite passed at 8a93ac5 (1,289 tests).

Not run, because they open windows: the UI smoke suites (including the render recovery, toolbar and certificate catalog smokes edited here), TabPlacementTests, DocumentTabLayoutTests, ComparisonBarLayoutTests and WindowFxTests. The following are verified by source-boundary tests only and still need a hands-on or VM check:

- the print preview warning and its screen-reader announcement
- High Contrast menu highlighting
- the certificate dialog
- the setup wizard's desktop shortcut warning
- installer registration order
- render recovery, including the restart with no recovery folder
- unlocking an owner-restricted PDF with a full temp folder, including keeping read-only form entries
- a fresh setup install whose registration fails, which should list the new version in Apps and features
- a first install into an empty folder made in the setup folder picker, and an upgrade while a file of the old copy is held open
- uninstalling a real install from a profile folder with a non-ASCII name
