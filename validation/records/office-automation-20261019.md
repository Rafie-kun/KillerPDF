# Office automation boundary, October 19, 2026

BF-B001 is partially repaired. Word and Excel now force AutomationSecurity to 3 only while opening a binary document, then restore the previous value even when Open fails. This protects the VBA automation boundary. Excel 4.0 macros remain unresolved because [Microsoft documents that ForceDisable does not disable them](https://learn.microsoft.com/en-us/office/vba/api/excel.application.automationsecurity). [Word documents the same default and restoration requirement](https://learn.microsoft.com/en-us/office/vba/api/word.application.automationsecurity). No existing compound-file or BIFF parser was found to safely identify those inputs before Office opens them.

The test seam is the external COM application factory. The unchanged default resolves the Office ProgID and creates the application. Tests supply managed objects only at that system boundary, then drive the actual ToPdf content routing, STA thread, late-bound Open, export, exception propagation, and temporary output cleanup. No Office process or adversarial document ran on the host.

Ranked hypotheses: (1) Open observes the default security value 1 because it is never changed; (2) failure during Open can bypass restoration unless it is in finally; (3) Word needs the same guard; (4) application visibility changes ownership cleanup but does not disable macros.

Command:

```powershell
dotnet test KillerPDF\KillerPDF.Tests\KillerPDF.Tests.csproj --no-restore --filter "FullyQualifiedName~OfficeAutomationSecurityTests|FullyQualifiedName~OfficeConversionTests" -p:WarningLevel=0
```

Before the fix, all four new cases failed: Word and Excel, successful and throwing Open, each observed security 1 instead of 3. The Excel fake first needed Optional parameter metadata to model Type.Missing correctly; that harness error was corrected before accepting its red verdict. After the fix, all 20 focused tests passed, including existing internal document conversion tests.

The subsequent non-UI followup adds four cases for a visible external application whose initial security setting is 2. Both document formats and successful/throwing Open restore that original setting and leave the visible application running. All 24 focused tests pass. These use the same fake COM boundary; no Office window or process opens.

The disposable VM was running with a clean-install checkpoint and Guest Service Interface enabled, but there were zero authenticated PowerShell sessions. Guest execution is unavailable. Actual Office trust policies, Protected View, VBA execution prevention, and Excel 4.0 behavior still need the disposable Office lab. These automated tests are boundary evidence, not Office acceptance or full macro-safety certification.
