# BF-L003 horizontal rail shadow composition

October 19, 2026. Actual isolated MainWindow rendering at 1000x700, 100% app scale, and 96 DPI. Startup settings select rail side and theme; actual chevron and split-button events select expanded and split states. The split window is resized through its public Width property so both visible viewers fit in the capture. Private reflection is limited to isolated application/settings/resource bootstrap.

The original Dark collapsed top/bottom case failed the rendered regression: mean rail-boundary brightness differed by 11.1135 levels. The diagnostic samples eight rows outside the pane at the rail boundary across a 40px center band. It requires a paired mean difference no greater than 2 to tolerate the existing grain. Downward shadow depth and the caster's 1px offset were the suspected causes; symmetric rail padding and theme color alone could not explain the directional difference.

The viewer now splits shadow composition into two parent-clipped containers. The original downward theme effect remains on the non-rail half. Only the rail-facing half uses a local centered clone with matching theme opacity and blur. Clipping happens on the parent after the child's effect. The global PaneShadowEffect and footer geometry remain unchanged. A binding tracks resource replacement and preserves the absence of an effect on 98SE.

All 24 representative cases pass: Dark, Light, and 98SE; top and bottom; expanded and collapsed; single and actual split viewers. The 12 paired mean differences range from 0 through 0.7042. Every complete 24px footer row has an identical before/after pixel hash. Actual split widths are 480px per pane in modern themes and 483px in 98SE. The source files and host setter calls match the tested snapshot by SHA-256.

A live theme-selector cycle also passes: Dark to 98SE to Light to Dark. Both effects are null in 98SE; modern themes retain source depth 5 and centered depth 0. Actual RadioButton state changes drive the existing theme handlers.

The original candidate was installer/cli-build/KillerPDF.dll and the green candidate was installer/combined-green-v3/KillerPDF.dll under C:/Users/steve/scratch/kp-fresh-remediation-20261019. The green app built with zero errors and warnings. Compact complete measurements are in metrics.json. Representative Dark before/after, Light split, and 98SE images were visually inspected. The full temporary render matrix is not needed to rerun the retained regression.

```powershell
./tests/Test-RailShadow.ps1 -AssemblyPath <current-KillerPDF.dll> -WorkDirectory C:/Users/steve/scratch/rail-shadow-check -Theme Light -Split -Expanded
```

Use a new scratch directory. Add -CycleThemes for the live selector check. Optional -BaselineDirectory compares exact footer hashes with a previous runner output. The runner copies .cs.txt tooling into scratch to avoid application source globs. Process-only TEMP/TMP and exact SmokeDataRoot logging keep temporary app files under the output folder. The retained runner passed a fresh Light split/expanded rerun.

Limits: empty synthetic viewers, three representative themes, 96 DPI, and 100% app scale. This does not claim populated-document, native-monitor DPI, every locale/theme/scale, native pointer, or normal application shutdown acceptance. No shared theme loader or global opacity/depth value was edited.
