# Release pixel accuracy checkpoint, October 18, 2026

The actual Release executables rendered all 649 conformance inputs and all 976
PDF.js inputs at both 512 and 2048. Conformance selects page one; PDF.js selects
up to three pages, matching the accepted comparison's page selection. Every source
PDF and both payloads were hashed, and the runner verified that they stayed
unchanged. Outputs were compared using decoded RGBA pixel hashes. Source and
payload manifests, CSV outcomes and rendered pages remain in the owned run.
Timing is not used as performance evidence.

Frozen reference: 096a2cf application SHA-256
`21B1145BD7078C23B693E57D3DF123B5EF2E0F3D2414549885F3BEBF06BF0608`.
Candidate: the Release build containing 7353e08's source, application SHA-256
`8EFBDA45607B0A173232984DD7938BE2941B685330B87F7242EEA15084CD809E`.
Costura embeds the engine in the application DLL, so payload identity includes
that DLL, executable, dependency metadata and runtime configuration.

## Results

| Set | Size | Reference pages | Candidate pages | Changed pixel hashes | Changed status/detail rows |
| --- | --- | --- | --- | --- | --- |
| Conformance | 512 | 634 | 634 | 572 | 0 |
| Conformance | 2048 | 634 | 634 | 573 | 0 |
| PDF.js | 512 | 1,098 | 1,100 | 764 | 18 |
| PDF.js | 2048 | 1,098 | 1,100 | 765 | 18 |

Changed hashes include added pages. A1 is not passed. This comparison spans all
changes since the frozen reference; it does not attribute every difference to the
new work safeguard. Existing quarter-pixel glyph placement is one known source
of changed text edges, but individual differences still require disposition.
A two-way reference comparison does not establish three-oracle consensus for A2.

Difference ranking uses the fraction of pixels with any channel delta above 32.
The following counts only prioritize review; limited differences are not accepted
automatically.

| Set | Size | At most 2% significant pixels | Above 2% | Added pages |
| --- | --- | --- | --- | --- |
| Conformance | 512 | 570 | 2 | 0 |
| Conformance | 2048 | 571 | 2 | 0 |
| PDF.js | 512 | 716 | 46 | 2 |
| PDF.js | 2048 | 716 | 47 | 2 |

There are 97 significant page/size cases across 50 distinct pages. None of the
reference/candidate pairs changed dimensions. Scores and names are retained in
`difference-scores.json`, produced by `Score-ReleasePixels.py`.

The conformance accepted CSV and fresh frozen executable both record 634 OK and
15 identical skips, not the old prose count of 639 plus ten. They comprise eleven
protected inputs without supplied credentials, two missing-page inputs and two
without recoverable indirect objects. All remain in the denominator. The accepted
PDF.js CSV and current folder contain exactly the same 976 unique file names;
the old 977 count was a documentation error. No input was removed or waived.

## Status triage

`PDFBOX-4352-0.pdf` changes from authentication skip to a rendered page.
`bug1755507.pdf` changes from an out-of-range failure to a rendered page. Fifteen
other changed rows clear or reduce existing glyph/text/resource diagnostics.
The remaining changed row adds a SimSun-ExtB glyph diagnostic in `issue20489.pdf`.
These status changes are identical at both resolutions.

The new warning reproduces through the actual executable with a one-file render,
and through the public renderer using the application's installed-font resolver.
Hypotheses tested were missing family resolution, a persisted-index problem,
decoder differences, and a specific unmapped source character. The source has
non-embedded TrueType resources using WinAnsiEncoding. A diagnostic walk of all
four text-showing operators isolates source code 10: it decodes to the replacement
character and has no outline. Its width is zero in the other two fonts but 1000
in SimSun-ExtB, which explains why that resource produces the warning. The
installed SimSun-ExtB font is indexed and its ordinary characters resolve.
This establishes a specific unmapped-source-code diagnostic rather than failure
to locate the family. No font warning was suppressed and no font code changed.
Poppler also reports unavailable source fonts and substitutes Latin glyphs on this
sample, so it is not a reliable visual oracle for its non-embedded Chinese text.

## Evidence and remaining work

Run root:
`C:\Users\steve\scratch\kp-bench-render\form-workflows-20261018`.
The `release-pixels-512-2048-v2` folder contains all rendered pages,
`identity.json`, `source-manifest.json`, `results.json`, CSVs and process logs.
`Check-ReleasePixels.py` is the owned comparison runner. The initial empty output
folder records a fixture assumption corrected before rendering: Release embeds
the engine rather than shipping a separate engine DLL.

`issue20489-alone.csv` is the actual executable reproduction;
`issue20489-font-diagnostic-final.log` is the final glyph walk, including quote
operators and q/Q font restoration. Earlier diagnostic iterations omitted those
operators and are not the disposition evidence. The diagnostic reflection only
selects the same recovery decoder used by the renderer; public rendering and
glyph/width observations establish the outcome.

The first external-oracle wrapper supplied a current parallelism option to the
older 1.8 executable, which rejected the command as usage. Its missing images
were fixture failures, not product failures. The corrected wrapper removes that
option and rejects invalid exits or absent CSVs immediately. Its fresh evidence
directory is `external-oracles-v2`; the original `external-oracles` run cannot
establish three-oracle agreement.

Both external renderers produced all 97 selected page/size cases. Their source
hashes match the original manifest. Twenty-nine cases across 21 distinct pages
exceed the strict 2%/32 threshold against both oracles. When image dimensions
differ by at most two pixels, the oracle image is Lanczos-aligned to the candidate
for this triage metric, matching the existing runner; original dimensions and
alignment are recorded. This does not change the exact A1 comparison.

The source-backed pattern/stencil color defect in `issue13372.pdf` is repaired;
see [its diagnosis](pattern-stencil-background-20261018.md). The rebuilt Release
app is `3FD59678B522C117E6B97DE3C870A530221191154E06480323AC4DA123211C1B`.
Its complete rerun changes only four page/size cases, with no changed outcomes.
All four are unchanged or closer to both external renderers. The repeat and
`updated-a1-results.json` are in `pattern-stencil-pixels` under the same run root.
Against the frozen reference, conformance remains 572/573 changed hashes and
PDF.js now has 765 at each size. The additional 512 difference is a reviewed
fractional shading edge in issue18816, not an omitted or newly failed input.

The later [profile-cache repair](profile-color-parallelism-20261018.md) removes
the stippling, and the [page-group backdrop repair](page-group-backdrop-20261018.md)
corrects the reproduced RGB blend order. The current Release application hash is
`32A9375BCCC531B1A3C8757244B681431FFA0E65E6345A7708503BF0F84FC97E`.
`Reconcile-CurrentPixels.py` compares its retained actual-executable captures to
the frozen accepted baseline, checking application and source identities before
and after. `page-group-a1-reconciliation` retains results and decoded RGBA hash
inventories. Conformance has 572 changed hashes at 512 and 573 at 2048; PDF.js has
770 and 769, respectively. Render counts and the eighteen PDF.js status changes
from the original comparison remain unchanged. A1 remains open.

Strict oracle differences remain. A2 has only been sampled on selected changed
pages, not every shared corpus page. Next: record remaining source-backed
dispositions and complete the all-shared-page review. The latest page-group
review is a different subset from the initial 97 cases; its counts must not be
substituted for a complete A2 result.
Physical input, assistive technology, display scaling and the other release gates
remain separate work.
