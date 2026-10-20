# Adversarial review remediation, October 20, 2026

Scope: the four confirmed findings in
[the verification report](verification-adversarial-20261020.md), plus synthetic
investigation of Office timeout ownership. Branch: `dev/1.9-overkill`.
No push or release was performed. Concurrent sidebar work in `4e2b4f9` and
untracked review artifacts were preserved.

## Separate fixes and verification

| Finding | Local commit | Regression before fix | Verification after fix |
| --- | --- | --- | --- |
| BF-005: uninstall command expansion changes literal paths | `f909bed` | Four path cases failed | All 10 ownership tests passed |
| BF-003: malformed optional XMP prevents reading document information | `6e8f81f` | Both filter cases threw | 26 focused information, upgrade, and form-data tests passed |
| BF-004: PDF 2.0 migration loses trapping status | `c952e7e` | All six trapping cases failed | 23 information and upgrade tests passed |
| BF-002: page-group optimization misses nested drawing programs | `c5eed4b` | All four fixtures rendered cyan instead of red | Full engine suite passed: 6,685 tests, zero failures or skips |

BF-005 escapes percent signs for batch parsing and disables delayed expansion.
The regression invokes actual `cmd.exe` with delayed expansion initially enabled,
but replaces removal commands with echo commands. It verifies undefined and
defined environment-variable syntax, exclamation marks, command punctuation,
and ordinary percent signs without executing deletion commands.

BF-003 catches expected filter failures only at the optional XMP boundary.
The fixtures retain readable Info metadata and pages while their XMP uses either
broken Flate data or an unsupported filter.

BF-004 migrates and reads True, False, and Unknown trapping states through XMP.
Tests cover both new and existing XMP, serialized metadata, removal from Info,
and retention of unrelated custom metadata. The metadata commits include the
previously uncommitted XMP reader and PDF 2.0 upgrade implementation they repair.

BF-002 conservatively retains an isolated page group when content selects a
named pattern or Type3 font, including fonts selected through an extended
graphics state. Tests cover direct patterns, patterns inside forms, and both
Type3 selection paths. Each checks whole-page pixels, region pixels, and
transparent rendering. Ordinary content retains the existing fast path.
This conservative choice can incur page-group allocation for patterns or
Type3 fonts that do not actually blend.

## Office timeout investigation

The user selected synthetic investigation. No real Office instance or guest
desktop was automated. A fake application object was injected through the
existing conversion factory seam. Its window handle belonged to a disposable
helper process created by the probe. A blocked Open operation forced the real
timeout and PID termination path to run.

| Injected state | Word route | Excel route |
| --- | --- | --- |
| Hidden, one existing document | Helper terminated | Helper terminated |
| Visible, one existing document | Helper survived | Helper survived |
| Hidden, no documents | Helper terminated | Helper terminated |

Confirmed cause: `Export` calls `NoteOwnedApplication` while the application is
hidden, before checking Documents.Count or Workbooks.Count. A later decision
that the instance is not owned changes the local `ours` flag but leaves the
session PID registered. Timeout cleanup therefore still terminates it.

This confirms the ownership bookkeeping defect for an injected instance.
It does not establish that production `Activator.CreateInstance` returns an
existing hidden Word or Excel instance with user documents. That reachability
question remains open. Office product code was not modified during this
investigation. A bounded correction would defer PID registration until all
ownership checks have passed; actual COM activation and document survival still
need disposable Office lab verification.

## Office ownership follow-up fix

Following authorization to fix BF-001, ownership now starts false and is
established only after confirming the application is hidden and its document
collection is empty. PID registration and changes to visibility and alerts
follow that decision. A failure while reading ownership properties leaves
ownership unconfirmed.

The committed `tests/Test-OfficeTimeoutOwnership.ps1` exercises the real
conversion timeout with synthetic Office objects and test-owned child windows.
Before the fix, both hidden-existing-document cases failed because their helpers
were terminated. After the fix, all eight cases passed: hidden and visible
instances, each with zero or one existing document, on both Word and Excel
routes. Only hidden, empty instances were terminated. No actual Office process
was launched or terminated.

Reproduction from the repository root after building KillerPDF.Tests:

```powershell
pwsh -NoProfile -File tests/Test-OfficeTimeoutOwnership.ps1 -TestAssembly KillerPDF.Tests/bin/Release/net10.0-windows/KillerPDF.Tests.dll -EvidenceRoot C:/Users/steve/scratch/kp-fixes-20261020
```

The full application suite passed again (964 tests), and the Release application
build passed with zero warnings and errors. Real Office activation remains a
lab verification boundary. Sidebar work and preexisting review files were
preserved, and nothing was pushed.

## Acceptance limits

Final verification also passed all 964 application tests, the CLI output-safety
script, and a Release application build with zero warnings and zero errors.
The application test build emitted existing third-party CoreJ2K warnings.
Harness cleanup passed with no outstanding items.

Automatic approval review rejected deletion of
`C:\Users\steve\scratch\kp-fixes-20261020` as "blocked by policy."
The test logs and synthetic Office probe scripts remain there.

The rendering checks are synthetic pixel assertions, not corpus visual approval.
The uninstall tests verify command argument behavior, not an installed-product
uninstall. The reported v1.8.91 merge hang still lacks input files and a concrete
reproduction. Existing release, installer, parity, interactive UI, and Office lab
gates in [the release checklist](../RELEASE-GATES-1.9.md) remain open.
