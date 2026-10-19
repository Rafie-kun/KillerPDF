# KillerPDF 1.9 readiness, October 19, 2026

All implementation work targets 1.9. Issue 437 is implemented by 086b8d3 and belongs in release verification, not a separate repair track unless the guarded 1.9 candidate fails that check. Issues remain open for reporter confirmation.

## Checks completed

- All 334 issues now have labels. All 51 open issues were already labeled; 61 closed historical issues received existing classification labels. No issue states or existing labels changed.
- Latest headless application build passed. All 900 application tests passed in the preceding verification. The earlier engine suite passed 6,652 tests; that result does not replace corpus accuracy checks.
- Current app and engine direct/transitive vulnerability queries report no vulnerable packages from the configured sources.
- Safe packaging-security fixtures pass in Windows PowerShell 5.1 and PowerShell 7. They check signing targets, negative signature gates, manifest hashes and release ordering without signing, publishing or executing an installer. The first 5.1 runs inherited an incompatible module path; a clean child-process PSModulePath corrected that environment failure.

## Remaining release work in execution order

| Order | Work | Evidence and acceptance |
| --- | --- | --- |
| 1 | Freeze one integrated 1.9 candidate | Preserve concurrent WIP, finish its intended integration, then capture source, payload and input manifests. Different retained/current DLL hashes exist; do not mix them in one result. |
| 2 | Finish accuracy gates A1 and A2 | Rerender the current candidate at 512 and 2048 against the accepted 1.9 baseline. Inputs remain 649 conformance and 976 PDF.js files. Last reconciled October 18 comparison changed 572/573 conformance and 770/769 PDF.js hashes; newer engine changes make those captures stale for the current candidate. Every difference needs a source-backed disposition. Run strict three-oracle comparison for every shared page, not only sampled differences. |
| 3 | Finish accuracy gates A3 and A4 | Complete the 76-page, 41-file difficult-set ledger at 2048. Verify 20 representative documents display the same bitmap as the headless renderer. Timing screenshots are not approved pixel baselines. |
| 4 | Close remaining safety and lab acceptance | Resolve Excel 4.0 macro handling. Verify real installer registration and rollback, custom-path updates, legacy-folder refusal/migration, separate Windows sessions and elevation, and normal startup concurrent with CLI. Guest access is unavailable. See the remediation record. |
| 5 | Run the 1.9 interactive acceptance pass | Verify 437 using maximized Win+D, taskbar and gesture restoration, both empty and populated windows, at representative native scales. Include signing, printing, split panes, theme/locale/scale coverage, and the normally closed sidebar with no PDF. |
| 6 | Remeasure performance and memory | Reestablish noise floor, warmups, alternating runs and exact payload identities. Historical S1/S3/S4 and M2/M3 failures are checkpoints, not measurements of the current candidate. Pause backups and heavy disk activity before measured runs. |
| 7 | Verify final packages and release metadata | Test real portable/installer payload inventories and clean-install/update/uninstall behavior. Verify final signed bytes when release is authorized. Update release-facing documentation and corpus pages from verified results. Tagging, pushing and publishing remain separate actions. |

## Runner preparation required before corpus or measured runs

The accepted frozen 1.9 baseline, PDFium reference and Poppler identities still match the retained records. The latest published release is v1.8.91; the gate document's historical reference wording still says v1.8.80. Reconcile that reference identity before fresh release performance measurements. This is reference verification only, not 1.8 development.

Several old runners cannot run unchanged. `Run-Baseline.ps1`, `Run-Triage.py` and the old `release-19-triage/triage.py` use obsolete non-scratch paths whose payload subdirectories no longer exist. Some also use page-one-only selection, 1024-pixel captures or an older scoring rule. Those are not the current A1/A2 contract.

Adapt `C:/Users/steve/scratch/kp-bench-render/form-workflows-20261018/Check-ReleasePixels.py` into a fresh owned run with a pinned candidate and new output root, preserving the accepted page selections. Extend the oracle wrapper to all shared pages using the current strict rule. `Reconcile-CurrentPixels.py` reconciles retained October 18 captures; it does not rerender a new candidate.

The next useful execution packet is therefore candidate freeze plus corrected, pinned accuracy-run configuration. Accuracy disposition comes before performance tuning or release claims.

## References

- [Release gates](../RELEASE-GATES-1.9.md)
- [Accuracy checkpoint](release-pixels-20261018.md)
- [Remediation and lab limits](adversarial-remediation-20261019-fresh.md)
- [437 verification context](issue-437-20261019.md)

## Label verification

Authenticated final enumeration returned 334 issues, 51 open and zero unlabeled. All 61 planned assignments are present, no original labels were removed, and no issue states changed. Primary classifications added were 39 enhancements, 17 bugs, four questions and one documentation issue. Area labels were added only where supported by the full issue context; the newer Engine label was not retroactively assigned to legacy renderer reports.

The exact assignments, reasons and final counts are retained in [the label audit](readiness-20261019-labels.json). No comments, issue closures, milestone changes, pushes or releases were performed. Temporary intake copies were used only for this audit; durable results are in this record and the linked JSON.
