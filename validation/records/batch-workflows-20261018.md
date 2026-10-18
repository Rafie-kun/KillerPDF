# Batch safety and production workflows, October 18, 2026

Scope: KillerPDF 1.9 on `dev/1.9-overkill`, following the earlier
[desktop workflow pass](desktop-workflows-20261018.md). No product defect was
reproduced by this pass. Four permanent app regression cases were added.

## Batch safety

`PdfBatchSafetyTests` exercises the desktop data-merge workflow boundary:

- An existing output without overwrite approval is unchanged. The next record
  still writes successfully, and the failed write leaves no temporary file.
- An approved output held open exclusively is unchanged after replacement fails.
  The next record still writes successfully, with no temporary file left behind.
- A missing image fails its record without changing an existing destination.
  A later record completes with its expected field value.
- Cancellation from the image boundary stops later records, preserves the existing
  destination and leaves no temporary files or reported successful output.

These use actual filesystem behavior, form generation and output reopening.
Image loading is the injected system boundary. They do not drive the desktop
Cancel button or simulate process termination during an operating-system rename.

The existing engine macro, Bates and imposition suites pass 38 tests, including
operation order, failed-input isolation, cancellation and resuming canceled work.
An additional corpus-based macro run processes a valid four-page PDF, an invalid
input, then the valid PDF again: success, failure, success. Both successful outputs
retain four pages. Cancellation in a host operation reports no successful output.

## Real PDF outputs

Source: `py-pdf-sample-files/004-pdflatex-4-pages/pdflatex-4-pages.pdf`.

| Layout | Settings | Reopened and rendered pages |
| --- | --- | --- |
| Repeat | Five copies, two slots | 3 |
| Cut-stack | Four source pages, two slots | 2 |
| Manual | Source pages 4, 1, 3, two slots | 2 |
| Poster | Page 1, 300 by 400 point sheets, 18 point margin, 12 point overlap | 9 |

All 16 layout pages render without engine diagnostics at 512 pixels. Inspected
the first manual sheet: page 4 is left and page 1 is right. The source file's
SHA-256 is unchanged. Earlier coverage checked booklet and N-up.

The real LaTeX form from `010-pdflatex-forms` passes conditional image merge in
both editable and flattened modes. Each mode has two successes, one filtered
record and one missing-image failure; the last record completes. Each successful
output contains the image. Editable results retain the expected names, while
flattened results have no form fields. Inspected the flattened Ada output.
Twenty workflow-output pages render successfully through the app batch renderer.

## Installer UI

Built the current .NET Framework installer launcher with `InstallerPackage=true`,
`LauncherVersion=1.9.0` and `LauncherFileVersion=1.9.0.0` into the retained scratch
run. No payload was embedded, signed or installed. Instantiated the real installer
window and rendered its content without invoking installation or uninstallation.

The complete 1.9.0 label measures within its available width in all 19 shipped
installer locales. Inspected English and Russian renders. This verifies the
current version-label layout for #440, not final package installation, native
high-DPI behavior, runtime extraction, signatures or reporter confirmation.

## Verification and remaining work

- App suite: 818 passed, zero failed or skipped.
- Targeted unchanged engine macro/Bates/imposition suites: 38 passed.
- Installer launcher build: zero warnings and errors.
- Product engine source is unchanged in this pass. The prior full engine run
  passed 6,491 tests; it is not presented as a new full run.

Desktop clicks and undo, accessibility structure repair interaction, final package
installation, native scaling, full corpus comparison and the full adversarial
release review remain open. The repeated-XFA-path conversion gap remains open.
No issue was posted or closed, and nothing was pushed or released.

Retain the cited internal run at
`C:/Users/steve/scratch/kp-bench-render/batch-workflows-20261018`, including fixture
PDFs, render CSV/PNGs, installer screenshots and the versioned launcher used for
layout verification. These are development evidence, not public release results.
