# Profile color parallelism diagnosis, October 18, 2026

The actual 2048-pixel render of PDF.js `transparency_group.pdf` has visible
stippling absent from both PDFium and Poppler. Source SHA-256:
`029DD50635ADEE0711836095B76AAB62226CA8731F6642A9454B25E2668226DD`.
It contains smooth axial shadings, an RGB ICC page group and a non-isolated Form
using Difference blending. The paint contains no image that explains the noise.

Ranked hypotheses:

1. Shared last-color caches return another row worker's color. Surface read and
   write caches update their key, value and validity separately, without a lock.
2. ICC evaluation itself has mutable state shared between workers.
3. Group backdrop removal or alpha rounding produces a stable numeric artifact.
4. The source deliberately contains a noisy image or discontinuous shading.

An actual executable run with `DOTNET_PROCESSOR_COUNT=1` removes the stippling;
`transparency-serial` and its CSV retain that result. Both external renders are
smooth, and source inspection rules out a noisy image. The public regression seam
is `PdfPageRenderer.Render` with `MaximumParallelism` one versus four and finished
bitmap caching disabled. A synthetic calibrated RGB page with a non-isolated
gradient group removes the external ICC profile while retaining profile conversion
and row concurrency. No private cache fields are asserted.

The calibrated regression fails before the fix in four seconds, with no rendering
diagnostics: expected pixel bytes 74/37/70, actual 58/10/98 at byte offset 196.
This removes ICC parsing as a necessary cause. The proposed RGB repair keeps
conversion computation outside a small lock, publishes cache key/value/validity
together, and returns the local computed result instead of a shared mutable field.
The rebuilt RGB regression passes across three uncached parallel renders. The
test was then expanded with a real synthetic CMYK ICC profile; that case also
passes without a CMYK implementation change. The two-case public check passes.
The fix remains scoped to the observed RGB cache race. Tested Debug engine hash:
`6B1B72701E436DA4CCF7D81A0DE4AD0D697ADD6BC18C795081316D788DB6B160`.
The full engine suite passes 6,564 tests. Debug and Release engine builds pass
without warnings or errors. The actual rebuilt Release executable now reproduces
the original file with zero pixel differences from the retained pre-fix serial
capture at 2048. Its embedded engine hash matches the newly built library:
`C4CE0B660516E94C9CEDC92CC7B7495C6D9E7275FC495B39252D7140043D0BE8`.
Release application hash:
`D186E3B0872550A426C89236F11AC5D481D83267E33B25365A667144D9EAE0F4`.
The complete actual-executable comparison passes with zero serial/parallel pixel
or status differences: 634 conformance and 1,100 PDF.js page captures at each of
512 and 2048 pixels, for each worker configuration. All source and application
hashes remain unchanged throughout the run. Compared with the previous stencil
captures, only `issue19360.pdf` and `transparency_group.pdf` at 2048 change; all
other pixels and every status stay identical. The ordinary app suite passes
834 tests, and the Debug app build passes without warnings or errors.

Evidence is retained under
`C:\Users\steve\scratch\kp-bench-render\form-workflows-20261018`:
`profile-parallel-red-runtime`, `profile-parallel-pixels/results.json`, its CSVs
and captures, `profile-parallel-app-final`, and `transparency-fixed-parallel`.
`Check-ProfileParallelPixels.py` records the reproducible corpus command and
identity checks. These are internal correctness checks, not speed benchmarks
or a claim that the remaining release parity gates pass.
