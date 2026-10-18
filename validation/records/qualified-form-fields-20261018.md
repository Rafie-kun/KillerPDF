# Editing separately authored qualified fields

Date: 2026-10-18. Scope: KillerPDF 1.9 form edits and data import.

Fields such as `customer.first` and `customer.last`, added separately to an existing
PDF, can be edited independently even when their unchanged parent groups share a
name. Terminal field renames and calculation-order updates also work. Ambiguous
parent references and actual field or renamed-parent collisions still fail.

## Diagnosis

The public regression added the two fields separately, then changed only
`customer.last`. It failed with a duplicate `customer` rename error. Candidate
causes were duplicate terminal field names, incorrect qualified-name resolution,
and rename validation incorrectly treating unchanged parent groups as collisions.
The distinct terminal names resolved correctly; the failure was on the unchanged
parent group in the editor's shared value/rename traversal.

The editor now records each resulting name's original name and whether it belongs
to an untyped hierarchy parent. Duplicate groups are permitted only when both
remain unchanged. Calculation-order resolution ignores unrequested duplicate
parents while requiring requested field references to remain unambiguous.

## Verification

- Eight new public cases cover independent value changes, a terminal rename
  followed by editing, calculation-order references to unique children, ambiguous
  parent references, and collision rejection in both parent traversal orders.
- Both value-edit and calculation-order regressions failed before their fixes.
  The focused run, including existing rename and calculation-order checks, passed
  all 12 cases.
- Full engine suite: 6,547 passed, zero failed or skipped. Ordinary app suite:
  834 passed, zero failed or skipped. Normal analyzer and app builds passed with
  zero warnings or errors.
- Actual desktop XFDF import changed only the requested child field. Preview
  cancellation, undo, redo and original source-byte preservation passed.
- Actual `KillerPDF.exe --import-form-data` returned 0. Its saved output retained
  both values and permitted a further independent field edit.

Evidence is retained under
`C:\Users\steve\scratch\kp-bench-render\form-workflows-20261018`:
`qualified-*.pdf`, `qualified-values.xfdf`, `qualified-fields-engine-final` and
`qualified-fields-app-final`. These managed UI checks do not establish native
input or assistive-technology behavior.
