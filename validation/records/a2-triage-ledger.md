# A2 three-oracle triage ledger

Started 2026-10-10 against 1.9 at 096a2cf plus 2c76ca2 and 6ab5428 (both pixel-neutral), v1.8.80 PDFium payload and poppler pdftoppm 26.09.0. Method and raw results: `C:/Users/steve/kp-bench-render/release-19-triage/` (`triage.py`, per-set `compare.csv`, `outliers.md`, `triptychs/`). Page 1 of every input. Threshold: 4x box-downsampled, more than 2% of cells with any channel delta over 32. "Outlier" means 1.9 disagrees with both oracles while they agree; "all-disagree" means all three differ and the vote cannot decide.

## Counts

| Set | Agree | Outlier | All-disagree | One oracle differs | No oracle | 1.9 missing |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| conformance 512 | 481 | 16 | 38 | 58 | 28 | 15 |
| conformance 2048 | | 3 | 44 | 106 | 28 | |
| pdf.js 512 | | 36 | 74 | 219 | 4 | |
| pdf.js 2048 | pending | | | | | |

"1.9 missing" on conformance is the ten documented unrenderable inputs plus files the batch could not open; "no oracle" is where neither PDFium nor poppler produced a page.

## Dispositions

Status values: defect (1.9 wrong, fix with regression test), accepted (1.9 right or spec-permitted, oracles wrong or limited), review (needs the source or a pdf.js reference image), minor (anti-aliasing or font weight, not a content difference).

| Set | File | 1.9 vs PDFium | 1.9 vs poppler | Oracles | Status | Note |
| --- | --- | ---: | ---: | ---: | --- | --- |
| pdf.js 512 | issue7229.pdf | 52.5% | 51.9% | 0.0% | defect | 1.9 blank; both oracles render a full scanned form. Image not painted. |
| pdf.js 512 | issue13372.pdf | 46.9% | 46.8% | 0.0% | defect | 1.9 blank; oracles render a gradient photo. Image not painted. |
| pdf.js 512 | issue21346.pdf | 69.9% | 68.6% | 0.0% | defect | 1.9 near-white square; oracles light blue-gray. Color or transparency. |
| pdf.js 512 | annotation_hidden_print.pdf | 13.9% | 13.9% | 0.0% | defect | 1.9 paints an ink annotation carrying the Hidden flag; spec says do not display. |
| pdf.js 512 | widget_hidden_print.pdf | 3.0% | 3.0% | 0.0% | defect | Same Hidden-flag handling for a widget. |
| pdf.js 512 | issue12337.pdf | 7.8% | 9.0% | 1.7% | defect | Highlight annotation missing in 1.9; oracles show yellow multiply highlight. |
| pdf.js 512 | issue11931.pdf | 9.8% | 9.8% | 0.0% | defect | 1.9 magenta bar and green outline; oracles a gray half-disc. Composite or mask wrong. |
| pdf.js 512 | issue10339_reduced.pdf | 29.6% | 35.2% | 0.4% | defect | Color grid shades differ from both oracles. Color conversion. |
| pdf.js 512 | radial_gradients.pdf | 10.4% | 11.0% | 0.4% | defect | Disjoint and both-centres-outside radial cases fill the whole circle in 1.9; oracles draw the cone. Extend handling. |
| pdf.js 512 | jbig2_file_header.pdf | 41.0% | 41.0% | 0.0% | accepted (review) | 1.9 paints the JBIG2 image; both oracles blank. pdf.js test expects the embedded file header to be tolerated. Confirm against pdf.js reference. |
| pdf.js 512 | knockout_inner_backdrop.pdf | 36.1% | 36.1% | 0.0% | review | Knockout group; PDFium and poppler do not implement knockout, so their agreement is not evidence. Compare with pdf.js reference. |
| pdf.js 512 | knockout_nested.pdf, knockout_nested_group_alpha.pdf, knockout_isolated_overlap.pdf | 9% to 13% | | ~0% | review | Same knockout family. |
| pdf.js 512 | issue20232.pdf | 7.4% | 9.1% | 0.3% | minor | Dimension label glyph weight. |
| pdf.js 512 | annotation-square-circle-without-appearance.pdf | 6.8% | 6.8% | 0.0% | review | 1.9 likely synthesizes an appearance for annotations without AP streams (pdf.js does too); oracles draw nothing. |
| conformance 512 | VerticalText.pdf | 2.9% | 2.5% | 0.0% | accepted | 1.9 renders the vertical Japanese text; PDFium blank, poppler tofu. Both oracles lack the fallback font. |
| conformance 512/2048 | GWG173_JBIG2_compression_X4.pdf | 4.5% | 7.9% | 0.5% | accepted | Ghent 17.3 says no X must be visible; 1.9 shows none, oracles show a bold X. |
| conformance 512 | iep-as-published.pdf | 3.5% | 3.1% | 1.4% | minor | Text density; 1.9 slightly heavier than PDFium, poppler lightest. |
| conformance 512 | GWG220, GWG221 | 10% | 12% | 2% | accepted | Known: 1.9 avoids the error crosses PDFium paints (prior disposition in PERFORMANCE.md). |
| conformance 512 | CompactedPDFSyntaxMatrix.pdf | 18.6% | 18.0% | 4.8% | accepted | Known: source-defined 0.33/0.66 opacity; oracles paint opaque (prior disposition). |
| conformance 512 | Ghent overprint and spot patches (GWG010, 011, 020, 030, 031, 040, 041, 080, 081, 082, 120, 190 to 192) | 2% to 11% | | | review | 1.9 simulates overprint; PDFium and poppler mostly do not. Each needs its Ghent README reading. |

Remaining outlier and all-disagree rows in each set's `outliers.md` are not yet dispositioned.
