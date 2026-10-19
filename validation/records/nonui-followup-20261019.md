# Headless follow-up review, October 19, 2026

An independent review of the new temporary-file lifecycle found and repaired one concrete retry regression in f6d9ea8. No windows, Office processes, installer entry points, or VM interaction were used.

Reproduction used the actual public ProcessTempFiles methods: create and write a tracked synthetic PDF, hold it open without delete sharing, dispose its owner, run stale cleanup while the file remains locked, release the reader, then run cleanup again. The original implementation deleted owner.lease even though PDF deletion failed. The second sweep skipped the remaining directory because it no longer had a lease. The probe failed with exit 1 and reported that the file remained after the lock was released.

Ranked hypotheses before the probe: (1) deleting the lease after a failed PDF deletion permanently prevents retry; (2) failed directory deletion might preserve the lease automatically; (3) an active owner's exclusive lease should prevent cleanup before disposal. The probe confirmed the first cause and verified the active-owner control.

The repair retains the lease whenever any other directory entry remains. It keeps the existing nonrecursive removal policy. After the repair, the same scratch probe reported lease present after failed deletion and successfully reclaimed the formerly locked PDF on the next sweep.

```powershell
dotnet run --project C:\Users\steve\scratch\kp-boundaries-20261019\nonui-review\office\Probe.csproj
powershell -NoProfile -ExecutionPolicy Bypass -File KillerPDF\tests\Test-ProcessTempFiles.ps1 -WorkDirectory C:\Users\steve\scratch\kp-boundaries-20261019\nonui-review\office\retained-probe-green
```

The retained probe passed all seven checks: active owner preservation, root junction refusal, crashed owner reclamation, locked stale-file retry, unowned file preservation, registered-file cleanup, and graceful lease release. Its build had zero warnings and errors. An initial repeat of the scratch probe accidentally included the nested retained probe through default source globs; explicit source includes corrected that harness error before the successful repeat.

Read-only inspection of the new session, download, and CSV changes found no additional concrete regression. Mutex and pipe names use the same account/session scope. TempDir callers use tracked paths except the existing clear-data operation. The CLI OCR destination replacement remains after successful completion of the temporary download. These observations do not add UI or installer acceptance claims.

A separate worker checked the destination validator it did not implement. Thirteen additional cases passed: legitimate nested payloads and path normalization were accepted; duplicate entries, traversal, rooted paths, drive-qualified paths, empty/dot/dotdot segments, alternate data stream syntax, self-manifest entries, missing members, and invalid hashes were rejected. The exact replay is `powershell -NoProfile -ExecutionPolicy Bypass -File C:/Users/steve/scratch/kp-boundaries-20261019/nonui-review/network/manifest-probe.ps1`. It loads the compiled validator only, without invoking the installer entry point or changing registration. Update handoff and generated wrapper inspection found no additional mismatch.

The coordinator added four Office boundary cases for visible instances with a nondefault initial security value. All 24 focused Office tests pass, including restoration on successful and throwing Open. These instantiate only managed COM substitutes, without Office windows or processes. See [Office evidence](office-automation-20261019.md).
