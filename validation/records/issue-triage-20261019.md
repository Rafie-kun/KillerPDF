# KillerPDF 1.9 issue work, October 19, 2026

## Intake and authorization

Steve requested work on open KillerPDF issues alongside headless rendering accuracy, with a comment after a verified 1.9 commit explaining that the cause is understood and a fix is in for 1.9. This authorizes bounded local investigation, repairs, tests, local commits and matching issue comments. Issues remain open for reporter confirmation. No push, release, branch change or UI work is authorized by this continuation.

The GitHub scope is `SteveTheKiller/KillerPDF` and the full issue conversations and linked context required by selected defects, including accessible private context if present. Authenticated live repository metadata, open-issue enumeration and issue 455 with comments succeeded. GitHub reports this repository is public. This is bounded issue execution, not a completed repository-wide triage scan.

## Selected defects

Issue [455](https://github.com/SteveTheKiller/KillerPDF/issues/455) reports three merge-import validation failures with exact object structures. The original business PDFs are unavailable. A standard implementation worker owns public-merge synthetic reproductions, primary-spec verification and bounded fixes in import validation, with malformed-input controls. The worker cannot publish, commit or modify shared release text. Root owns final reconciliation and the authorized comment after a verified commit.

The rendering accuracy payload A04 remains frozen. New issue changes require their own integration evidence and do not inherit A04's corpus results automatically.

Issue [454](https://github.com/SteveTheKiller/KillerPDF/issues/454) supplies exact document-level duplex ticket XML and explains why the job-level setting is dropped. Its full issue and comments were read successfully through authenticated GitHub access. A separate standard worker owns headless ticket/capability reproduction and any bounded repair, preserving job-level drivers and unrelated ticket features. Actual Canon hardware confirmation remains unavailable. No print job or UI interaction is part of this task.

## Verified existing fixes and posted updates

Both selected defects already have local 1.9 commits that are ancestors of the current HEAD. No redundant product edits or commits were made.

For 454, commit `92083f349e93c5a656737749a26463cc38d18bca` handles document-level ticket settings and capability detection. A replay using real WPF PrintTicket construction reproduced four unchecked-duplex failures in the historical parent. Current code passes all fifteen ticket/capability controls, and the existing nineteen regression tests pass. Source inspection confirms the actual print path uses the returned ticket. This proves emitted XML behavior, not Canon driver acceptance or physical sheet counts. Evidence is `C:/Users/steve/scratch/kp-bench-render/accuracy-remediation-20261019/issue454/FINDINGS.md`.

For 455, commit `0dd284dcc2d19394de9357ab56441dfec23c14c2` handles optional Type 3 descriptor metrics, CIDFont language names and empty stitching intervals. All eight public-import cases and two malformed-font controls pass. Merged output reopens with two pages and preserved structures; equal-bound shading produces the expected red/blue pixels without diagnostics. CLI and public-import tests reach the same validation path. The original private documents remain unavailable, and no new historical red is claimed for this already committed fix. Logs and copied build artifacts are retained under `C:/Users/steve/scratch/kp-bench-render/accuracy-remediation-20261019/issue455/verification`.

After reviewing the commits, diffs, tests and current conversations, root posted one concise update on each issue: [454 comment](https://github.com/SteveTheKiller/KillerPDF/issues/454#issuecomment-6280163337) and [455 comment](https://github.com/SteveTheKiller/KillerPDF/issues/455#issuecomment-6280163584). Live readback confirms both comments belong to SteveTheKiller and both issues remain OPEN. Each reply says the fix is in for 1.9 and retains the unavailable original-file or hardware confirmation. No issue closure, push or release occurred.
