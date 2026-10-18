# Editable XFA template occurrences

Date: 2026-10-18. Scope: KillerPDF 1.9 XFA conversion with repeated template paths.

Static and dynamic text fields with the same template path can convert when each
definition has a distinct explicit binding to one dataset value and the template
has no scripts or behaviors. Dynamic flow now carries the template definition
index separately from the data occurrence, including fields inside table rows.
Each output field retains the matching definition's tooltip and dimensions.

Converted fields are authored together as one hierarchy before being imported
onto the source pages. This prevents separate copies of a shared parent from
blocking a later field edit. Both editable and flattened conversion remain
available; the default output retains editable fields.

## Diagnosis and verification

- A public conversion regression first rejected a dynamic pair with distinct
  explicit bindings. The candidate causes were a blanket dynamic-form guard,
  ambiguous dataset matching, and lost template identity in flow placement.
  Distinct static bindings already resolved correctly. Dynamic conversion used
  only the first definition for every placement and had no template index.
- After preserving template identity, the regression reached a later field edit
  and exposed duplicate hierarchy parents created by separate field authoring.
  Authoring the complete converted hierarchy together fixed that failure.
- Three new cases cover vertical flow, table-row cells and preservation of the
  planner's existing behavior for unnamed fields outside flowed containers.
  Existing static conversion coverage now also edits one converted value and
  verifies its sibling is unchanged.
- Full engine suite: 6,539 passed, zero failed or skipped. Ordinary app suite:
  834 passed, zero failed or skipped. Normal engine analyzer and app builds passed
  with zero warnings or errors.
- Actual desktop conversion retained Ada and Lovelace in distinct editable fields;
  a later edit changed only Lovelace to Byron. Cancellation, undo, redo and source
  byte preservation passed. Editable and flattened output rendered pixel-identically
  at 600 by 600, and the explicit 8-point fixture capture was visually inspected.
- Actual `KillerPDF.exe --xfa-to-acroform` returned 0. Its saved output retained both
  values and allowed a later independent field edit.
- The real `xfa_filled_imm1344e.pdf` corpus sample still fails safely on repeated
  `form1.TextField`. Scripts, behaviors, absent or duplicate explicit bindings and
  multiple values per repeated definition remain outside this supported subset.

Evidence is retained under
`C:\Users\steve\scratch\kp-bench-render\form-workflows-20261018`:
`xfa-dynamic-*.pdf`, `xfa-dynamic-render.png`, `xfa-occurrence-engine-final` and
`xfa-occurrence-app-final`. These managed UI checks do not replace native input,
assistive-technology or display-scaling validation.
