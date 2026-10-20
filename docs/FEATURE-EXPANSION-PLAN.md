# KillerPDF feature expansion plan

Date: October 20, 2026

Status: Proposed implementation sequence. This document is a design plan, not a second active backlog or a claim that these features are complete. Active assignments belong in the shared BACKLOG.md. The current request authorizes this plan only.

## Objective and boundaries

Make KillerPDF a practical free alternative for more of the work people buy Acrobat or Foxit to do. Complete useful desktop workflows first, then evaluate services that require accounts, hosting, or external integrations.

- Keep local desktop features free, usable offline, and independent of paid APIs.
- Preserve the current engine, WPF application, family design, existing sidebar work, and user documents.
- Treat cloud features as optional. They must not become prerequisites for local editing.
- Do not assign all phases to 1.9. Establish release scope separately after sizing and prototypes. No new version or branch is authorized by this plan.
- Before implementation of each major feature, confirm its interaction design, supported cases, and unresolved decisions. Record acceptance criteria before writing code.
- No push, publication, service deployment, paid subscription, or external message is authorized by this document.

## Starting point

The current 1.9 branch already implements OCR, redaction, form authoring, signatures, comparison, Bates numbering, batch macros, protection, optimization, and selected accessibility repairs. Build on these paths rather than creating competing systems.

Confirmed export limitations in the current implementation:

- Excel output is a list of Page, Line, and Text values, not reconstructed tables.
- Word and PowerPoint exports contain editable text but use image placeholders.
- Structured export does not promise complete layout or semantic preservation.
- Accessibility inspection covers selected requirements; passing those checks does not prove full PDF/UA conformance.

Watched document folders, full paragraph reflow, hosted signature requests, shared online review, browser/mobile editing, and business connectors require a deeper inventory before their exact implementation scope is finalized. Their absence was not established by an exhaustive repository audit.

## Order and dependencies

| Phase | Deliverable | Prerequisites | Relative scope |
| --- | --- | --- | --- |
| 0 | Verified baseline and shared document layout foundation | Current source and release gates | Medium |
| 1 | Table selection and real Excel/CSV export | Phase 0 | Large |
| 2 | Watched-folder automation | Phase 0 and existing macro engine | Medium |
| 3 | Better Word and PowerPoint export | Phase 0; reuse Phase 1 tables | Large |
| 4 | Paragraph editing with reflow | Phase 0 and proven text mutation | Large |
| 5 | Guided accessibility repair | Phase 0 and existing inspection/repair APIs | Large |
| 6 | Guided task workflows | Stable underlying operations; can start with existing tools | Medium |
| 7 | Signature requests and tracking | Approved service design and operating model | Extra large |
| 8 | Shared online review | Phase 7 identity, storage, and revision foundations | Extra large |
| 9 | Browser and mobile editing | Approved platform design; Phases 7/8 for connected use | Extra large |
| 10 | Business integrations and deployment controls | Stable desktop automation; cloud APIs where needed | Large |

Scope labels are planning judgments, not calendar estimates. Size each phase after its first prototype. Phase 2 can proceed without table export. Phase 6 can deliver useful presets before later editor features finish.

## Phase 0: Establish the baseline

1. Inventory existing engine APIs, desktop commands, macro steps, tests, and known limitations for every phase. Record what can be reused and what needs new behavior.
2. Reconcile proposed work with the shared backlog and existing fixes. Avoid duplicate implementations.
3. Capture exact build identifiers and baseline results using the existing 1.9 release gates. Resolve relevant rendering and save defects before expanding the affected path.
4. Assemble synthetic and redistributable fixtures: ruled and unruled tables, merged cells, scanned pages, columns, mixed scripts, rotated pages, images, forms, protected documents, tagged PDFs, and signed PDFs.
5. Extend existing layout/extraction models only where needed. Preserve page coordinates, reading order, source object identity, font information, images, and confidence so export and repair tools can share results.
6. Establish explicit behavior for unsupported documents, cancellation, password handling, signatures, and partial failures. Preserve originals and never imply that a rewrite preserves an existing signature.
7. Agree on a bounded first release for each feature. Record measurable acceptance checks and a representative holdout corpus before tuning algorithms.

Exit criteria: reviewed scope and fixtures, reproducible baseline, identified integration points, and acceptance criteria for the next phase. Unresolved release gates remain visible; passing unit tests does not close them.

## Phase 1: Table selection and real Excel/CSV export

1. Add a page-region selection action for extracting a table, with an entry in Export and the appropriate selection context menu.
2. Detect ruled tables from lines and intersections. Add unruled table detection using text alignment and spacing. Keep original text and coordinates available.
3. Model rows, columns, merged cells, header rows, empty cells, multiline content, and confidence. Use OCR positions for scans without silently treating uncertain characters as correct.
4. Build a preview in which users can move column boundaries, split or merge cells, designate headers, and correct OCR text before export.
5. Support multi-page tables with an explicit preview of repeated headers and page joins. Do not merge unrelated adjacent tables automatically.
6. Write real XLSX cells and CSV rows. Preserve leading zeros and identifiers by default; expose number/date interpretation rather than guessing destructively. Neutralize spreadsheet formula injection from extracted text.
7. Reuse Export's file picker, page selection, cancellation, loss reporting, and batch behavior. Add CLI and macro support after the interactive model is stable.
8. Verify against known expected cell grids, merged ranges, Unicode text, scanned fixtures, and malicious spreadsheet values. Open results in Excel and LibreOffice and review representative outputs visually.

Exit criteria: selected fixture tables export to the expected cells; uncertain structure is reviewable; corrected previews match saved output; failed or canceled exports leave no misleading completed file.

## Phase 2: Watched-folder automation

1. Add a watched-folder configuration surface that selects an existing macro, input folder, output folder, file filters, and failure handling.
2. Start with an opt-in worker while KillerPDF is running. Treat unattended operation while the app is closed as a separate design decision, including portable-install behavior.
3. Detect completed file transfers using stability checks and safe open attempts. Treat file-system notifications as hints and reconcile missed events with bounded scans.
4. Persist a job queue with retry state and input identity. Handle duplicate notifications, restarts, renamed files, locked files, network disconnects, and output folders nested under inputs without loops.
5. Execute existing macro operations with bounded concurrency and cancellation. Save through temporary output and atomic completion. Preserve input files by default and prevent accidental overwrite.
6. Provide pause/resume, per-file results, retry, and an error folder or report. Never store passwords in plain-text profiles or logs.
7. Supply starter recipes for OCR, scan cleanup, compression, and consistent naming. Make recipe effects visible before enabling a folder.
8. Test partial copies, duplicate events, crash/restart, disk full, network reconnect, encrypted inputs, and simultaneous jobs in a disposable environment.

Exit criteria: completed inputs produce the intended outputs without duplicate effective results; recovery is predictable; failures are visible; original documents survive every adverse case.

## Phase 3: Better Word and PowerPoint export

1. Add export choices for editable flow and closer visual layout, with plain-language explanations of their tradeoffs.
2. Embed supported source images with correct cropping, rotation, and transparency. Report unsupported content instead of silently omitting it.
3. Improve Word export in slices: paragraphs and spacing, headings and lists, tables from Phase 1, columns and page sections, then headers and footers.
4. Improve PowerPoint export with positioned editable text, embedded images, tables, and supported shapes. Preserve page size and object order.
5. Offer an explicit rendered-page fallback for content that cannot be represented faithfully. Label its reduced editability before export.
6. Preserve supported links and handle fonts consistently. Check font embedding permissions and report substitution or missing glyphs.
7. Carry the options through existing batch, macro, and CLI exports without changing their default behavior silently.
8. Verify semantic content and editability separately from visual similarity. Open and render results in Word, PowerPoint, and LibreOffice; compare columns, tables, image placement, clipping, and pagination.

Exit criteria: supported images are real embedded assets; expected text and tables remain editable; each unsupported feature has a visible loss report or chosen fallback; representative files open without repair prompts.

## Phase 4: Paragraph editing with reflow

1. Confirm the intended editing interaction: selection boundaries, resize handles, alignment, spacing, overflow indication, and how users distinguish a paragraph from an individual line.
2. Group source text into candidate paragraphs using geometry, font runs, and reading order. Permit manual boundary correction when grouping is uncertain.
3. Start with horizontal, single-column paragraphs. Lay out replacement text within the selected box while preserving supported styles, shaping, and glyph coverage.
4. Rewrite the affected source content rather than covering old words. Preserve unrelated objects, crop/rotation behavior, and shared resources.
5. Show overflow before applying. Offer explicit resize or font-size adjustment. Do not move unrelated page content or reflow across pages in the initial slice.
6. Integrate undo/redo, dirty state, save/reopen, search, copying, and extraction. Identify signed or restricted document consequences before mutation.
7. Expand supported writing directions and mixed scripts only with shaping and visual fixtures. Unsupported cases remain explicit.
8. Test deletion, longer replacements, missing fonts, ligatures, rotations, shared streams, cancellation, and repeated undo. Verify old text is absent from the rewritten affected content and new text extracts correctly.

Exit criteria: supported paragraphs wrap predictably, saved results match the preview, source text is genuinely replaced, and unrelated page content is unchanged.

## Phase 5: Guided accessibility repair

1. Map each existing accessibility finding to a page/object location, an explanation, and a supported repair action.
2. Add a guided panel that highlights the selected finding on the page and distinguishes automatically checkable problems from those requiring human judgment.
3. Wire existing language, description, reading-order, and table-header repairs into reviewable actions with undo.
4. Add a visual reading-order editor and structure inspection using existing tagging APIs. Preserve content-to-tag references and validate structural consistency after edits.
5. Provide reviewed heading, list, figure, artifact, and table assignments. Suggest descriptions only as drafts; require review rather than claiming automatic semantic correctness.
6. Recheck affected findings after each repair and offer an exportable report separating fixed, remaining, unsupported, and manually reviewed items.
7. Validate structure with applicable external validators and exercise real reading/navigation behavior with assistive technology on representative documents.
8. Test malformed trees, complex tables, forms, repeated artifacts, missing descriptions, and save/reopen. Do not equate a clean internal report with complete PDF/UA conformance.

Exit criteria: supported findings navigate to the right content, repairs survive reopening, structure remains valid, and manual accessibility review is recorded separately from automated checks.

## Phase 6: Guided task workflows

1. Define four initial workflows: Prepare for email, Clean up a scan, Remove sensitive information, and Create a fillable form.
2. Compose existing commands and macro steps. Keep one implementation of each operation and avoid a separate workflow processing engine.
3. Show inputs, proposed changes, destination, and relevant tradeoffs before execution. Report achieved file size rather than promising an arbitrary compression result.
4. Provide progress, cancel, retry, and per-file outcomes. Preview irreversible output changes and save a copy by default where appropriate.
5. Let users save a configured workflow as a reusable macro and use compatible workflows with watched folders.
6. Expose workflows through tool search and appropriate menus without overcrowding the toolbar.
7. Verify end-to-end journeys, keyboard navigation, localization, small windows, app scaling, and failure recovery. Confirm redaction and form recognition retain their review steps.

Exit criteria: each workflow completes a recognizable task with understandable choices and produces the same verified result as its underlying commands.

## Phase 7: Signature requests, tracking, and reminders

1. Decide the operating model before coding: self-hosted service, a managed service, or an integration with an existing provider. Compare operating costs, deployment complexity, and what can realistically remain free.
2. Define signer identity requirements, signing method, evidence retention, expiry, cancellation, document residency, and jurisdiction-specific claims with appropriate review. Existing local certificate signing is a separate capability.
3. Design authenticated accounts, tenant separation, encrypted storage, scoped document access, retention/deletion, rate limits, and auditable revisions.
4. Implement request drafting with recipients, signing order, fields, and a final document preview. Sending requires an explicit user action.
5. Implement a browser signing journey with recipient verification, accessible field navigation, consent, completion, and download.
6. Add request status, reminders, expiry, cancellation, and an evidence package tied to the exact signed document revision. Prevent replay and duplicate completion.
7. Integrate request creation and tracking into KillerPDF without requiring an account for local signatures.
8. Test wrong-recipient access, altered documents, expired links, tenant isolation, interrupted signing, duplicate events, recovery, and deletion. Run a bounded pilot before public availability.

Exit criteria: independently reviewed security and signing design, verified document integrity and audit evidence, reliable delivery/state handling, and an approved support and operating budget. Do not advertise legal equivalence based only on implementation tests.

## Phase 8: Shared online review

1. Reuse approved identity, storage, access, and revision services from Phase 7. Keep review permission distinct from permission to edit or sign.
2. Define immutable document versions and stable comment anchors so comments never silently attach to different content after a revision.
3. Support invitations, view/comment permissions, threads, replies, resolved state, and controlled notifications.
4. Build desktop and browser synchronization for comments, including offline edits, conflicts, deleted comments, and revoked access.
5. Reuse existing PDF comment and XFDF interchange where compatible. Make import/export of review history available without requiring permanent service access.
6. Show version changes and allow explicit migration of applicable comments to a newer document.
7. Test concurrent reviews, offline reconnect, conflicting changes, malicious annotation content, revision mismatch, and permission revocation.

Exit criteria: reviewers see consistent versions and threads, conflicts are visible, access is enforced server-side, and review data can be exported.

## Phase 9: Browser and mobile editing

1. Confirm target platforms and the first supported actions. Recommend viewing, annotation, form filling, and signing before full text or structural editing.
2. Prototype reuse of the existing engine through a browser-compatible runtime or a narrowly scoped service. Measure startup, memory, rendering quality, input latency, and offline feasibility before selecting an architecture.
3. Define local versus uploaded processing clearly. Never upload a document merely because it was opened; connected workflows must make their destination explicit.
4. Build responsive navigation, touch selection, zoom, annotation, form entry, and accessible controls. Preserve the family design while adapting to small screens.
5. Implement save/download and safe interruption recovery. Integrate shared review only after local document handling is reliable.
6. Test desktop browsers and representative Android/iOS devices, large PDFs, low memory, rotation, touch/keyboard input, offline use, and interrupted connections.
7. Expand to more editing tools only when their underlying engine path and platform interaction are verified.

Exit criteria: the declared feature subset works on the supported device matrix, saved files remain compatible with desktop KillerPDF, and upload/offline boundaries are clear.

## Phase 10: Business integrations and deployment controls

1. Rank concrete MSP workflows before selecting connectors: Outlook attachments, OneDrive/SharePoint documents, document-management systems, and scripted local jobs.
2. Start with local CLI/macro integration and documented deployment settings. Identify which existing installer/update controls already satisfy the need.
3. Define a narrow connector contract for open, save-copy, version/conflict handling, and authentication. Do not introduce a general plugin framework without a demonstrated requirement.
4. Implement the first approved connector with minimum permissions and secure token storage. Respect server-side access and version checks; do not overwrite another user's changes silently.
5. Add organization settings for update policy, cloud-feature availability, defaults, and logging, with portable versus installed behavior explicitly defined.
6. Document administrator deployment and removal, authentication recovery, supported licenses, and service limitations. External providers may require accounts or subscriptions even when KillerPDF is free.
7. Test revoked access, expired tokens, offline edits, conflicts, tenant boundaries, unattended execution, and removal in a disposable business environment.

Exit criteria: the chosen workflow operates with least necessary access, preserves remote revisions, and has verified deployment and recovery instructions.

## Verification and delivery for every phase

1. Re-read the current implementation and preserve unrelated work before each change.
2. Build one bounded vertical slice at a time: input, operation, preview or review, output, and failure behavior.
3. Use failing behavior tests before algorithm or data-mutation fixes. Add integration and rendered checks appropriate to the change; do not substitute private-method assertions for user-visible behavior.
4. Verify undo, cancellation, save/reopen, source preservation, and output compatibility wherever applicable.
5. Validate applicable family themes, all application locales, keyboard/screen-reader access, app scaling, and constrained window sizes.
6. Re-run affected engine, app, external validation, and corpus gates. Re-measure speed and peak memory after rendering/layout changes rather than carrying forward old performance claims.
7. Run the required adversarial review before release. Record confirmed defects, unresolved investigations, and any remaining runtime or service gates.
8. Commit independently verified changes locally with the current project version convention. Preserve evidence summaries and concise changelog entries. Push and release only under separate authorization.

## Decisions needed before implementation

- Which bounded feature starts first? Recommendation: Phase 1 table selection and export, beginning with single-page ruled tables and an editable preview.
- Which release receives each phase? Recommendation: keep current 1.9 reliability gates independent of the entire roadmap.
- Should watched folders work while the app is closed? Recommendation: start with an explicit running-app worker and decide unattended hosting separately.
- Which export behavior should be the default? Recommendation: editable flow for Word, positioned content for PowerPoint, with clear fallback choices.
- What platforms and operating budget are acceptable for services? Recommendation: finish the local desktop phases before committing to a public hosted service.
- What is the first business connector? Recommendation: choose from actual MSP demand after the local workflow pilot.

## References

- [Current application changelog](../CHANGELOG.md)
- [Structured export behavior and limitations](../engine/docs/exporting.md)
- [Accessibility inspection and tagged content](../engine/docs/accessibility.md)
- [Existing macro execution](../engine/docs/macros.md)
- [KillerPDF 1.9 release gates](../validation/RELEASE-GATES-1.9.md)
- [Adobe product comparison](https://www.adobe.com/acrobat/pricing/compare-versions.html)
- [Foxit PDF Editor feature overview](https://www.foxit.com/pdf-editor/)

Competitor references informed the October 20, 2026 discussion. Recheck current packaging and terms before making public comparisons. This plan makes no claim of complete Acrobat or Foxit parity.
