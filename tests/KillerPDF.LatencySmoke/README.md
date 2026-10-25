# Interactive latency measurement

Black-box timing of the built application for S1 (cold launch to first paint),
scrolling, S3 (zoom to refined output) and per-document working set. The same
procedure runs against every version passed in, so the PDFium pipeline in
1.8 and the managed engine in 1.9 are measured identically. Neither application is
instrumented or modified.

Prerequisites: Windows, the SDK selected by global.json, an unlocked interactive desktop,
and no running KillerPDF process. The harness refuses to start while one exists; it never
closes a user session. It moves each launched window to 0,0 at 1600 by 1000, brings it to
the foreground and sends keys to it, so do not use the desktop while it runs.

```powershell
dotnet build tests/KillerPDF.LatencySmoke/KillerPDF.LatencySmoke.csproj --configuration Release
dotnet run --project tests/KillerPDF.LatencySmoke/KillerPDF.LatencySmoke.csproj --configuration Release --no-build -- `
  --version 1.8.80=C:\path\to\ref\KillerPDF.exe `
  --version 1.9=C:\path\to\candidate\KillerPDF.exe `
  --doc C:\corpus\article.pdf --doc C:\corpus\scan.pdf `
  --runs 5 --warmup 1 --out $env:USERPROFILE\scratch\kp-bench-render\latency-<date>
```

Each launch gets its own settings folder under the output directory through
`KILLERPDF_LAUNCHER_PATH`, so the user's real settings are untouched. The first launch of a
version seeds that folder with a plain open and close; every measured run then starts from a
copy of the seed, so the saved zoom or window state of one run never leaks into the next. Order alternates between versions on every run. Warmup runs are
recorded and flagged in results.csv, never discarded.

How events are detected: the window client area is captured every 33 ms and reduced to a
hash and variance over the central 20% of width and 40% of height. That region stays
inside the page for the five gate fixtures at the fixed window size, so a white page
edge against the dark surround cannot count as content. First paint is the first
captured frame with content there, and content must remain present at acceptance.
Painted, scrolled and zoomed are the time of the
last pixel change once no change has been seen for 700 ms. Scroll sends Page Down nineteen
times from page one; zoom resets with Ctrl+0, then uses UI Automation to select the
actual 200% preset in the measured process's ZoomBox and verifies its selection.
Timing includes opening, selecting and closing that dropdown. The earlier seven-key
procedure reached 205% and is diagnostic only for the strict 200% gate. Working set is
sampled every 20 ms for peak and final values.

Outputs: results.csv (every run), summary.md (mean and median per version and document,
warmups excluded), and frames/ with the stable frame after each event as diagnostic
evidence. These frames are not approved pixel baselines.

Limits: capture resolution is 33 ms. Frame stability cannot distinguish a slow final
refinement from an animation, so documents with animated content are unsuitable. The zoom
scenario assumes the default view mode. Hosted CI compiles this project and does not run it.

Measured scroll and zoom actions require a captured change before they can settle.
Returning Home or resetting to 100% may already be at its target, so those unmeasured
setup steps allow unchanged frames. A missed action must time out rather than
become a zero-latency result. On an unlocked disposable test desktop, run
`KillerPDF.LatencySmoke.exe --verify-capture-stability` to verify this with a real
test-owned static window. It sends no keyboard input and opens no PDF.

`--verify-content-frame FRAME.png blank` checks a retained blank capture through
the real content classifier; use `present` for a painted control. It performs no
desktop capture or input. This protects against the observed blank Altona page
passing the earlier wide-region variance check. Other document layouts require
their own content-region review before this heuristic can certify a paint event.

Scroll timing does not assert fit-width, final page index or final-quality pixels
for every intermediate page, so it cannot alone certify S2. Per-document working
set does not implement M3's five-document live-viewer scenario. Inspect captured
page content and zoom state before accepting S1 or S3 results; successful exit
alone does not prove the requested actions occurred.

A timed-out sample retains its error row and last captured frame (when a window
was captured), writes the partial summary, and stops the phase with exit 1.
Unstarted samples remain unmeasured. Inspect raw errors before using statistics;
the summary's means describe only completed valid measurements.

## Five-document live memory

Add `--live-viewer-memory` with exactly five distinct `--doc` inputs, version
labels and the normal warmup/run counts. Use an English, unlocked test desktop.
This separate M3 workflow keeps one measured application process open, forwards
the other four files through its ordinary single-instance entry, selects Fit
Width, and sends Page Down until UI Automation reports the last page of each
document. It records title, page count, key count and a settled final frame for
every document, then requires all five tab titles to remain visible.

Final working set and Windows peak working set are sampled after the five
documents, with no forced collection. JSON retains every document and sample;
CSV retains warmups, memory and errors. Inputs are hashed before and after.
Any missing title, incorrect page state, foreground loss or timeout invalidates
the phase. This traversal does not certify S2's final-quality output on every
intermediate page. Use the existing runner for S1/S3; this mode has its own
preflight and must not overlap other measurements.
