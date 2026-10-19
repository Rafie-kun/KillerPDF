# BF-F007 signature picker lifecycle

October 19, 2026. Actual isolated MainWindow, seeded inert signature, synthetic unsigned signature-field PDFs, and routed control events. No private business method was invoked. Host bootstrap bypassed normal app startup, isolated settings/data, and disabled window activation.

The original candidate reproduced both failures: dismiss/reopen the picker, or open another document while a field was waiting, then choose a saved signature. Both incorrectly reported Field signed rather than free placement. The first cancellation repair passed those cases and ordinary direct field selection.

A second real two-pane case remained red: preselect Signature tool in the right pane, activate left, click its field, activate right without opening a document, and choose the still-visible saved tile. The right pane incorrectly signed the left field target. Capturing source viewer and document identity and checking both before placement fixed this case.

Pane switching also left an invisible old picker attached to its original parent. The focused pane changed before HideSignaturePopup selected a removal parent. The regression counted one stale saved tile after the fade; capturing the popup's actual parent removes it and the count is now zero.

Dismissal clears the pending target. Successful selection captures it before hiding. Internal rebuild/create/import transitions preserve field intent. A creator-cancel-then-select regression caught and prevented an unintended loss of field intent while implementing cancellation.

All six final control workflows pass: dismiss/reopen, document switch/reopen, ordinary direct field selection, creator cancellation, pane switch, and old-picker removal. The combined candidate built with zero warnings/errors at C:/Users/steve/scratch/kp-fresh-remediation-20261019/installer/rail-green/KillerPDF.dll. It included the final Signing.cs containing the source identity and actual-parent fixes. Results and red checkpoints are in results.json.

Run a workflow against a freshly built matching application assembly:

```powershell
./tests/Test-SignaturePicker.ps1 -AssemblyPath <current-KillerPDF.dll> -WorkDirectory C:/Users/steve/scratch/signature-picker-check -Mode pane
```

Supported modes: dismiss, switch, direct, create-cancel, pane, orphan. Use a new scratch directory per run. The runner stores .cs.txt in the repository to avoid application source globs and compiles its scratch copy.

These are actual control/event acceptance checks using an isolated synthetic window. They assert visible status and picker removal. They do not claim native pointer automation, final saved PDF/signature validation, image import acceptance, or a normal application shutdown. The owned diagnostic exits after recording its verdict to avoid an unsaved-changes prompt from an intentional red case.
