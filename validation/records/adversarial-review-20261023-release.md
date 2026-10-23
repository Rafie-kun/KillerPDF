# KillerPDF 1.9 adversarial release review, October 23, 2026

This continues the chained 1.9 release review. [The October 21 review](adversarial-review-20261021-release.md) covered `4df9b86..e0b9c6e`. This record covers everything after it in two rounds, plus the fixes they produced. Each round ran the three isolated stages from the harness contract (bug-finder, defender, referee). The defender received only the bug-finder's structured report, and the referee only the two reports, checking evidence against the pinned head with `git show`. All stages were read-only: no builds, test runs or app launches.

| Round | Range | Reviewed head | Findings | Valid | Invalid | Needs investigation |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| A: 1.9 delta | `e0b9c6e..f8cd0ba` (29 commits, 136 files) | `f8cd0bac61902f66d3f0bfe8db981420b39fe832` | 21 | 19 | 2 | 0 |
| B: #435 and CLI crash fixes | `f8cd0ba..cb4276a` | `cb4276a` | 8 | 8 | 0 | 0 |

Both invalid items came from round A. BF-005 is an accurate description of a deliberate, documented trade-off. BF-019 claimed state was never read, but a UI smoke test reads it. Referee health scores: 5 for round A, 6 for round B.

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

## Related work in the same range

- Render completeness: page-list thumbnails no longer go blank for pages that render with warnings (e149280). Twenty-two engine render warnings were reviewed as spec fallbacks or extraction-only, so they no longer block publication ([render diagnostics review](render-diagnostics-review-20261023.md); ad2f9ba, 0c4b63b, 9b919cd).
- The owner decided that printing and image export continue with a warning naming the affected pages. The command line stays strict unless `--allow-incomplete` is passed (ff87aa7, 016f2ed). Release-gate batch renders use the same option (d95d21c, 8065c4e).
- Command-line crashes report on the console instead of opening the crash window (cb4276a, reviewed in round B).
- A flaky timestamp test now has fixture windows that survive a full parallel run (5250c80).

## Owner decisions still open

- BF-004: whether a desktop shortcut that cannot be written, for example under Controlled Folder Access, should abort setup or only warn.
- BF-005: whether to narrow the cross-session uninstall guard on multi-user hosts.
- BF-010: whether signing should offer a field picker and a `--field` command-line option.

## Verification

At `5250c80`: the window-free app suite passed (1,238 tests). The engine suite passed (6,798 tests, with the timestamp test fixed for parallel load). The Release build has zero errors.

Not run, because they open windows: the UI smoke suites (including the render recovery, toolbar and certificate catalog smokes edited here), TabPlacementTests, DocumentTabLayoutTests, ComparisonBarLayoutTests and WindowFxTests. The print preview warning, the High Contrast hover color, the certificate dialog and the installer paths are verified by source-boundary tests only. They still need a hands-on or VM check.

A third round reviewing these repairs (`cb4276a..5250c80`) is recorded below when it completes.
