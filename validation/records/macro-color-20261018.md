# Editable macro color conversion

Date: 2026-10-18. Scope: KillerPDF 1.9 ConvertColor macro support.

Grayscale and threshold-based black-and-white steps retain selectable text and
editable form fields. The handler converts device-gray, RGB and CMYK page paint,
Form XObjects, widget appearances, annotation colors and supported image samples.
Pages resolve inherited device-color aliases; reusable forms resolve their own
resource aliases.
Image opacity masks remain unchanged. Editing a converted field retains its gray
text, background and border colors.

The macro editor uses compact family dialogs with existing localized labels.
Current-document, ordered batch, CLI and per-record Data Merge workflows share
the same handler. Unsupported content reports an error before output is written;
there is no automatic page-image fallback.

## Verification

- Eighteen new public-seam tests cover selectable text, later field edits and
  regenerated widget appearance colors, transparent images, bitonal thresholds,
  CMYK path/image tones, device color-space selection and saved graphics states.
- Named device-color regressions initially failed on both pages and reusable forms.
  A form with indirectly stored type names then rendered red after conversion.
  Hypotheses were unresolved type references, a lost replacement form reference,
  or a missing color resource. Changing only the indirect type reproduced the
  failure while the direct-type case passed, confirming the type lookup defect.
  Classification now resolves type references before converting or rejecting the
  object. PostScript rejection also covers indirectly stored type names.
- Safety regressions first returned altered output for overprinting, group masks,
  rich text, XFA, device color replacements, transfer functions and PostScript.
  All now fail safely. A zero-threshold regression exposed implicit black paint
  that was left black; it now becomes white consistently with explicit paint.
- The widget-edit regression exposed the field editor ignoring grayscale `g`
  default-appearance colors. Its parser now preserves them when rebuilding fields.
- Data Merge integration fills two records, resizes and converts each separately,
  and checks both rendered gray content and distinct editable values after Rename.
- Full engine suite: 6,536 passed, zero failed or skipped. Ordinary app suite:
  834 passed, zero failed or skipped. Normal analyzer and app builds passed with
  zero warnings or errors.
- Actual desktop current-document and ordered two-file batch output retained
  selectable text and editable fields. Gray pixels and subsequent field editing
  were checked through public engine APIs. Both source files remained unchanged.
  Current-document preview cancellation wrote no output.
- Actual `KillerPDF.exe --macro` returned 0. The saved PDF retained selectable text,
  an editable field and the expected gray paint. A later field edit also succeeded.
- Settings restored, accepted and canceled in Dark, Light and SE98 using the public
  ThemeManager.Apply path in isolated startup-smoke mode. Dark and SE98 mode dialogs
  and Light and SE98 threshold dialogs were visually inspected. All four labels
  exist in all 19 application locale files.
- A seven-file corpus check converted the PDFBox RGB JPEG and qpdf shared-form
  image fixtures, retaining text, field names and values, page counts and gray
  rendered pixels. Four fixtures stopped for known unsupported color spaces,
  default color replacements or inline images. One source was rejected before
  conversion: its startxref says 4740 but its xref table begins at byte 5097.
  All seven source files remained byte-identical. This bounded check is not a
  release corpus review or a renderer-parity claim.

## Supported limits

Named image color spaces, non-device named paint colors, shading and pattern paint,
inline images, Type 3
glyphs, PostScript XObjects, overprinting, transfer functions, group soft masks,
image matte colors, XFA and rich-text styling are rejected. Images use the existing
bounded image decoder, which rejects unsupported encodings and color-space or mask
combinations rather than guessing. Ordinary image soft masks are preserved.
Retaining transparency means overlapping bitonal paint may still render gray.

Evidence remains under
`C:\Users\steve\scratch\kp-bench-render\form-workflows-20261018`:
`color-mode-*.png`, `color-threshold-*.png`, `color-macro-preview.png`, unique
`color-copy-*.pdf` files, `color-cli.json`, `color-cli-output.pdf`, and the
`color-resources-engine-final` and `color-resources-app-final` test results.
`color-corpus-results-v2.json` records source hashes and the final classifications;
the earlier `color-corpus-results.json` retains the initial source-reader failure.

These checks use managed UI and routed events. Native input, assistive technology
and display scaling remain separate validation.
