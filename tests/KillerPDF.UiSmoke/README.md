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

`--verify-pending-text-recovery` uses the isolated startup-smoke invocation to check
unsaved annotation text and existing page-text edits through recovery, saved PDF
contents and the reopened text provider. It sends no desktop input. The separate
`--verify-render-recovery` mode also injects filesystem permission failures and
belongs in the disposable lab; the pending-text mode does not run that injection.

## Certificate catalog

Use `--verify-certificates APP_DLL APP_XAML OUTPUT_DIRECTORY --startup-smoke`
with a fresh output directory. The isolated host imports a generated public CA
certificate, verifies search, untrusted import, trust removal and cancellation,
certificate removal and read-only Windows controls,
and renders Dark, Light, 98SE and all 20 locales at the minimum dialog size.
The result identifies the compiled assembly hash. Settings and imported
certificates stay in the supplied directory; Windows trust stores are read-only.
These WPF checks do not certify physical input, a screen reader, or signature
verification against a certificate already trusted by Windows.

## Viewer and headless bitmap parity

Use `--verify-viewer-parity APP_DLL MANIFEST_JSON NEW_OUTPUT_DIRECTORY --startup-smoke`.
The manifest has a corpus `Root`, `AppXaml` and `AppXamlSha256` identifying the
unchanged App.xaml from the tested application's frozen source snapshot, and
`Documents` entries with relative `Path`,
`Sha256` and descriptive `Category` fields. Inputs must remain under that root
and retain their hashes. Use an owned, unlocked test desktop and serialize this
check after timing runs because it creates offscreen WPF windows and renders PDFs.
The isolated smoke settings lock the quit choice and disable tray hiding so
closing test windows cannot wait on an unrelated quit-preference prompt.

The isolated host loads those verified resources and the compiled application window, opens each PDF through
the public window entry, selects single-page mode through its routed shortcut,
and reads the primary page image's bitmap. A separate process runs that same
application's headless CLI at the observed pixel size. Exact BGRA pixels and
dimensions must match; every input and result is retained in `result.json`.
Supply twenty representative documents for the release A4 matrix. A smaller
manifest is a diagnostic check. This verifies primary viewer bitmap delivery;
annotation/form overlays, zoom detail composition and physical display output
need their own checks. It does not establish cross-renderer accuracy.

## Document-window lifetime regression

Use an owned UI VM and a fresh output directory. Run
`--verify-window-lifecycle APP_DLL APP_XAML OUTPUT_DIRECTORY --startup-smoke`.
The isolated host opens two synthetic documents in actual MainWindows, waits for
the first window to finish closing, requires the application to designate the
survivor as its primary window, and uses the survivor's real Save menu and
file picker. The saved PDF must reopen as the survivor's one 300 by 200 point
page, and both input files must remain unchanged. Ordinary unsigned Save As may
add an incremental rotation update, so saved output is checked as a document
rather than requiring byte identity with its input.

The fixture locks only the optional clean-document quit confirmation in its
isolated settings. It does not bypass dirty-document prompts, launch normal app
startup, register a protocol, change installed settings or use personal files.
It verifies shared process/window lifetime, not the physical tab-drag gesture or
external-open IPC routing.

## Document-window session restoration

Use an owned UI VM and a fresh output directory. Run
`--verify-window-session APP_DLL APP_XAML OUTPUT_DIRECTORY --session-isolation`.
The host isolates portable settings and working PDFs before services initialize,
suppresses process startup and registration, and lets normal window restoration run.
It opens two synthetic PDFs in separate windows, closes them and constructs a new
window from saved settings. Both documents must return together, with the original
window's size and position and unchanged source files.

Use `--verify-window-session-secondary-first` for the reverse close order,
`--verify-window-session-manual-close` to verify a manually closed tab stays closed,
`--verify-window-session-forget` to check disabling remembered files restores an
empty window, or `--verify-window-session-close-all` to exercise the tab menu's
Close All action after a previous document window closes.
These modes require `--session-isolation`, not `--startup-smoke`,
because startup-smoke skips normal restoration. Single-document windows hide the
tab strip; the manual-close assertion checks the remaining filename and absence of
a visible strip. The fixture verifies window reconstruction in one isolated process,
not actual process restart, physical tab dragging or Windows sign-out.

## Update document-window state

The updater state regression uses
`--verify-update-window-state APP_DLL APP_XAML OUTPUT_DIRECTORY --session-isolation`.
It opens a synthetic PDF in a secondary document window, rotates it through the actual
thumbnail menu, and verifies that the primary window's existing update-host contract
detects those unsaved edits. The source bytes must remain unchanged. This is an isolated
WPF runtime check; it does not install an update or certify relaunch behavior.

## Saved-file update restart

On an owned UI VM, run `--verify-update-restart-stage APP_DLL APP_XAML OUTPUT_DIRECTORY SHARED_STATE --session-isolation`
with a fresh shared-state directory. The isolated host opens three saved synthetic PDFs across
two windows, closes a fourth document window, and disables remembered files. Preparation must
freeze editing while leaving native windows enabled. Canceling must restore editing and remove
the pending record. Committing must survive scope disposal, accept a separate process's native
close request, and close all captured windows despite tray mode without changing the preference.
The F11 check first requires normal shortcut transitions to work, then requires no transition
while the handoff is prepared. Each check waits through the fullscreen animation.

After that process exits successfully, run `--verify-update-restart-consume` with the same
arguments and shared state but a fresh output directory. The second isolated process calls the
same claim seam as eligible app startup before constructing its first window. Exactly the three
live files must return together, with the active file and original geometry retained. The closed
file stays excluded, source hashes and preferences remain unchanged, and a later window cannot
replay the handoff. Keep actual exit codes, bounded process receipts and both streams for each
phase. This does not run a real installer or normal startup registration.

Use `--verify-update-restart-early-exit` in place of the stage mode to exercise
application exit before updater activation returns. It requests native closure,
executes public application shutdown, and disposes the prepared handoff from the
Exit event before the caller can report a started updater. The same separate
consumer must still restore the saved session exactly once.

## Windows session-ending protection

Use an owned UI VM and a fresh output directory with
`--verify-session-ending APP_DLL APP_XAML OUTPUT_DIRECTORY --session-isolation`.
The isolated host exposes WPF's protected session-ending extension through a public
wrapper and supplies framework logoff arguments. It makes the secondary window
dirty through the real thumbnail Rotate menu, chooses Cancel in the existing save
prompt, and requires the cancellation flag, both windows, unsaved edits and source
bytes to survive. It does not send an actual Windows shutdown or logoff command.

`--verify-session-ending-save` chooses Save and reopens the synthetic output to
require its 90-degree rotation, a cleared dirty indicator and shutdown permission.
`--verify-session-ending-controls` edits two windows, cancels the later prompt after
the first approval, then checks both Don't Save choices permit closure despite tray
mode without another prompt or source changes. Keep each process under an external
watchdog and retain its exit code and streams. A result file alone does not replace
the process receipt. These checks exercise application lifecycle policy, not native
Windows message delivery or another application's shutdown cancellation.

## Print-only annotation visibility

On an owned UI VM, use `--verify-print-visibility APP_DLL APP_XAML OUTPUT_DIRECTORY --session-isolation`
with a fresh output directory. Independent positive and negative controls run before product assertions.
The isolated host opens a synthetic PDF through the real window entry, requires visible fields,
measurements and redaction marks, and rejects their print-only counterparts on screen. It clicks Print
and requires the annotation, measurement and redaction review ink at distinct preview locations.
It closes the preview without submitting a printer job and checks unchanged source bytes.
This covers native WPF integration, not physical input, printer output or normal startup/registration.

The Windows-only `--verify-print-color-probe` mode checks the same bitmap probes without opening
windows or loading the app. It rejects pale-blue edge pixels mistaken for cyan and independently
removes each required preview signal. Redaction checks use the review appearance's ink and position;
the annotation's IC entry describes its applied fill, which can have a different color.
