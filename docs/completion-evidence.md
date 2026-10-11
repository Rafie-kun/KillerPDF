# PDF local completion evidence

Commits remain available while a maintenance change and its development counterpart are made.
Completion, pushes to the active PDF lines, and release preflight require both lines to be checked.
The shared identity and theme guards remain in place.

The gate checks exact local commit IDs, the committed development forward-port policy, reviewed
source assertions, and passing application and engine TRX results for both lines. Unknown or
missing review entries block completion. It does not establish visual, hardware, corpus, or
reporter-machine correctness. Include those checks in the review when the change requires them.
A hash proves which source was inspected; a named regression and a substring assertion do not
replace a careful review of equivalent behavior.

After finishing and committing both changes, prepare a source review outside the repository:

```json
{
  "schemaVersion": 1,
  "tips": {
    "maintenance": "FULL_MAIN_COMMIT_ID",
    "development": "FULL_DEVELOPMENT_COMMIT_ID"
  },
  "entries": [
    {
      "maintenanceCommit": "FULL_MAINTENANCE_COMMIT_ID",
      "status": "adapted",
      "reason": "Describe the verified counterpart and any different implementation.",
      "assertions": {
        "maintenance": [
          {"path": "Services/Example.cs", "sha256": "SHA256_OF_COMMITTED_BYTES", "contains": ["verified behavior"]}
        ],
        "development": [
          {"path": "Services/Example.cs", "sha256": "SHA256_OF_COMMITTED_BYTES", "contains": ["verified behavior"]}
        ]
      },
      "tests": {
        "maintenance": ["Exact TRX testName for the applicable regression"],
        "development": ["Exact TRX testName for the applicable regression"]
      }
    }
  ]
}
```

Every maintenance commit after the existing reviewed baseline needs one entry, including metadata
and merges. Status is `exact`, `adapted`, or `inapplicable`. Exact entries require a matching Git
patch or shared ancestor. Adapted and inapplicable entries require a concrete reviewed rationale.
Both lines require source assertions and named tests. An absent dependency can be asserted with
`{"path": "retired/file", "absent": true}`; verify its replacement behavior too. Use hashes of
committed Git blobs, not newline-normalized working files. Do not turn a gap into an inapplicable
entry or move the baseline to hide it.

Generate test evidence explicitly:

```powershell
python build/Check-PdfCompletion.py --repo . --run-tests --review C:/path/to/review.json
```

This runs the existing forward-port checker and builds/tests exact Git archive snapshots for
both application and engine suites on both lines. It writes retained temporary snapshots/TRX
files and one atomic receipt in the shared Git directory, `killerpdf-completion.json`.
A failed check produces no passing receipt. Restore/build may use the ordinary NuGet cache.
It does not sign, push, publish, change branches, or copy implementations between branches.

The read-only completion command is:

```powershell
./build/Test-PdfCompletion.ps1
```

The release script invokes that same check before any release side effects.
`./release.ps1 -PreflightOnly` returns after this local completion gate, without metadata changes,
network calls, builds, signing, or publication. It is a completion preflight, not certification of
release metadata or signed artifacts. `-DryRun` retains its existing build/sign behavior.

The ordinary release flow can create metadata and landing-page commits after preflight.
Those commits invalidate an earlier receipt. Its later push stays blocked until the new pair
is reviewed and tested. This gate does not automatically forward-port release changes.

The existing shared pre-push guard checks outgoing identities first, then validates PDF main,
dev/1.9-overkill, and release tags against a receipt. It substitutes the actual pushed OIDs for
the applicable local tips and checks the current local peer branch. It does not require the
peer to be published, so it supports paired local development. Other feature branch pushes
retain identity checks; this completion gate targets the two active lines and release tags.
Release tags must identify one of the checked current tips. Deletions keep the existing behavior.

Any new commit, changed gate/checker, missing assertion, altered TRX, missing suite, skipped/failed
test, or missing named regression invalidates evidence. Rerun review and tests for the new pair.
A ledger reason alone cannot make this completion check pass.

These are local checks. Someone able to edit local source or receipts can falsify a review or
test artifact. They are not cryptographically attested and cannot prove semantic equivalence.
GitHub web/API actions and tools bypassing local hooks are not covered. No workflow, ruleset,
permission, authentication, or cloud lifecycle hook is configured by this change.

Run the isolated fixtures without executing release scripts:

```powershell
python build/Test-PdfCompletion.py
./build/Test-MaintenanceForwardPorts.ps1
```

The completion fixtures use synthetic TRX to test evidence acceptance/rejection. The snapshot
runner test mocks dotnet execution while using real Git archives. A real pre-push fixture pushes
only to a temporary local bare repository. These fixture passes are not production test receipts.
