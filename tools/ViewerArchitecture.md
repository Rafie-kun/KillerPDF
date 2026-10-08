# Viewer rendering checks

The viewer presents a base raster asynchronously, then refines visible regions after zoom or scrolling settles. One raster worker per viewer prioritizes primary navigation, nearby pages, visible detail, and adjacent-page prefetch. Pending work is bounded to 32 requests and a 512 MiB estimate budget. Cached page bitmaps have a 160 MiB budget per tab. These are allocation controls, not a measured limit on total process memory.

Navigation, mode, tab, inversion, and document revision changes cancel work or reject stale results before presentation. Detail overlays preserve annotation coordinates and input. Region rendering uses the full page pixel grid, separates storage bounds from document clipping, and rounds spot composition once using exact integer arithmetic.

## Run checks

1. Build the app and run both test projects in Release. Serialize app builds with the family verification mutex when another task is building.
2. Build `tools/ViewerArchitectureSmoke/ViewerArchitectureSmoke.csproj` and `tools/RenderRegionCorpus/RenderRegionCorpus.csproj` in Release.
3. Freeze the built app directory under the external benchmark workspace. Use a new output directory for each run.
4. Run the real viewer probe. It uses synthetic PDFs and isolated settings in a hidden test window. It does not start the normal app or open user documents.

```powershell
& .\tools\ViewerArchitectureSmoke\bin\Release\net10.0-windows\ViewerArchitectureSmoke.exe APP_DLL OUTPUT_DIRECTORY SOURCE_DIRECTORY
```

The probe checks dispatcher response while raster work is pending, superseded navigation, revision rejection, annotation input during navigation, initial fit after raster completion, zoom, scrolling, spread fit, continuous refinement, and cancellation. It retains viewport captures and separated parse, raster, copy, and presentation timings. Those timings are diagnostics, not a controlled performance comparison.

5. Run exact full-raster versus region comparisons on the conformance and difficult corpora at 512 and 2048 pixels. `PAGES` is the maximum page count per input PDF.

```powershell
& .\tools\RenderRegionCorpus\bin\Release\net10.0-windows\RenderRegionCorpus.exe APP_DLL INPUT_DIRECTORY OUTPUT_DIRECTORY SIZE PAGES
```

The manifest records input and app hashes, full and region pixel hashes, differences, and failures. Region errors fail the check. Inputs that cannot produce a full raster are listed separately; compare that set against the reference run before accepting the result.

## Separate backend comparison

The comparison script invokes two existing batch-render executables in separate processes. It adds no viewer backend, native dependency, or authoring fallback. Supply the current custom-engine build as `ReferenceExe` and the released 1.8 PDFium executable as `ComparisonExe`.

```powershell
& .\tools\Compare-DisplayBackends.ps1 -ReferenceExe REFERENCE_EXE -ComparisonExe COMPARISON_EXE -InputDirectory INPUT_DIRECTORY -OutputDirectory OUTPUT_DIRECTORY -Size 2048 -Pages 3
```

Accuracy mode repeats each backend and requires stable pixels, page statuses, and diagnostics. The output preserves component hashes, CSV logs, PNGs, and cross-backend differences. Different output remains subject to source and visual review. It never selects or installs a backend automatically.

Use `-Measure` only with verified versions, matched publication settings, paused heavy activity, and no simultaneous build or corpus work. It performs warmups followed by balanced alternating runs and records process wall time and peak working set. Batch timing does not measure viewer interaction latency or establish visual parity.
