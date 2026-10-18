# Local desktop workflow validation, October 18, 2026

Scope: KillerPDF 1.9 development build on `dev/1.9-overkill`. This is a focused
workflow check, not release certification or a renderer parity report.

## Results

- Engine suite: 6,491 passed. App suite: 814 passed. App and engine builds: zero
  warnings and errors with already-built project references.
- Original LaTeX form from `py-pdf-sample-files/010-pdflatex-forms` now exports
  FDF, XFDF and JSON, imports FDF and XFDF, and flattens successfully.
- Reopened data-merge outputs contain Ada/Off and Grace/Yes as supplied, with
  two editable fields each. Input form bytes were not edited.
- Four-page real document produces two booklet sheet sides and one four-up page.
  Inspected the rendered booklet front and flattened form. Continuous Bates
  numbering runs QA000100 through QA000103 in the first file, then starts
  QA000104 in the second file.
- Sharing macro output reopens with four pages. Navigation and preflight reports
  execute. Accessibility reports findings with the documented exit code 3.
- Nine real bookmarks round-trip through JSON with titles, hierarchy, styles,
  page indexes and destination coordinates preserved. Import creates new object
  identities and resolves named destinations to explicit destinations.
- Language repair persists en-US and preserves the source document's extracted text.
- Sixteen final command checks have their expected exit codes; the XFA check is
  an expected rejection, not successful conversion. Twenty-two workflow-output
  pages render successfully at a maximum dimension of 512 pixels.
- Separate sample render: 34 input files, 42 rendered pages, two skipped inputs.
  Password-protected LibreOffice input needs credentials. The unreadable
  metadata sample fails because its catalog Pages value is not a page-tree dictionary.
- Isolated GUI startup, packaging target/signature rejection checks, and startup
  runner positive/negative checks pass. No installer was installed or signed.

## Defects and regression coverage

The public form-data import regression first failed for both checkbox states.
The editor incorrectly required an Off appearance, which [ISO 32000-1](https://opensource.adobe.com/dc-acrobat-sdk-docs/pdfstandards/PDF32000_2008.pdf) section
12.7.4.2.3 makes optional. Removing that requirement fixes the real form import.
The test also verifies source-byte preservation and subsequent flattening.

Flattening separately rejected a text widget without an AP dictionary. Its
public regression failed before the repair and now verifies that the generated
appearance preserves Ada as page text after field removal. Missing checkbox
Off appearances paint nothing when unchecked, as permitted by the format.

The XFA sample `pdfjs/test/pdfs/xfa_filled_imm1344e.pdf` failed while constructing
a dictionary keyed by template paths. Repeated `form1.TextField` paths cannot
currently be distinguished safely. A minimized public regression now verifies
an explicit unsupported-conversion explanation instead of ArgumentException.
The original sample returns exit 1 and writes no output. Repeated-field
conversion remains a feature gap; sibling values must not be silently merged.

## Remaining coverage

Desktop acceptance, undo, cancellation, field recognition quality, accessibility
structure repairs, all production layout modes, conditional/image merge batches,
full corpus comparison, native display scaling and assistive technology remain
open. Installer layout still needs verification in the packaged artifact.
The required full release review has not been performed by this focused pass.

## Retained evidence

Internal run directory: `C:/Users/steve/scratch/kp-bench-render/local-workflows-20261018`.
Retain `checks-final.json`, `render-final.csv`, `corpus-render.csv`, startup logs,
fixture outputs, and rendered PNGs for this record. These are development evidence;
do not publish them as release benchmark results.
