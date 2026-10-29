# Architecture

The KillerPDF.Engine is a reusable document library inside the KillerPDF monorepo. The project reference enforces a UI-free boundary while the shared repository lets application integration, library behavior, tests, and corpus gates evolve together.

## Library boundary

KillerPDF 1.9 uses The KillerPDF.Engine for rendering, text extraction, and OCR. Tesseract remains available in the desktop app as a fallback OCR provider. See the developer guide for current engine APIs.

## Internal layers

| Layer | Responsibility |
| --- | --- |
| `Syntax` and `Objects` | Binary-safe tokens and the typed PDF object model |
| `CrossReference`, `Filters`, and `Documents` | Revision discovery, stream decoding, lazy resolution, and authenticated access |
| `Authoring` | Typed construction of pages, content, resources, metadata, interactive features, and standards structures |
| `Editing` | High-level byte-preserving changes and safe cross-document graph imports |
| `Writing` | Incremental revisions, deterministic rewrites, metadata policy, and output sanitization |
| `Security` and `Signing` | Authentication, permissions, password and certificate encryption, signatures, timestamps, and revision verification |
| `Rendering` and `Fonts` | BGRA32 page rendering, color management, image decoding, font decoding, and glyph outlines |
| `Diagnostics` and `Validation` | Bounded inspection, implementation limits, structural reports, and round-trip checks |

## Read flow

1. Parse the header and locate the final `startxref` marker.
2. Read the cross-reference revision chain.
3. Authenticate encrypted documents before resolving protected objects.
4. Resolve indirect objects lazily through the cross-reference table.
5. Present common document features through typed readers.

## Write flow

For a deterministic rewrite, `PdfDocumentWriter` walks the resolved graph and serializes a normalized document. Repeating the write with equivalent input produces reproducible output.

For an incremental edit, a high-level editor records the intended changes, imports dependent graphs when needed, validates the result, and appends one new revision without modifying the original byte prefix.

## Fail-closed editing

Some operations intentionally refuse incomplete global structures. Examples include unsafe partial imports from tagged documents, ambiguous shared object graphs, unsupported XFA merges, missing credentials, and edits forbidden by authenticated user permissions.

A refusal is safer than producing a file that opens but has silently damaged navigation, forms, accessibility, optional content, or signatures.

## Architecture decisions

- [ADR-001: PDF engine boundary](https://github.com/SteveTheKiller/KillerPDF/blob/main/engine/docs/architecture/ADR-001-pdf-engine-boundary.md)
- [ADR-002: Modern Windows shell integration](https://github.com/SteveTheKiller/KillerPDF/blob/main/engine/docs/architecture/ADR-002-integrate-engine-through-modern-windows-shell.md)
- [ADR-003: Installed and portable packages](https://github.com/SteveTheKiller/KillerPDF/blob/main/engine/docs/architecture/ADR-003-split-installed-and-portable-packages.md)
