# Repeated static XFA fields with explicit bindings

Date: 2026-10-18. Scope: local 1.9 conversion and desktop verification.

The converter previously rejected every repeated template path. A failing
`ConvertDistinguishesRepeatedStaticFieldsWithExplicitBindings` regression showed
that two positioned text fields named `form.name`, bound explicitly to different
dataset leaves, could not convert even though their values were unambiguous.

Conversion now preserves the ordered template definitions and gives repeated
fields distinct AcroForm names such as `form.name[0]` and `form.name[1]`. Each
field keeps its geometry and reads its own explicit binding. The regression
checks Ada in the first field and Lovelace in the second, at their expected
positions. Flattened conversion keeps both visible values and removes widgets.

This support requires a static form, text controls, distinct nonempty explicit
bindings, and exactly one dataset value per binding. Scripted or behavioral
templates, ambiguous bindings, missing targets, multiple bound values, and
dynamic repetitions remain rejected. Generated field-name collisions also fail
instead of combining fields.

## Verification

- Engine build: zero warnings or errors with existing references reused.
- Focused XFA converter tests: 21 passed.
- Full engine suite: 6,497 passed, zero failed or skipped.
- App build: zero warnings or errors with existing references reused.
- App suite: 832 passed, zero failed or skipped.
- Isolated actual-shell `--xfa-repeated` case passed editable conversion,
  flattening, cancel, undo and redo. Both field values survived.
- The editable rendering was visually inspected. Editable and flattened PNGs
  are both 600 by 600 pixels and have identical SHA-256 hashes:
  `894D87172D8A5A8BDC5D5831800F695FEE225C9C23FE310FBBF17994C837200A`.
- `--xfa-corpus-guard` verified that the real dynamic
  `xfa_filled_imm1344e.pdf` corpus sample still reports repeated
  `form1.TextField` as unsupported.

PDFs and rendered evidence are retained in
`C:\Users\steve\scratch\kp-bench-render\form-workflows-20261018` as
`xfa-repeated-editable`, `xfa-repeated-flattened`, `xfa-repeated-undone` and
`xfa-repeated-redone`. The desktop fixture uses the real shell and resources,
with isolated startup and settings; it does not certify native keyboard input,
all themes, display scaling, or arbitrary dynamic XFA conversion.
