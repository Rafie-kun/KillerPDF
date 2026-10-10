# Interactive latency measurement

Black-box timing of the built application for release gates S1 (cold launch to first
paint), S2 (scroll to final quality), S3 (zoom to refined output) and M3 (live working
set). The same procedure runs against every version passed in, so the PDFium pipeline in
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
  --runs 5 --warmup 1 --out $env:USERPROFILE\kp-bench-render\latency-<date>
```

Each launch gets its own settings folder under the output directory through
`KILLERPDF_LAUNCHER_PATH`, so the user's real settings are untouched. The first launch of a
version seeds that folder with a plain open and close; every measured run then starts from a
copy of the seed, so the saved zoom or window state of one run never leaks into the next. Order alternates between versions on every run. Warmup runs are
recorded and flagged in results.csv, never discarded.

How events are detected: the window client area is captured every 33 ms and reduced to a
hash and a central-region variance. First paint is the first frame whose center has page
content and differs from the empty shell. Painted, scrolled and zoomed are the time of the
last pixel change once no change has been seen for 700 ms. Scroll sends Page Down nineteen
times from page one; zoom sends Ctrl+0 then Ctrl+Plus seven times (seven 0.15 steps from
100% reach 205%). Both key sets are handled identically by 1.8 and 1.9. Working set is
sampled every 20 ms for peak and final values.

Outputs: results.csv (every run), summary.md (mean and median per version and document,
warmups excluded), and frames/ with the stable frame after each event as diagnostic
evidence. These frames are not approved pixel baselines.

Limits: capture resolution is 33 ms. Frame stability cannot distinguish a slow final
refinement from an animation, so documents with animated content are unsuitable. The zoom
scenario assumes the default view mode. Hosted CI compiles this project and does not run it.
