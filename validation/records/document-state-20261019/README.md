# Document state remediation evidence

October 19, 2026. KillerPDF 1.9.0.

BF-F001, BF-F003, BF-F004, BF-F002 and BF-F006 were reproduced through actual synthetic MainWindow workflows, repaired, and verified. The final immutable candidate passed all nine cases. Detailed commands, source and assembly hashes, ranked hypotheses, verdicts and limitations are in [results.json](results.json); captured failure and success text is in [red-green.log](red-green.log).

The retained test runner and reproducible instructions are in [the document-state smoke tooling](../../../tests/KillerPDF.DocumentStateSmoke/README.md). Reflection initializes the isolated host and calls the public opening entry point. No business-private methods or editor collections form the regression seam.

| Snapshot repair | Red result | Green result |
| --- | --- | --- |
| BF-F001 | Right-pane SECRET remained extractable. | SECRET is absent; KEPT-RIGHT survives. |
| BF-F003 | Save and DataMerge macro output rotation was 0. | Both outputs retain 90 degrees; DataMerge also writes its selected record. |
| BF-F004 | Extraction returned Original instead of pending Updated. | Updated survives; rotated extraction retains exactly 90 degrees. |
| BF-F002 | Rotated page preview raster was portrait, 921x1536. | Preview raster is landscape, 1536x921. |
| BF-F006 | Preview appeared after switching documents during preparation. | A public Canceled status confirms completion; no stale preview appears. |

The source-choice feature is a separate test and staging group. `macro-source` checks current-document selection inside the macro window; `macro-batch` checks direct routing to the batch picker. Both pass. Existing localized labels are reused. The source-choice change is in Controls/MacroDialog.cs and the OpenMacroDialog hunk of Shell/Macros.cs.

[Current-document screenshot](macro-current.png) and [batch screenshot](macro-batch.png) each decode to 900x610. Source and retained PNG hashes and dimensions were compared before and after copying and match exactly. The Dark current-document capture was visually inspected.

These checks do not prove physical input, hardware printing, full locale/theme/DPI coverage, or raster-redaction fallback behavior. Complete saved-copy components persist additional overlays, stamps and measurements; new output cases directly verify forms, rotation and redaction. The unsupported-annotation exception retains an English diagnostic under a localized print-failure wrapper because no existing localized detail matches and all current concrete annotation types are supported.

Final test process settings and temporary files stayed under their task output folders. Earlier diagnostics used the existing startup-smoke temporary-directory convention before this was corrected; exact unrecorded roots were not broadly deleted. No user application was attached to or closed.
