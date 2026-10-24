# Document state smoke checks

Prerequisites: Windows, the repository's .NET SDK, and a compiled KillerPDF 1.9 application.
Use only a test-owned application assembly. The runner suppresses normal application startup,
redirects settings and process temporary paths into its output folder, and creates synthetic PDFs.
It sends no operating-system input and does not attach to another application window.

Build the runner with the engine assembly from the application being checked:

```powershell
dotnet build tests/KillerPDF.DocumentStateSmoke/KillerPDF.DocumentStateSmoke.csproj -p:AppDirectory=C:/absolute/app-output -p:BaseIntermediateOutputPath=C:/Users/steve/scratch/document-state/obj/ -o C:/Users/steve/scratch/document-state/runner
```

Run one case at a time, with a fresh output directory for each invocation:

```powershell
dotnet C:/Users/steve/scratch/document-state/runner/KillerPDF.DocumentStateSmoke.dll C:/absolute/app-output/KillerPDF.dll C:/Users/steve/code/KillerPDF/App.xaml C:/Users/steve/scratch/document-state/redaction redaction --startup-smoke
```

Cases:

| Case | Public operation and assertion |
| --- | --- |
| `redaction` | Open different marked PDFs in both panes, focus the right pane, run the actual Redact macro UI, and verify SECRET disappears while KEPT-RIGHT survives. |
| `macro-source` | Verify the macro window contains current-document and batch choices with current selected, and capture its rendered state. |
| `macro-batch` | Select the batch option and verify Run opens its file picker directly. Capture the selected option. |
| `macro-rotation` | Rotate through the thumbnail context menu and run the actual Save-only macro. Reopen the output and require 90 degrees. |
| `merge-rotation` | Run the actual DataMerge macro with a synthetic CSV. Require the selected record value and the pending rotation in its output. |
| `extract-form` | Edit a real form textbox, use Extract and its picker, and require the pending value in the output. |
| `extract-rotation` | Repeat extraction after rotation and require both the pending form value and exactly 90 degrees. |
| `print-rotation` | Open actual print preview after rotation and require a landscape preview raster. |
| `print-switch` | Queue a document switch during real form-print preparation, require a visible canceled status, and reject any stale preview. |
| `merge-failure-pane` | Drop two unreadable owned PDFs through the actual routed event, choose Merge, switch panes while its worker is pending, and require the other pane's unsaved form value to survive failure. |
| `merge-close-success` | Merge valid owned inputs in pane B, close the split while the worker is pending, and require the completed document to have a visible owning pane while pane A retains its pending edit. |
| `radio-accessibility` | Open generated Alpha/Beta radio widgets, require distinct option names on their actual automation peers, select Beta, and verify mutual exclusion. |
| `radio-name-boundary` | Require /Alpha and Alpha export values to retain distinct names on actual rendered radio peers. |
| `keyboard-pane-owner` | Tab from pane A's document into pane B's document, route F6, and require only pane B to change view mode. |
| `reading-caret` | Select a collapsed range inside the actual document, require exact caret round trip, and reject leaking that position after opening another file. |
| `reading-keyboard` | Route WPF keyboard events through the actual window, require line, character and page caret movement, and preserve text-provider positions. |
| `reading-terminal-home` | Start at the final insertion point and require Home, Up and PageUp to reach the expected line or page. |
| `reading-scroll` | Navigate 35 lines in a zoomed tall page, require a visible reading range, and verify actual UIA ScrollIntoView reveals that range again. |
| `reading-scroll-spread` | Move from a settled spread to a cold spread and require the complete short target line in the public visible ranges after its right-hand tile is created. |
| `reading-scroll-wide` | Request the last glyph of a line wider than the zoomed viewport and require that caret to remain visible. |

Reflection is limited to isolated host setup, service initialization, constructing WPF's internal
drag-event arguments, and calling the public OpenFromExternal entry point on the dynamically loaded application. Tests do not invoke private
workflow methods or inject private editor collections. Workflow actions use actual WPF controls,
routed events, textboxes and dialogs. Assertions inspect generated PDFs or public UI properties.
The runner exits its own process after the verdict; a 45-second process deadline prevents hangs.

These checks establish synthetic application behavior. They do not prove physical mouse/keyboard
input, full locale/theme coverage, raster-redaction fallback, or hardware printing.
