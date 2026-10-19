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
