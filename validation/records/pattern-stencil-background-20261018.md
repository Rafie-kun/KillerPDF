# Shading-pattern stencil diagnosis, October 18, 2026

The actual candidate render of PDF.js `issue13372.pdf` disagrees with both PDFium
and Poppler. The source paints a CCITT image stencil with a DeviceRGB axial
shading pattern, a cyan background and cyan/yellow/magenta function colors.
Its valid colors always have either red or green at full intensity, even after
blending with white. The candidate has 37,913 pixels violating that invariant
in the 512-pixel capture; PDFium has none. The older frozen engine was blank.

Ranked hypotheses:

1. Fractional stencil coverage is applied separately to the pattern background
   and shading instead of to their completed result, leaking background color.
2. The shading function or its coordinate transform produces incorrect colors.
3. CCITT decoding changes mask shape, or mask interpolation corrupts color.
4. DeviceRGB color conversion introduces an unrelated color-management error.

The public seam is `PdfPageRenderer.Render`: a small constant-red shading with
a cyan background, painted through a downsampled checkerboard image mask. It
removes CCITT, stitched functions and color profiles while retaining fractional
coverage. Red over white must retain full red intensity. The test failed before
the renderer change: expected red 255, actual 191, in 264 ms. That rules out
CCITT decoding, stitched functions and profiles as necessary causes. No private
renderer state is asserted. The proposed repair applies the painted object's
coverage after the pattern's background and shading have been composited.
The rebuilt test passes in 311 ms. All 233 related shading, pattern and stencil
checks pass. Engine and test-output assembly hashes match:
`F662FF10C6D5A19B45CFA065CA2EEDD94A5DBC75FED6E904E5F95AFC7C551C7D`.
An initial attempted green run used the preceding engine binary because
project-reference builds were disabled; that stale run is not fix evidence.
The complete engine suite passes 6,562 tests. The actual Release app embeds the
new engine, verified by decompressing and hashing its Costura resource:
`DE1B12C01918078EE6EF3096008AB7B61B45B2F50BDAB2F2034D68AD04C89218`.
Release app SHA-256:
`3FD59678B522C117E6B97DE3C870A530221191154E06480323AC4DA123211C1B`.
The original 512-pixel render now has zero invariant violations. Its difference
from PDFium falls from 32.19% to 4.22% at channel delta 32; that remaining
difference still requires disposition under the strict release threshold.
The complete ordinary app suite passes 834 tests. This project links application
source files; actual executable behavior is separately verified by the CLI runs.
Debug and Release engine/app builds pass without warnings or errors when using
the already-built vendored dependencies.

The complete before/after pixel comparison covers all 649 conformance and 976
PDF.js sources at 512 and 2048. No conformance pixels or status rows change.
Exactly two PDF.js pages change at each size, with no changed status rows:
`issue13372.pdf` and `issue18816.pdf` at 512; `issue19360.pdf` and
`transparency_group.pdf` at 2048. All four final previews were visually inspected.
Every affected comparison is unchanged or closer to both external renderers:

| Page | Size | PDFium before/after | Poppler before/after |
| --- | --- | --- | --- |
| issue13372 | 512 | 32.193% / 4.223% | 33.607% / 14.619% |
| issue18816 | 512 | 0.499% / 0.499% | 0.976% / 0.976% |
| issue19360 | 2048 | 0.053% / 0.048% | 0.491% / 0.482% |
| transparency_group | 2048 | 9.221% / 9.177% | 10.654% / 10.645% |

These are significant-pixel fractions at channel delta 32. Strict oracle
differences remain in issue13372 and transparency_group. The latter also has
pre-existing visible stippling absent from both external images and needs a
separate diagnosis. This fix is not a complete renderer-parity claim.

Evidence is under
`C:\Users\steve\scratch\kp-bench-render\form-workflows-20261018`:
`pattern-stencil-red`, `pattern-stencil-green-rebuilt`, `pattern-stencil-related`,
`pattern-stencil-engine-final`, `pattern-stencil-app-final`,
`pattern-stencil-pixels` and
`release-pixels-512-2048-v2/pattern-stencil-external-v2`.
Source and payload identities remain checked throughout the comparisons.
