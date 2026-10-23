# Certificate catalog validation, October 22, 2026

## Scope and issue 425

The original signed-field overlay and missing signature information reported in [#425](https://github.com/SteveTheKiller/KillerPDF/issues/425) have source fixes in 1.9. Commit `9d9ed14` suppresses the unsigned Sign overlay for signed fields and adds inspection; `1fba01a` adds the sidebar. Those changes were not in the reporter's 1.8.91 build. The new certificate-management request was a remaining feature, now implemented locally with the approved Windows-read-only and app-local design.

The catalog is reachable from Document Tools with or without a PDF open, from the Signatures panel context menu, and through the shared document-tool shortcut table (default Ctrl+Shift+D, Z). Search Tools derives the same menu actions. It shows subject, issuer, validity, SHA-256 fingerprint, source, type and trust state.

Windows current-user/computer ROOT and CA stores are read-only. Public DER and PEM certificates can be imported into KillerPDF, inspected, explicitly trusted as roots, untrusted or removed. Import never grants trust. Only CA certificates can become local anchors. PFX and private-key content are rejected, including renamed files and mixed bundles. Intermediate candidates do not automatically become roots.

Desktop and CLI signature reports use the same catalog policy. Windows trust is checked first; local roots only provide a fallback for otherwise untrusted signatures that retain valid structure, cryptographic integrity and certificate time. Trust changes do not clear tampering, expiration or revocation failures. Certificate downloads remain disabled and revocation remains unchecked by default, so a trusted chain does not automatically mean a complete validation verdict.

Storage uses bounded public-certificate JSON, atomic replacement and a cross-process write lock with reload-before-mutation. Damaged or locked existing storage is preserved. The dialog prevents closure while a mutation is pending. Closing it refreshes signature results even when this dialog did not write, covering external trust changes.

## Automated evidence

- Application build: succeeded, zero warnings and errors with isolated artifacts.
- UI smoke build: succeeded, zero warnings and errors.
- 20 catalog cases passed, including DER/PEM and intermediate chains, explicit trust/untrust/remove, duplicate preservation, non-CA rejection, read-only Windows inventory, damaged storage, size/count limits, private-key rejection, locked-file preservation, concurrent writers, expired certificates and changed signed bytes. Four initial feature slices were observed failing before implementation.
- 62 focused tests passed: catalog, shortcut table and localization parity. The first isolated invocation had 11 source-location failures because existing tests walk upward from their output folder to locate repository sources. Running the same binaries beside a fresh source snapshot inside the task scratch directory passed all 62. Product tests were not weakened.
- 45 existing engine signature-reader/verifier/embedded-certificate/appearance tests passed during the initial #425 investigation.
- 23 rendered UI cases passed: Dark, Light and 98SE with populated data; all 20 locales at the dialog's minimum dimensions. Action bounds, search, empty results, untrusted import, Windows read-only controls, trust/untrust/remove and cancellation were exercised through WPF controls.
- Independent review found stale displayed trust after an external change: closing refreshed only after a local mutation. The compiled pre-fix app failed the added close-refresh check; the corrected app passed. No additional actionable catalog findings were reported.
- The smoke's first close-refresh attempt selected a prior fading test dialog and stalled. Only that identified test-owned process was stopped. The smoke now identifies the newly opened manager and completed. This was a harness-selection problem, not a product failure.
- All 20 locale deltas contain exactly 32 new catalog resource keys; key/placeholder parity passed. Prior locale edits are byte-preserved when the catalog additions are removed.
- Added content contains zero U+2013 or U+2014 characters.

Final UI-tested app SHA-256:
`11F418A845B0681DC2C6CEBF942F6657E91461177168A69713BB4FA67D76E021`

Repeatable UI check:
`--verify-certificates APP_DLL APP_XAML OUTPUT_DIRECTORY --startup-smoke`.
Use a fresh scratch directory and the matching built application resources. Catalog test data and settings are isolated. Windows trust stores were never modified.

## Review and evidence limits

The rendered captures were inspected in Dark, Light, 98SE and German at minimum size. These are WPF control checks, not physical mouse/keyboard, native display-scaling or screen-reader certification. The file picker import path was source-reviewed; backend import and actual dialog trust/removal controls were exercised. A positive PDF fixture already trusted by Windows was not available, so system-trust preservation is supported by the system-first implementation and review, not a new signed Windows-trusted fixture.

No independent bundled CA repository, automatic internet trust updates, online revocation checking, or comprehensive historical timestamp-authority trust validation is claimed. The original reporter's appearance example still needs acceptance against their document. Existing release, native integration and hardware gates remain open.

Concurrent unrelated changes and staged theme work were preserved. Only catalog changes and this evidence are intended for the local feature commit. The separate [open-issue audit](open-issues-triage-20261022.md) covers all 53 open reports and verified label changes. No push or release was performed.
