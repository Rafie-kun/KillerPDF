# Session and temporary-file boundaries, 2026-10-19

BF-B005 and BF-B006 now have production service seams used by `App.xaml.cs`, with real Windows named-pipe and filesystem subprocess checks. These checks do not certify full GUI startup or delivery between two actual Windows sessions.

## Diagnosis and observed red results

The coordinator approved a behavior-preserving extraction before repair. The extracted cleanup preserved the original broad `killerpdf_*.pdf` sweep, and the extracted IPC preserved the original constant pipe name and successful secondary-launch exit after failed sending. The prior `App.xaml.cs` implementation is pinned by commit `deb2d54` for comparison.

Already-run baseline commands:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/steve/code/KillerPDF/tests/Test-ProcessTempFiles.ps1 -WorkDirectory C:/Users/steve/scratch/kp-boundaries-20261019/session-temp/temp-red
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/steve/code/KillerPDF/tests/Test-SingleInstanceChannel.ps1 -WorkDirectory C:/Users/steve/scratch/kp-boundaries-20261019/session-temp/pipe-red -Mode sessions
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/steve/code/KillerPDF/tests/Test-SingleInstanceChannel.ps1 -WorkDirectory C:/Users/steve/scratch/kp-boundaries-20261019/session-temp/forward-red -Mode missing
```

Observed results before repair, each test exit code 1:

- Temp owner subprocess remained alive, but its freshly written, closed working PDF was deleted: `working PDF survived startup cleanup=False`.
- Distinct logical session identities could not both create their real Windows pipe servers: one reported `IOException: All pipe instances are busy.`
- An absent primary returned `missing primary forwarding launch exit=0`.

Ranked temp hypotheses before repair: broad sweeping disregards ownership; shared directory permits cross-process deletion; age controls deletion. A fresh PDF being removed disproved the age explanation. A lifetime ownership lease and distinct owned directories address the first two.

Ranked IPC hypotheses before repair: constant pipe naming ignores session identity; launch forwarding masks failed sending; readiness timing causes the collision. Collision at server creation without a send attempt disproved the readiness explanation.

A subsequent lifecycle check reproduced an integration defect introduced by holding the new lease: after registered-file cleanup, recursive deletion of the owned directory failed with `IOException`. The already-run `temp-lifetime-red` command used `Test-ProcessTempFiles.ps1` and exited 1 with `FAIL lifetime lease blocks shutdown directory cleanup`. Ranked explanations were the exclusive lease, an unrelated regular sentinel, and a live child process. The child had already been killed and joined, and recursive deletion can remove ordinary sentinel files. Releasing the lease before the same deletion resolved it.

## Repair and green checks

`ProcessTempFiles` creates a GUID directory per process and holds its `owner.lease` exclusively for the process lifetime. Startup cleanup can claim and reclaim abandoned owned directories, but cannot claim an active process's lease. It leaves flat legacy files and directories without an ownership marker alone. It does not follow root, child-directory, or lease reparse points. Directory removal is nonrecursive, preserving unknown contents.

Disposal is idempotent, cleans registered files, and releases an already-created lease without creating a directory or deleting the ownership marker. Normal app exit and the isolated startup-smoke cleanup release the store before directory removal. Nonterminal registered-file cleanup keeps its existing meaning.

`SingleInstanceChannel` scopes both mutex and pipe names to account SID and Windows session ID. `App` supplies its real process identity. A failed secondary launch now returns exit code 1. Existing pipe options are preserved. `CurrentUserOnly` was considered and removed because Windows also checks elevation, which would change uninstall behavior. See [Microsoft's PipeOptions documentation](https://learn.microsoft.com/dotnet/api/system.io.pipes.pipeoptions).

Already-run final commands, each exit code 0 with zero build warnings and errors:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/steve/code/KillerPDF/tests/Test-ProcessTempFiles.ps1 -WorkDirectory C:/Users/steve/scratch/kp-boundaries-20261019/session-temp/temp-lifetime-green
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/steve/code/KillerPDF/tests/Test-SingleInstanceChannel.ps1 -WorkDirectory C:/Users/steve/scratch/kp-boundaries-20261019/session-temp/pipe-green-final -Mode sessions
powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/steve/code/KillerPDF/tests/Test-SingleInstanceChannel.ps1 -WorkDirectory C:/Users/steve/scratch/kp-boundaries-20261019/session-temp/forward-green-final -Mode missing
```

The six temp checks prove active-owner preservation, real root-junction refusal, killed-owner reclamation, legacy and unowned preservation, registered-file-only cleanup, and successful directory deletion after idempotent graceful release. The IPC check starts two child processes using distinct logical session IDs within one actual Windows session and verifies each receives only its expected payload. The absent-server check proves nonzero failure reporting. Each runner requires a new output directory; use fresh scratch paths when repeating.

## Limits and remaining acceptance

- No normal GUI startup was run against host local app data. Its registry registration and shared user state require isolation beyond changing TEMP and TMP.
- No second authenticated Windows session was available. Logical session IDs in the subprocess contract test do not establish real cross-session GUI acceptance.
- Elevated and unelevated process interaction remains untested.
- An older running version uses the former mutex and pipe names. Upgrade coexistence and uninstall interaction across that naming transition remain untested; there is no fallback to the old shared pipe.
- The temp lease is released by disposal or process termination, and abandoned owned directories can be reclaimed on a later startup. The service lifetime check passes; full GUI graceful-shutdown acceptance remains separate.
- Old flat temporary PDFs remain because their owner cannot be proven inactive. Explicit user data clearing remains a separate operation.
- This does not certify installer execution, native hardware, release parity, or all existing CLI workflows.

No host GUI, registry cleanup, second-session creation, or user temporary-file deletion occurred in these tests. Test child processes were stopped, and the task scratch artifacts remain for coordinator review.

## Final source SHA256

| File | SHA256 |
| --- | --- |
| App.xaml.cs | 5255DC914831AB94C6FEF2A88D697064B3F4F80FB591C8DC68E9C1BB0B12BEE5 |
| App.StartupSmoke.cs | E523B1C63FED284FDB7E5D20141A35388AE4AC744634421A3EC4CDB932B7758C |
| Services/ProcessTempFiles.cs | D4F50EA8430D2B77AFA2D29D7C442628594682FBE9C9847E2D733A9530434CC4 |
| Services/SingleInstanceChannel.cs | 498BB41E52C35B27CB20FA7EBEF329B4BE34A3ED5F941DBBEAF5F0041774B2BE |
| tests/Test-ProcessTempFiles.ps1 | 39D0A3C2F8AF2658B8DA66E5A4C7C0F8AF59C41C577B17CC79A09EA6A2314063 |
| tests/Test-SingleInstanceChannel.ps1 | 0270435A42B74792BA8E67C15F420E9E17D3C360D051AD1F400FD3D503A65348 |
| tests/KillerPDF.BoundarySmoke/ProcessTempProbe.cs.txt | 70FFBA935212CFB03BD673843657E1F718EFE0A69E58320D4BCE69A4E80FE271 |
| tests/KillerPDF.BoundarySmoke/SingleInstanceProbe.cs.txt | 7BC50B3197199F144070E3B70D2BAD86DF7BA32F7E32B3FA863CE0E4A48C0B81 |
