# Render diagnostics review, October 23, 2026

Branch `dev/1.9-overkill`. Since 3f45603, `Services/PdfRenderCompleteness.cs` refuses printing, image export, OCR base images, encoding and exact comparison for any page whose render reports a Degraded or OmittedContent diagnostic. Messages it does not recognise default to Degraded. The [A2 run at cb4276a](A2-triage-2026-10-23.md) found 85 corpus pages refused this way, 26 of them for recoveries only. Page-list thumbnails were refused the same way and showed label-only tiles (fixed in e149280).

## Inventory

A read-only trace of `engine/KillerPdf.Engine` found 55 distinct messages that can reach a rendered page's diagnostics. Messages enter through the document's recovery diagnostics, page-tree and resource checks, content parsing, font reading and the renderer partials. Twenty-three blocked publication although the page draws what the file specifies or the fallback ISO 32000 defines.

## Reclassified (ad2f9ba, 9b919cd)

Matched exactly, before the wording rules, so a repair whose message mentions an omission still publishes:

- Information: an unreadable ToUnicode map was ignored. The glyphs come from the embedded font's own CID-to-GID mapping; ToUnicode feeds only text extraction.
- Recovery, malformed input skipped or clamped the way viewers do: invalid line join, invalid text position, miter limit clamped to one, and an invalid graphics-state /LW, /LC, /LJ, /ML, /D or /SA stroke setting.
- Recovery, defined color fallbacks:
  - an ICCBased profile falls back to its /Alternate
  - an unusable output intent leaves DeviceCMYK converting as it would with no intent; this fired on files whose only output intent is sRGB
  - group blending spaces and intents fall back to the alternate or device space
  - black-point compensation is not applicable, or its policy is invalid
  - an invalid or unreadable transfer function is ignored
- Recovery, other repairs: recovered Type 1 font lengths, a cyclic page-tree back-edge (no page is lost), and a comb field value beyond MaxLen.
- Recovery after an engine fix: an unrecognised blend mode. The graphics-state path kept the previous mode; it now paints Normal, as the pattern path already did (0c4b63b, `Render_UnrecognisedBlendModePaintsNormal`).

Corpus effect: 8 of the 85 refused pages now publish. The remaining 77 report omitted content or a visible substitution. Most of them are undefined operators in damaged test files, followed by unavailable glyph outlines, missing fonts replaced with Helvetica, and regenerated form-field appearances.

## Still blocking, deliberately

- Transfer functions not applied to process-color output, a cyclic Form XObject limit, invalid page resources treated as empty: each can change what is drawn.
- Missing fonts replaced with Helvetica, regenerated form-field appearances, retained stale appearances: visible approximations.
- Every omission: unresolved XObjects, undecodable images, missing glyph outlines, truncated content.
- "Rendering operator {op} is not implemented." Kept blocking, including undefined operator names that have nothing to draw, because three existing tests pin `UnsupportedPaint` as blocking.

Whether printing and export should refuse or warn for these remaining pages is a product decision, not settled here.

## Noted, not changed

- Two blocking messages sit in the wrong blocking category: an unimplemented graphics-state soft mask draws unmasked content (Degraded, filed as omitted), and "Text outlines for font {FontName} are not available." (an omission, filed as Degraded). Both still block.
- Silent gaps with no diagnostic: a `gs` naming a missing ExtGState is ignored, and short image sample data is padded in recovery mode. "Invalid page resources were treated as empty." stays blocking until the first gap reports.
- "Unclosed graphics states were restored at the end of the content stream." is classified but produced only by text extraction, never by rendering.

## Verification

`KillerPDF.Tests/PdfRenderCompletenessTests.cs`: 44 pass, including each reclassified message and a set that must still block, including document-supplied names. Engine rendering tests: 3,346 pass. Nothing was run with a window.
