# Headless 1.9 accuracy remediation, October 19, 2026

This continuation targets rendering accuracy only. No UI interaction, performance measurement, baseline replacement, push or release is authorized by these results. Concurrent source changes remain intact.

## Evidence and candidate separation

Working evidence is retained under `C:/Users/steve/scratch/kp-bench-render/accuracy-remediation-20261019/`. The initial application SHA-256 is `64887713EA7CC8F59D3E219CA84254F6907B986D43B71F0E115402D7EE637525`, including the earlier validated page-tree recovery classification. Product fixes developed during this continuation require a separate integrated candidate and verification.

Three independent workers own strict full-corpus oracle coverage, native CMYK page-group diagnosis, and non-color PDF.js diagnosis. Source fixes require a real public rendering reproduction, ranked hypotheses, red-green tests and original-input verification. Existing negative controls remain required; a changed expected value requires independent evidence that its old model was wrong, not a weaker assertion. Unknown or omitted content is not collectively waived.

## Difficult-set evidence

The retained 41-file input set was freshly rendered at 2048 with up to three pages per file and one document worker. All 76 expected page rows are present: 74 OK, no skips and two publication failures. The frozen PDFium and Poppler references both produced all 76 images. Input and candidate payload manifests match after rendering.

The final A04 refresh repeats all 76 rows against the same frozen references, verifying input, complete payload and reference hashes. It retains 74 OK pages and the same two failures, with zero status changes, zero new strict outliers and zero resolved strict outliers. Two published rows change decoded pixels. The process exit code is 1 because the two guarded failures remain. Evidence is `difficult/final-a04/summary.json` and `difficult/final-a04/strict-ledger.json`.

`difficult/strict-ledger.json` records every page, raw publication status, image paths, 256-pixel thumbnail comparisons and full-resolution comparisons. All 74 published pages have compatible images from both oracles. Forty-four exceed the conservative full-resolution threshold against both. This is a review queue, not 44 confirmed defects or an A3 pass. Thirty-one of those have thumbnail differences below the threshold against both oracles and need rasterization/alignment review before any correctness finding.

Visual inspection of the original Altona technical captures confirms visible overprint/color differences remain. AIAA page 2 retains the same visible text, equations and layout in the inspected candidate and Poppler images, while its full-resolution metric remains above threshold. Candidate width is 1447 pixels and Poppler width is 1448 at the same 2048 height. The recorded alignment rule resizes only within two pixels; this convention can amplify text-edge differences. No score or acceptance threshold was changed to dismiss them. All page-level visual dispositions remain pending.

## Malformed-source investigation

`064034.pdf` page 3 fails publication with XML/XMP tokens reported as unsupported drawing operators. An independent reader confirms the page's `/Contents` resolves to object 6, a stream declaring `/Type /Metadata` and `/Subtype /XML`. Its decoded prefix is an XMP packet, not page drawing instructions. Both external renderers produce an entirely white image for this page. Source SHA-256 is `459584BA2FBB713A4E9BE0CE2D6A50956EC67313C8E724A6600AEFBB9130D6F7`.

[Adobe PDF Reference 1.7, section 10.2.2](https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/pdfreference1.7old.pdf) defines metadata streams as XML metadata. The source object inspection and `difficult/064034-source-investigation.json` explain this rejection. The publication guard remains intact; no parser change or baseline exclusion was made. This is a documented malformed input, not a claimed renderer repair.

The second difficult-set rejection, `AIAA-2005-6173-712.pdf` page 1, references `/F3` through the missing object `19 0 R`. An independent reader resolves that resource to null and confirms the page uses `/F3` in a `Tf` instruction. Source SHA-256 is `4BB86B4F80134C764DA1BCAC9FA43F24D01EF783D511EE90AEA494B0C4D38812`. Evidence is `difficult/AIAA-source-investigation.json`. The replacement-font diagnostic and publication rejection remain intact; no font content was invented.

## Verified standard-font repair

Commit `69ddd09` fixes false publication failures for undefined blank control slots in nonembedded standard Type 1 fonts. It preserves explicit character mappings, embedded glyphs, unknown font families and visible Unicode fallback failures. Nine new tests and four existing font negatives pass. Actual `standard_fonts.pdf` CLI rendering now produces three OK pages at both required sizes.

Matched public-engine before/after probes retain identical pixels on all six page/size cases while eliminating the false diagnostics. Those probes use 511/2047-pixel heights for the nominal 512/2048 cases; the actual CLI produces 512/2048. A cross-grid hash assertion was rejected as a fixture mismatch. No actual-CLI before/after pixel-equality claim is made from those different dimensions. Exact dimensions and CLI output hashes are in `integrated/font-cli-verification.json`; detailed font/source investigation is in `pdfjs/LEDGER.md`.

The same worker found `issue18042.pdf` stores only the four bytes ASCII `1234` as its DCT image payload. Poppler also rejects it. No JPEG decoder repair or blanket failure waiver was justified.

## Verified native CMYK repair

The minimized public renderer proves that the first cyan Difference paint in a native CMYK page group incorrectly sees opaque paper. The bounded candidate starts unprofiled DeviceCMYK groups transparently and adds paper after output conversion. It preserves the first fully opaque named spot's identity and excludes ICCBased, DefaultCMYK and profile-bound groups. The wider trial that broke ICC tests is rejected.

Eight new public-render cases cover both isolation flags, native resource aliases, full/half opacity, transparency, region equivalence and separation coverage. Source-backed Poppler controls establish the intended first-paint result. Fourteen matched real captures show twelve identical outputs and two changed sizes of the Risk-Based Management paper; no preexisting strict outlier is claimed resolved.

A full combined engine run exposed three failures (6,666 passed, three failed): a 30-second form-expansion deadline, a full-cover alpha-plane allocation and an older half-opacity expected value. One focused deadline replay passed, which did not override the failed full run. The allocation repair retains the existing memory bound. Independent pinned Poppler rendering supports replacing the old ink-fading expectation with a strict output-color expectation. The final 984 focused cases and the complete serial engine suite pass: 6,669 passed, zero failures or skips, with no exclusions or timeout changes. All 405 production manifest entries match the integrated candidate before and after. Commit `1d48e0f` contains the bounded repair. TRX evidence is `cmyk/serial-results-final/engine-final-serial.trx`, SHA-256 `15674D37A55B92C9900F1B9C21BCED0A1C80B50371A25E822E02C47B9728732B`. Detailed rejected trials, hypotheses and oracle evidence remain in `cmyk/CMYK-FINDINGS.md`.

An intermediate combined app, SHA-256 `EE2BF16ECD35781FC34F99C0702B8262ADF649725987D8023813D3046065F5DA`, built successfully with 671 warnings and zero errors; all 904 app tests passed. It predates the subsequent alpha-allocation and narrow form-appearance changes and is not the final candidate. The warning count is retained rather than described as a warning-free build.

## Retained overprint differences

Supplemental captures with the pinned Poppler executable and its `-overprint` option reduce Altona technical's significant differences from 31.9720% to 27.5011%, and GWG010 from 7.7623% to 4.5276%. Both remain strict outliers. Source labels identify overprint mode controls, and rendered inspection shows that configuration explains some missing or added marks. The default oracle captures and acceptance thresholds remain unchanged. Fractional named-spot identity also retains an existing overprint disagreement. Evidence is in `cmyk/overprint-diagnostic` and `cmyk/pinned-synthetic`; these are bounded diagnoses, not accepted dispositions for the whole pages.

## Verified plain-text form publication

Commit `d39a18a` publishes only the supported plain-text appearance regeneration path. Eligibility requires a resolved font, fixed black text, printable ASCII, fitting width and font-metric height, and no saved appearance, rich text, special flags, alignment or border styling. Complex and unknown appearances remain blocked. Seventeen public-session checks pass, including an explicit appearance pixel comparison and sixteen negative controls.

The original `annotation-tx.pdf` now publishes at both sizes. Its source requests appearance regeneration and provides a resolved Helvetica resource with a plain text value. Detailed source and negative-control evidence is retained in `pdfjs/forms/LEDGER.md`.

## Integrated candidate verification

The frozen accuracy candidate is `integrated-final/app`, application DLL SHA-256 `A04A9C3524D72BD55BCA1F22E2476FA51FDCF72B37914AA8C519C4A28AB7160A`. Its source and complete payload manifests are retained. The build has 671 warnings and zero errors, and all 921 app tests pass with no skips. Eight actual CLI scenarios pass across 512 and 2048: standard fonts publish three pages, the supported form publishes one page, and both a corrupt JPEG and an unsupported empty form remain rejected without replacing existing output. The final font CLI pixels exactly match the intermediate fixed-font CLI captures at equal dimensions. The complete serial engine suite passes all 6,669 tests. Broad-corpus refresh and final identity verification are complete. Subsequent independent issue work does not change this frozen payload or inherit its full-corpus evidence automatically.

## Strict broad-corpus coverage

The fresh runner uses stable short input IDs, explicit page mappings and pinned complete payload manifests. It retains all 649 conformance and 976 PDF.js inputs, accepted page selections, both required sizes, guard failures, skips and unavailable oracle outcomes. It scores every shared page at full resolution and also records thumbnail scores. Oracle agreement never waives a candidate that exceeds the threshold against both.

All four groups and final integrity verification are complete for the initial 648877 candidate, covering 3,532 union page rows. Final assignment evidence is under `oracles/coverage-648877-final`:

| Set | Agrees with an oracle | Conservative full-resolution flags | Unavailable oracle | Candidate missing |
| --- | --- | --- | --- | --- |
| Conformance 512 | 344 | 222 | 39 | 44 |
| PDF.js 512 | 781 | 243 | 15 | 78 |
| Conformance 2048 | 403 | 163 | 39 | 44 |
| PDF.js 2048 | 903 | 122 | 14 | 78 |

Conformance has 649 rows, with 29 failures and 15 skips. PDF.js has 1117 union rows: its raw initial candidate CSV has 61 failures and 16 skips, while one additional missing page is a failed PDFium/Poppler attempt for `Pages-tree-refs.pdf` page 2, not a successfully rendered candidate page. These are initial-candidate results, not counts of confirmed bugs. Detailed source hashes, page IDs and images remain in each group's `ledger.json`. The separate A04 refresh uses these frozen references; exact A1 baseline reconciliation is recorded separately.

The comparison helper computes channel differences after RGBA decoding. A complete read-only alpha audit covers all 10,079 original PNG files: all 10,043 readable captures have alpha exactly 255, so their RGBA and RGB significant-pixel scores are identical. Thirty-six unreadable partial Poppler PNGs remain unavailable coverage and were not retried or waived. Evidence is `oracles/alpha-audit.json`. This audit does not replace A1's exact decoded-RGBA comparison.

## Completed fixed-candidate refresh

A04 retains all conformance counts at both sizes. Five PDF.js pages now publish at each size: `annotation-tx.pdf` page 1, `issue18059.pdf` page 1 and all three `standard_fonts.pdf` pages. Four agree with at least one oracle at 512. Standard-font page 2 is a newly scored strict difference there; all five agree at 2048. No previously scored agreement becomes strict, and there are zero new FAIL outcomes. A newly available image exposes an open difference; it is not evidence that this patch introduced the underlying pixel discrepancy.

| Set | Agrees with an oracle | Raw strict flags | Unavailable oracle | Candidate missing |
| --- | --- | --- | --- | --- |
| Conformance 512 | 344 | 222 | 39 | 44 |
| PDF.js 512 | 785 | 244 | 15 | 73 |
| Conformance 2048 | 403 | 163 | 39 | 44 |
| PDF.js 2048 | 908 | 122 | 14 | 73 |

Final evidence is under `oracles/run-A04-refresh` and `oracles/coverage-A04-final`. Each PDF.js raw CSV has 1,044 OK, 56 FAIL and 16 SKIP rows. The extra invalid oracle page makes 57 rejected union rows, not an extra successful page. One detail-only PDF.js change occurs per size. All 3,298 new candidate images decode successfully with alpha exactly 255 (`oracles/alpha-A04-audit.json`), completing RGB/RGBA strict-score equivalence alongside the frozen-reference audit. All final payload, input and oracle identity checks pass. This completes coverage and comparison, not A2 acceptance of the remaining raw strict cases.

## Source-backed accepted differences

Coordinator review accepts the following exact-source, page-one differences at 512 and 2048 under A2's source-backed disposition rule. All retain their raw strict flags and original denominator entries. This accepts six specific page/size rows, not codecs or transparency generally, and changes no scoring threshold or reference capture.

`jbig2_file_header.pdf`, SHA-256 `5EDB6045FC86EE5FA8E837685A35A57014764D1F72518E136E5E8F13FCF92C8D`, embeds a standalone JBIG2 header in its image stream. Removing only that 13-byte header makes both pinned reference renderers match A04's original decoded RGB pixels exactly at both sizes. A04's original output also exactly matches the initial candidate. The source MD5 matches the exact regression fixture added by [PDF.js PR 21388](https://github.com/mozilla/pdf.js/pull/21388/files), whose implementation explicitly recovers these headers. The original references remain blank and retain 41.040039% significant differences. This is an accepted compatibility difference, not an engine repair. Evidence: `jbig2-diagnosis/FINDINGS.md`, `results.json` and `two-size-verification.json`.

`jpx_smaskindata.pdf`, SHA-256 `A82813559C74F074B0CAF92C80875FC018ACB737269A2AF5508C7E083B2A5FD3`, uses embedded JPX alpha with preblended matte colors over gray. Public frozen-engine decoded bytes exactly match independent Pillow/OpenJPEG samples. Native public rendering and actual CLI output at both sizes match all eight independently derived interior colors, with no diagnostics. [Adobe PDF Reference 1.7](https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/pdfreference1.7old.pdf), printed pages 342 and 554, defines the preblend behavior; pinned [PDF.js handling](https://github.com/mozilla/pdf.js/blob/22b0b86441f35f984271057483033283cff30b0e/src/core/image.js#L187) independently implements matte reversal. The references disagree with these samples and each other. A [PDFium July 2026 test commit](https://pdfium.googlesource.com/pdfium/+/b18d018b2f9aa2913cc435b5ccdd59d0a5856ad8%5E!/) acknowledges incorrect mode-two expectations at that revision, corroborating a reference limitation without making a claim about every current PDFium build. This is an accepted exact-file reference difference. Evidence: `jpx-diagnosis/README.md`, public decoder/render probe and `verify-pixels.py`. It is not generic JPX conformance proof or a guarantee about other interpolation boundaries.

`knockout_inner_backdrop.pdf`, SHA-256 `BE0B806E63AD097945AA372D286784C8AC152102DC268780AE94DBE89A78E23C`, nests a nonisolated Multiply group inside an isolated knockout group. [Adobe PDF Reference 1.6](https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/pdfreference1.6.pdf), section 7.3.5, printed page 510, explicitly assigns that inner group the outer group's initial backdrop. Here it is transparent. Literal color composition therefore produces red and green lobes with black overlap, matching A04. Both frozen references instead produce a black union as if the preceding blue sibling were the initial backdrop. Three one-variable controls (disable outer knockout, isolate the inner group, use Normal blending) isolate this behavior; all three renderers agree on their interior colors at both sizes. A04 also matches the initial candidate exactly. Raw differences of about 34.6016% remain recorded. This accepts the exact-source reference limitation and preserves the specification's behavior. Evidence: `knockout-diagnosis/FINDINGS.md`, `results.json`, `identities.json` and the literal-color `verify.py` checks. No renderer patch is justified by this case.

## Open standard-font outline difference

`standard_fonts.pdf` page 2 remains a strict 512 case: significant fractions are 2.388% against PDFium and 12.476% against Poppler. At 2048 the PDFium fraction is 1.036%, below threshold. PDFium differences are entirely in text, with none near the table grid. Poppler also differs in grid strokes and requires one-pixel width alignment. All visible cell mappings remain consistent; no missing or wrongly mapped glyph was identified.

A frozen public-renderer experiment resolving the exact Windows Courier New Bold font reproduces the actual 2048 CLI bitmap byte for byte. Switching only to packaged fonts worsens the PDFium comparison, so a blanket font-source change is not justified. Substitute outlines and glyph-mask rasterization remain to be separated independently. This row is not accepted or waived. Evidence, font hashes and a proposed single-glyph Courier-Bold K diagnostic are retained in `standard-font-outlier/README.md`.

## Completed exact A1 reconciliation

`oracles/A04-reconciliation/completed.json` confirms the single final comparison against accepted application `21B1145BD7078C23B693E57D3DF123B5EF2E0F3D2414549885F3BEBF06BF0608`. Source manifests, frozen payloads and retained capture hashes are verified. Comparisons use dimensions plus exact decoded RGBA, with no alignment or threshold allowance.

| Set | Accepted/current images | Changed pixel keys | Status changes | Detail-only changes |
| --- | --- | --- | --- | --- |
| Conformance 512 | 634/605 | 584 | 29 | 0 |
| PDF.js 512 | 1098/1044 | 784 | 58 | 18 |
| Conformance 2048 | 634/605 | 585 | 29 | 0 |
| PDF.js 2048 | 1098/1044 | 783 | 58 | 18 |

Changed pixel keys include missing and added images. Conformance has 29 removed images and 555/556 changed shared images. PDF.js has 56 removed, two added and 726/725 changed shared images. Per size, conformance has 29 OK-to-FAIL transitions; PDF.js has 56 OK-to-FAIL, one SKIP-to-OK and one FAIL-to-OK. These are differences from the historical accepted baseline, not new failures from this repair batch. A1 fails parity and remains release blocking. The current payload is not substituted for the accepted baseline.

The separate comparison against the initial 648877 candidate records zero new failures, zero previously agreeing pages becoming strict, zero resolved existing strict cases and zero lost scoring. The sole new strict row was previously unpublished standard-font page 2 at 512. All five restored page publications at each size remain explicitly listed in `versus-648877.json`.

## Remaining work

Continue source-backed dispositions for the remaining A1 pixel/status changes, A2 strict or unavailable cases and A3 visual review. The full corpus and difficult-set captures are complete for this pinned batch. The open standard-font outline/rasterization seam, profile/overprint color cases and remaining source-guard failures need further focused work. A4 viewer/headless equivalence requires UI verification and is outside this headless continuation. Passing engine/app tests, completed coverage and the six accepted exact-case differences do not establish release readiness.
