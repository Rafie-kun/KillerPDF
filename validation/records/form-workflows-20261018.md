# Desktop form, repair, macro and merge workflows

October 18, 2026. KillerPDF 1.9.0, source commit `b0ca181` on `dev/1.9-overkill`. Tested application SHA-256: `E27A31E6A59C678B97B0DD2A3850806A056F97D17A24F8986DDCA74ED6986337`.

## Host and scope

The isolated host constructed the actual MainWindow with complete application resources and its own window resources. A test Application subclass suppressed normal startup. Startup-smoke mode kept settings in memory, prevented session restore and update startup, and placed temporary files under the task directory. No installed app instance or user document was controlled.

The host invoked shell workflow entry points, drove their real modal controls, and clicked actual toolbar Undo and Redo buttons. Assertions reopened serialized snapshots or output PDFs through public engine readers. These are desktop integration checks using programmatic control activation, not native mouse/keyboard automation or hardware validation.

The first host attempt omitted the dispatcher synchronization context, causing the recognition continuation to run on a worker thread. Installing the normal WPF DispatcherSynchronizationContext corrected that fixture error. The final combined run completed successfully with no product defect identified.

## Verified cases

| Workflow | Verified result |
| --- | --- |
| Field recognition | Cancel preserved the source. Accepting a renamed text field excluded the rejected checkbox. Undo removed the new field and Redo restored its name. |
| Form flattening | Cancel retained the filled field. Flatten preserved the visible value Ada and removed editable widgets. Undo restored the field and its value; Redo flattened it again. |
| Editable XFA conversion | Cancel retained XFA. Conversion preserved Ada in an editable AcroForm field and removed XFA. Undo restored the original datasets; Redo restored the converted field. |
| Flattened XFA conversion | Removed XFA and interactive fields while retaining visible Ada. Undo restored XFA; Redo restored flattened output. |
| Language repair | Saved en-US, removed it on Undo and restored it on Redo. |
| Accessibility description | Saved a field tooltip, removed it on Undo and restored it on Redo. |
| Reading order | The real Move up control reversed two tagged children. Saved order, Undo and Redo matched the expected sequences. |
| Table headers | A reviewed data cell became a table header and cleared the corresponding finding. Undo restored the finding; Redo cleared it. |
| Ordered desktop macro | Actual output picker, confirmation and result report completed. Page labels ran before numbering: output contained Section-7. The existing filled field remained intact. Canceling the preview wrote no output. |
| Desktop data merge | Actual CSV picker, output picker, automatic field mapping, settings, two-file preview and report completed. Editable outputs contained Ada and Grace in form fields; flattened outputs retained those visible values without widgets. Canceling the output preview wrote neither file. The open source stayed unchanged. |

Recognition, merge settings, merge output preview and macro preview screenshots were visually inspected in Dark. Synthetic source files, snapshots, output PDFs and screenshots are retained in `C:\Users\steve\scratch\kp-bench-render\form-workflows-20261018`.

No product source changed during this pass. The existing 831-test application suite was already green for this exact source increment; this pass exercised additional real shell paths instead of repeating those unit tests.

## Remaining scope

- Native mouse and keyboard input, alternate themes and accents, native display scaling, assistive technology, tab switches during background work, and in-progress cancellation from the desktop still require coverage.
- The XFA fixture is a simple positioned template. Repeated template paths in the dynamic corpus example remain unsupported; this pass does not resolve that conversion gap.
- More complex merge mappings, image inputs, conditional records, collisions and failures have earlier automated coverage; this desktop pass used a two-record CSV and one matching field.
- General macro resize cannot reuse the current raster transform without flattening content. Imposition imports page content without copying annotations. A content-preserving resize needs coordinated page geometry, annotations, form widgets and destination behavior. Color conversion, data merge and rename also remain macro host work.
- Broad rendering and corpus regression review remains separate from these synthetic workflow checks.
