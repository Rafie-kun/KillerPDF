# Interactive PDF smoke checks

Prerequisites: Windows with .NET SDK selected by the repository's global.json, an unlocked
interactive desktop, and no running KillerPDF user session. Build commands write only build
output and use the official NuGet registry. Run one repository at a time.

From this checkout:

```powershell
dotnet build KillerPDF.csproj --configuration Release
dotnet build tests/KillerPDF.UiSmoke/KillerPDF.UiSmoke.csproj --configuration Release
dotnet run --project tests/KillerPDF.UiSmoke/KillerPDF.UiSmoke.csproj --configuration Release --no-build -- --verify-fixture bin/Release/net10.0-windows/KillerPDF.dll "$env:TEMP/pdf-fixture-check"
dotnet run --project tests/KillerPDF.UiSmoke/KillerPDF.UiSmoke.csproj --configuration Release --no-build -- --interactive bin/Release/net10.0-windows/KillerPDF.dll "$env:TEMP/pdf-ui-smoke"
```

FlaUI.UIA3 5.0.0 is confined to this test project; the app already excludes tests/**. The console
runner returns 1 on unavailable desktop, timeout, assertion failure, or host failure, and returns
0 only after all requested checks pass. Hosted PR CI runs only --verify-fixture, with its evidence
retained under ci-results/fixture; it does not run this interactive suite.

The isolated WPF host loads the actual compiled app's StampWindow, ColorPickerDialog, theme
loader, PDF renderer, and working-document services. It creates its own one-page PDF fixture and
portable settings directory. It does not launch App.OnStartup, register a protocol, open a user
document, or interact with another app. Only the host process created by a case is terminated.

Dark, Light, and SE98 cases accept two stamp colors and cancel a subsequent edit, then assert the
committed StampSpec preserves the accepted values. UI Automation invokes real buttons and sets
the hex field through its value pattern. RenderTargetBitmap captures only test-owned WPF content,
including the full stamp window. These are retained diagnostic images, not approved pixel baselines.
The save/reopen case uses the actual PdfWorkingDocument service with an unchanged synthetic PDF.
It checks page count and exact saved bytes. It does not certify the main app's Save As picker,
serialization of applied stamps, window startup, other themes, keyboard focus, or a screen reader.

UIA calls have bounded connection/transaction timeouts. A discovery timeout retains the test-owned
process tree and its AutomationIds; stamp-managed.txt records the actual stamp HWND and peer ID.
Every theme's own child host is closed on success or failure. These diagnostics do not alter the
StampDialog discovery assertion or the accepted/canceled color expectations.

The fixture-only command parses, renders, saves, and reopens without showing a window. It is useful
when an interactive desktop is unavailable; it does not count as a FlaUI pass. The existing
build/Test-ColorPickerDialog.ps1 covers all 45 theme/accent resource states and brush shapes in its
compatible PowerShell runtime. Pass `-Configuration Release` for the same assemblies used above.
Keep that check as separate evidence. Maintenance uses its actual app PDFium renderer;
development uses the Overkill engine renderer. This tooling preserves that architecture difference.

## Field preset selection regression

From the development checkout, after building the app and this test project:

```powershell
dotnet tests/KillerPDF.UiSmoke/bin/Debug/net10.0-windows/KillerPDF.UiSmoke.dll --verify-presets bin/Debug/net10.0-windows/KillerPDF.dll App.xaml C:/Users/steve/scratch/kp-bench-render/preset-selection-check --startup-smoke
```

Use a fresh evidence directory. This mode loads the actual window resources and
opens the populated Field Presets dialog in all 13 default themes. It changes
selection and checks both rows' name and detail colors against the selected and
unselected semantic brushes. It retains PNGs and a result with the assembly hash.
Normal app startup is suppressed, preferences remain in memory, and files stay
under the supplied evidence directory or the process's temporary directory. It
uses WPF controls directly and does not certify physical input or a screen reader.

## Help overflow regression

Use `--verify-scroll-overlays APP_DLL APP_XAML OUTPUT_DIRECTORY --startup-smoke`
to measure and render the compiled Help card in Dark, Light, and 98SE. The check
covers short and normal heights, both scroll ends, fitting content, the keyboard
view, and separation of the scrollbar from Close. App startup is suppressed and
the output folder owns all test settings. No desktop input is sent.

## Toolbar and display layout regression

Use `--verify-toolbar APP_DLL APP_XAML OUTPUT_DIRECTORY --startup-smoke`
to render the toolbar at 1000, 1320, and 1600 pixels with captions beside,
under, and hidden. The check rejects unused space that can fit an overflow
tool, verifies the footer display controls sit between view modes and zoom,
and renders Color Correction to check its right inset and preview zoom.
Exposure, saturation, warmth and tint must change the preview and reset to neutral.
An exposure adjustment is applied, saved and reopened to verify the PDF output.
It also checks document and empty-pane transitions, rail right-click routing,
and opening saved copies in an empty second pane or a new tab.
Use a fresh output directory. Startup and user settings remain isolated;
the check sends no desktop input.

## Table extraction and export

After building the app and this test project, use
`--verify-table-export APP_DLL APP_XAML OUTPUT_DIRECTORY --startup-smoke`.
Use a fresh output directory. The isolated host renders the actual table dialog
in all 13 themes at 100% and 150% app scaling, checks a 480x360 window at 250%,
and checks all 20 locales at 640x480 and 150%. It verifies invalid selections,
editable cells, and access to the scrolling content and save controls.

The workflow cases open an owned synthetic PDF through the public window entry,
invoke the rendered Save menu, select a table area, edit a cell, and save through
the real file picker. They inspect XLSX and CSV output, cancel both review and
save, switch the source during review, and try to overwrite a PDF fixture named
with a CSV extension. Original bytes must survive each case.

The captures and results identify the compiled app hash. These WPF control
checks do not certify physical mouse dragging, native display scaling, screen
readers, or interoperability with Excel and LibreOffice. Keep those checks
separate from the public engine tests and the existing release gates.

The same isolated invocation accepts `--verify-office-export`, `--verify-sidebar-actions`,
`--verify-grain-themes` and `--verify-dialog-text` instead of `--verify-table-export`.
Office checks include editable and page-image DOCX/PPTX saves, source-change cancellation,
and command-line layout selection. Sidebar checks cover cursor menus, measurements, signing,
the F1 list and command dispatch through Search Tools. Grain checks seal a WPF style before
switching through every theme. Dialog text checks render 12 prompt variants across all 13 themes,
including multiline warnings, errors, choices, confirmations, password and text entry.

`--verify-paragraph-edit` uses the same isolated invocation. It checks paragraph
selection, resizing, font size, alignment, line spacing, overflow, preview
invalidation, cancel, Undo/Redo and save/reopen through the actual dialog and
main-window command. Rendered checks cover all themes, locales and high app scale.
