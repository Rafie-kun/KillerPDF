# Page group backdrop diagnosis, October 18, 2026

After repairing profile-cache concurrency, `transparency_group.pdf` remains
smooth but has a cyan ellipse where Poppler renders red. PDFium agrees with the
cyan result, so agreement alone cannot decide correctness.

The public minimal reproduction is `PdfPageGroupBackdropTests`: one red rectangle
painted with Difference blending inside an explicit DeviceRGB page group. Both
isolation settings produce cyan instead of red, while a transparent render of
the same document produces red. The two opaque-output regression cases fail in
247 ms, with evidence in `page-group-red` under the task scratch directory.

Ranked hypotheses:

1. Opaque output adds white before page content blends, rather than afterward.
2. The explicit group's isolation flag is mishandled.
3. Profile conversion changes the blend result.

The minimal reproduction has no profile, and both isolation settings fail in
the same way. The transparent-output probe changes only the output backdrop
and returns red. This isolates the early white backdrop as the cause.

[Adobe PDF Reference 1.6, section 7.3.6](https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/pdfreference1.6.pdf)
defines page content compositing on a transparent backdrop, followed by Normal
compositing onto the output medium. The proposed bounded repair applies this
order to explicit RGB and grayscale page groups. Ordinary pages without an
explicit group, native CMYK and separation previews retain their existing path.
The first attempt also isolated CMYK pages, exposing failures in existing
process/spot overprint checks. That broader change is excluded while its
ink-alpha interactions remain unresolved; no existing assertions were relaxed.

The original two cases pass after the repair. Expanded checks cover half-opacity
paint, retained transparent alpha, cropped output and untouched separation paper.
Their first fixture applied opacity and blend state separately, accidentally
resetting opacity. Combining both in one public graphics state corrects the
fixture, and all four cases pass in 242 ms. This fixture error is distinct from
the original red reproduction.

Task evidence root:
`C:\Users\steve\scratch\kp-bench-render\form-workflows-20261018`.
The standalone scratch probe is `page-group-backdrop-probe.json`; the retained
test case uses only public document construction, updates and rendering APIs.
The bounded repair passes all 6,568 engine tests. Debug and Release app builds
pass without warnings or errors; the full dependency rebuild also reported
660 existing CoreJ2K warnings. The rebuilt Release app hash is
`32A9375BCCC531B1A3C8757244B681431FFA0E65E6345A7708503BF0F84FC97E`;
its embedded engine matches the Release library hash
`E68B07722564F62518F36383CE39727534A481B2941D3AEF18DD28C37117A708`.
Debug engine hash:
`46516995EAA43835ABB27D8FE2E50ADE04D34C9C0D5ECC13F28AFAEBFB57F1D4`.
All 834 app tests pass. The actual-executable corpus comparison retains identical
statuses and source/app hashes. Eight conformance captures change at each size;
40 PDF.js captures change at 512 and 38 at 2048. All other captures are identical.
The original sample now has the red/magenta ellipse rather than cyan, with its
smooth gradient retained. The 512 result was visually inspected.

The first external wrapper stopped when Poppler left an empty PNG for
`bug1978317.pdf`, reporting a malformed annotation array with 32,768 entries.
The corrected wrapper records that unavailable oracle explicitly and completes
in the fresh `page-group-external-v2` directory. Three other sources initially
failed because their nested output filenames were too long for Poppler. The
`Retry-PageGroupPoppler.py` short-path retry recovers those six captures. Its
`page-group-poppler-path-retry/results-validated.json` is the final combined
94-case review. Both sizes of `bug1978317.pdf` remain unavailable in Poppler;
their current pixels differ from before by at most two channel values.

Ninety changed captures differ from before by at most three channel values.
Only `issue17215.pdf` and `transparency_group.pdf`, at each size, change more.
The 2048 before/after/oracle contact sheet was visually inspected:
`page-group-visual-review.png`. The annotation colors and ellipse now agree with
Poppler and the page-group compositing rule. Original-sample significant
differences against Poppler fall from 10.690% to 2.770% at 512, and from 10.518%
to 2.601% at 2048. PDFium retains the earlier conflicting result.

Of the 92 cases with both oracles, 25 remain above the strict threshold against
both, with no newly introduced strict outliers. Three cases have tiny increases
against both metrics from the small channel rounding changes; the largest
smaller-oracle increase is 0.00108 percentage points. These results do not pass
the broader release parity gates. The full A1/A2 review, native CMYK page-group
isolation, and the original sample's remaining color-profile discrepancy stay
open. `change-analysis.json` retains every before/after channel bound.
