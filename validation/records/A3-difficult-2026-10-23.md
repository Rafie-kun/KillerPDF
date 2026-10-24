# A3 difficult set, 2026-10-23

Gate A3: all 76 pages (up to three per file) of the 41-file difficult set, rendered by 1.9 at a 2048-pixel long edge and visually dispositioned. Oracles are the frozen renders of the 2026-10-19 ledger (`accuracy-remediation-20261019/difficult/final-a04/strict-ledger.json`): v1.8.80 (PDFium) and pdftoppm 26.09.0. Inputs are verified against that run's SHA-256 manifest before each render. Metric: the share of pixels where any channel differs by more than 32; a page is a strict outlier when it exceeds 2% against both oracles. A strict outlier is a prompt for review, not a verdict: the verdict is visual.

## Review at c6e13fd

Every page was reviewed on a sheet (1.9, PDFium, Poppler, difference mask) and a full-resolution crop of its densest difference, with the PDF's own objects read where a verdict depended on them. Run, sheets, crops and per-page verdicts with evidence: `C:/Users/steve/scratch/kp-bench-render/a3-20261023-c6e13fd/` (`REVIEW-BRIEF.md`, `dispositions-g1.json` to `dispositions-g4.json`).

| Verdict | Pages |
| --- | ---: |
| Equivalent: matches an oracle in content, position, color and weight; counted pixels are edges | 48 |
| Accepted difference: visible, but 1.9 is right or the behavior is defined, with a source-backed reason | 23 |
| Defect | 5 |

## Defects

| Page | Defect | Fix |
| --- | --- | --- |
| CompactedPDFSyntaxMatrix p1 | Table rules are 0.34 pt filled rectangles (0.59 px at 2048). 1.9 painted them at area coverage, a mid-gray grid, where both oracles paint one solid black pixel. | 7117c1f. Owner decision: a filled rule thinner than a pixel draws at least one solid pixel, as 1.8, Poppler and Acrobat do. |
| 42828.0001.001 p1 | A JBIG2 scan reduced to fit dropped one output column at x=724, shifting everything right of it 1 px left. | 6de46ce. The reduced image plane has one cell per pixel center it covers, so every column is kept; 4d82d0c then gives each cell the source under its own pixel (below). |
| Ghent_PDF-Output-Test-V50_ALL_X4 p3 | GWG 9.0 Type 3 bitmap glyphs (1-bit image masks reduced about 2.9 times) drew broken, with the tops of glyphs missing. | 6de46ce, the same plane alignment, which these axis-aligned mask glyphs read through; refined in 4d82d0c. |
| GWG061_Shading_x1a p1 | A 6 by 6 px wedge above the top-left corner of the page frame. The path closes on a copy of its start point, and stroke adjustment snapped the copies differently, so the join became a miter spike. | cd623e1. Repeated vertices share their snapping constraints, and near-zero segments are dropped before joins. |
| altona_technical_1v2_x3 p1 | Rows R and T: DeviceN colors made only of spot and None colorants knocked out instead of overprinting. | c0c5106 for row R and row T's vector patches. 8d94c1d for row T's image cells, which use a two-spot indexed palette (below). |

## Accepted differences

Twenty-three pages. Most are cases where 1.9 follows the file or the test's own instructions and one oracle does not:

- Ghent and GWG patches whose printed pass criterion 1.9 meets: Ghent ALL p1 and p2, ICC-CMS p1 and p2, GWG010, GWG1610, GWG1611, GWG168, GWG169, GWG180 and GWG182.
- Source-defined behavior: CompactedPDFSyntaxTest (its empty-name ExtGState sets /ca 0.33 and /CA 0.66; 1.9 and Poppler apply it, PDFium paints opaque) and PDF 2.0 image with BPC (the file's text says how its CalRGB and DeviceRGB images should differ).
- Damaged or incomplete files: 382252 and 4387ba label a PNG stream /DCTDecode, and 1.9 recovers the image; AIAA p1's footnote font is a missing object.
- Unembedded fonts substituted differently by each renderer: 210260 p2 and p3.
- Image smoothing: 210260 p1, 495712 p1 and p2, UMURF p1 and p3 were accepted at c6e13fd because 1.9 then never smoothed images without /Interpolate. That was changed by the owner decision below.

## Owner decisions, 2026-10-23

- Image smoothing (d1d2ee6, refined in 7e16e97 and 52c32c4, threshold revised in 21676b2). Enlarged continuous-tone images are smoothed as 1.8 shows them, from any enlargement, and an explicit /Interpolate true is always honored. The first decision started smoothing at about 1.5 times; Steve revised it the same day to match 1.8, because three difficult-set images enlarged 1.24 to 1.4 times looked jagged where both oracles smooth them. 1-bit images and image masks stay crisp. Measured cost on the difficult set: +3.4% in total render time (11,397 to 11,785 ms mean of three runs), concentrated on pages with enlarged images (+35 to 80 ms each).
  - Like 1.8 (PDFium) and Poppler, smoothing stops once an image covers about eight device pixels per sample. 1.8 uses bilinear only while `dest_height / 8 < src_width * src_height / dest_width`; Poppler stops at four times on either axis. The first smoothing build smoothed without that limit and turned pdf.js issue11124, a 4 by 4 swatch filling the page, into a wash. It now stays crisp (52c32c4).
- Thin rules (7117c1f): a filled rectangle thinner than a pixel draws at least one solid pixel wide.

## Regressions found by the rerun, fixed

- GWG169 (7e16e97): smoothing an image converted through the destination plane blended four samples' colors but read the soft mask from one, so colors the mask hides bled into the visible edge as a gray outline. Colors now weigh by mask value and the mask is interpolated on the same grid.
- Plain gray and RGB images on CMYK pages (52c32c4) took a nearest-sample fast path and were never smoothed, even with /Interpolate true. They now smooth through the plane.
- Reduced images that start partway into a pixel (4d82d0c), found by the A2 rerun rather than A3. The 6de46ce plane has one cell per pixel, but each cell still averaged an even share of the image, as if it started on a pixel edge. Stacked scan strips (PDF-HUL-164 0000490) moved by up to half a pixel, by different amounts from strip to strip, and a placed mask (PDF-HUL-85) by up to 0.7 px. Each cell now averages the source under its own pixel, and the end cells reach the image's edges so content under a partly covered edge pixel is kept. Measured against PDFium, 0000490's vertical offsets went from 0.2 to 0.5 px down to at most 0.17 px. Horizontally, 1.9 now places the strips where they are; PDFium snaps their left edge to a whole pixel, so that page sits about 0.2 px from PDFium on the left and matches Poppler there.

## Final run

At 4d82d0c (Release build; payload hashes in the run's `payload-manifest.json`): all 76 pages rendered, none missing, 44 strict outliers against 45 at c6e13fd. 210260 p1 is no longer an outlier now that its photo is smoothed. Run: `C:/Users/steve/scratch/kp-bench-render/a3-20261023-4d82d0c/`. The engine at 8a93ac5, after the review repairs below, renders all 76 pages pixel-identical to this run.

Pages whose pixels changed since the reviewed renders were checked again: 28 differ, 13 by more than 0.1% of pixels. For each of those 13, a before-and-after crop at the densest change sits beside PDFium (`change-crops/`):

- The defect fixes hold: CompactedPDFSyntaxMatrix's rules are solid like PDFium's, 42828 p1 keeps every column with no offset, altona_technical row R and row T's vector patches overprint, and GWG061's corner is square (its only 30 changed pixels are the removed wedge).
- Smoothing now matches PDFium on 210260 p1, UMURF p1, GWG180 and GWG181.
- The soft-mask pages (GWG1610, GWG168) show no edge fringe.
- 382252, 4387ba and GWG130 p1 and p2 change by under 0.7% of pixels, with no visible difference.

Every one of the 76 pages keeps its review verdict, except the defects, which are fixed, and 210260 p1, now equivalent.

At 21676b2, with smoothing from any enlargement, 495712 p1 (the swoosh logo, 1.4 times) and p2 (the fleur-de-lis photo, 1.24 times) and UMURF p3 (the race-car photo, 1.38 times) are smoothed as PDFium smooths them; a before, after and PDFium crop of each is in `C:/Users/steve/scratch/kp-bench-render/a3-20261023-21676b2/`. They move from accepted difference to equivalent, and every score that changed moved toward the oracles. They stay strict outliers on glyph and photo edges, as the review found for the rest of each page. emi15514 p1 and p2 also change, with unchanged scores. 44 strict outliers, none missing.

With all five smoothing pages now equivalent (210260 p1 and UMURF p1 since 4d82d0c, these three since 21676b2), the 76 pages stand at 53 equivalent, 18 accepted differences and 5 defects fixed.

The interim runs at d1d2ee6 and 52c32c4 found the GWG169 fringe and, through A2, the issue11124 wash; both are fixed above.

The round H review repairs (1e39a5a to 0ba31e0, [review record](adversarial-review-20261023-release.md)) render all 76 pages pixel-identical to 4d82d0c, so none of the cases they repair changes a pixel in this set; their own tests cover them. cc4c502, which puts a page's first spot-palette image on its spot plate, is pixel-identical too: it changes plates, not these composites. So are the round I repairs at 7e04a4b. The round J repairs at 28216a5 changed one page, altona_technical p1 (0.3% of pixels), slightly closer to both oracles: its spot-colored image masks kept their spot (JF-002), so in the two overprint blocks (columns 1 to 24) they overprinted the process stripes, as overprinting a spot color must and as the vector patches in the same rows do, and in the knockout block (columns 25 to 36) they changed by at most one level. That repair was reverted in 8a93ac5 after round K found it broke spot stencils inside transparency groups and at opacities below 1, so these stencil patches knock the stripes out again; 8a93ac5 renders all 76 pages pixel-identical to 7e04a4b.

8d94c1d carries spot data through image painting (review round L), which closes the spot-image open item: DeviceN images with a spot, indexed images over two spots, reduced Separation images that are not opaque, and spot-filled image masks and Type 3 bitmap glyphs now reach their spot plates. All 76 pages render and the 44 strict outliers are unchanged. Against 21676b2 one page changes, altona_technical p1 (0.25% of pixels), slightly closer to both oracles (PDFium 24.94% to 24.91%, Poppler 32.62% to 32.60%). In the two overprint blocks, the spot-filled image-mask cells and row T's two-spot palette image cells now overprint the process stripes, as the sheet asks and as the vector patches in the same rows do; PDFium knocks them out. The round L repairs at 5a6ef60 render all 76 pages pixel-identical to 8d94c1d. Runs: `C:/Users/steve/scratch/kp-bench-render/a3-20261023-8d94c1d/` (before, after and PDFium crops in `change-crops/`) and `a3-20261023-5a6ef60/`, both made with `run-gates.ps1` there.

## Open items

- RGB pages simulate overprint for DeviceN only with a single spot (the RGB spot shadow preflight). Two-spot DeviceN overprint is simulated on pages with an output intent or a CMYK group, which is what the GWG and altona sheets use. Recorded as scope, not a defect.
- The render log does not mention the PNG-labelled-DCT recovery on 382252 and 4387ba. Minor diagnostics gap.
