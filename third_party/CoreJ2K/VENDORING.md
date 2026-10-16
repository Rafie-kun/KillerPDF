# CoreJ2K vendoring record

CoreJ2K decodes the JPEG 2000 images that appear in PDFs through the `JPXDecode`
filter. It was previously consumed as the NuGet package `CoreJ2K` 2.3.3.91 and is
now built from vendored source so the codec can be audited, patched, and optimized
in place.

## Upstream

- Repository: https://github.com/cinderblocks/CoreJ2K
- Branch: `master`
- Commit: `f5895ac7f8371d6cf1c23828516c66675e07a87a`
- Commit date: 2026-10-09
- Relationship to releases: the `v2.4.1` release tag. It brings the decoder
  conformance fixes found against the ISO test suite and OpenJPEG, damaged-file
  handling, PPM and TLM marker reading, and files whose components use different
  wavelet filters.
- Previously vendored: `f75a5263ba21fcbeb8ee05cf354e9a32737fa42f` (2026-07-21), one
  commit after `v2.3.3`, which downgraded truncation warnings to informational
  messages for partially readable JP2 streams embedded in damaged PDFs. That change
  is part of `v2.4.1`.
- License: BSD-3-Clause. The upstream `LICENSE` and `COPYRIGHT-JJ2000-5.1` are kept
  beside this file. The notice text also appears in `engine/THIRD-PARTY-NOTICES.txt`.

A pristine, unmodified clone is kept outside this repository at
`C:\Users\steve\code\CoreJ2K` and is used as the reference for future merges.

## What was copied

Only the `CoreJ2K` library project, which is the whole codec. The upstream
repository also contains platform adapter projects (`CoreJ2K.Avalonia`,
`CoreJ2K.Skia`, `CoreJ2K.ImageSharp`, `CoreJ2K.Pfim`, `CoreJ2K.Windows`), a test
project, a fuzzing project, a codec test harness, and a solution file. None of
those are used by KillerPDF and none were copied.

The encoder was deliberately kept. Removing it is possible, but the encode and
decode APIs are interleaved in `J2kImage.cs`, `J2kImage.FastPath.cs`,
`J2KDecoderConfiguration.cs` and `J2KEncoderConfiguration.cs`, so stripping it
would create a permanent patch against four files upstream actively edits. The
encoder is unreachable from PDF parsing and is exercised by three engine tests
that build JPEG 2000 inputs on the fly, so keeping it costs a larger assembly and
nothing else.

## Who references it

`engine/KillerPdf.Engine` references this project for decoding. `engine/KillerPdf.Engine.Tests`
references it directly as well, because three JPEG 2000 tests build their own input with the
encoder and therefore use CoreJ2K types in their own source. A transitive reference through the
engine resolves at the command line but not in the Visual Studio design-time build, so the test
project declares its own.

The engine NuGet package includes the built `CoreJ2K.dll` alongside the engine
assembly. Its private project reference does not generate an external CoreJ2K
package dependency. Test projects continue to reference the vendored project.

## Local changes

`CoreJ2K/CoreJ2K.csproj` is modified. Upstream builds it alongside a
`Directory.Build.props` at its repository root, which is not vendored, so the
properties that file supplied are reproduced inline. The changes are:

1. `TargetFrameworks` reduced from five frameworks to `net10.0` only, matching the
   engine.
2. `IsPackable` set to `false`, since this builds as part of KillerPDF rather than
   as its own package. The packaging metadata, package content items and the
   netstandard2.0 `System.Memory` and `System.Buffers` references were dropped with
   it. The bare `NETSTANDARD` define was dropped too; no source file tests for it.
3. `LangVersion`, `Nullable`, `TreatWarningsAsErrors` and the `WarningsNotAsErrors`
   nullable exemption list copied from the upstream props file, so warning behavior
   is unchanged. The build produces 660 nullable warnings, the same as upstream
   builds for net10.0. Upstream's `NuGetAuditSuppress` entries are not reproduced:
   they cover SixLabors.ImageSharp, which only the adapter projects reference.
4. `Version`, `Product`, `Authors`, `Company` and `Copyright` copied from the same
   props file so the compiled assembly still carries the JJ2000, Clary, Cureos and
   Sjofn LLC copyright. `Version` follows the vendored release (2.4.1.0).

`CoreJ2K/j2k/entropy/decoder/ByteInputBuffer.cs` seals the concrete buffer type,
removes virtual dispatch from its methods, and requests inlining for the hot
unchecked byte read. KillerPDF only creates this concrete type. The change keeps
decoded pixels identical while reducing JPEG 2000 entropy-decoding overhead.

## Keeping it in sync

```
cd C:\Users\steve\code\CoreJ2K
git fetch
git log --oneline f5895ac7f8371d6cf1c23828516c66675e07a87a..origin/master
git diff f5895ac7f8371d6cf1c23828516c66675e07a87a..origin/master -- CoreJ2K
```

Apply the relevant changes to `third_party/CoreJ2K/CoreJ2K`, keeping the project
file changes listed above, then update the commit hash and date in this file.

Preserve the local changes above during a sync. Any additional optimization must
be recorded in this section and should be offered upstream first when it is not
specific to KillerPDF, so it can come back through a normal sync.

## Verification after a sync

Build the engine in Release, then run both suites and render the corpus files
that use `JPXDecode` with the previous and the new build
(`tools/EngineCorpusComparison`, the previous build's PNGs as the baseline).
A sync that carries decoder fixes changes pixels, so inspect every changed page
rather than expecting none. As of this vendoring the corpus holds 452 such files
out of 46,946, and 388 of those are in `stress/pdf-association`.

The v2.4.1 sync was verified this way at 1200 pixels, three pages per file: 6,441
engine tests and 737 application tests passed, the same 32 files failed to open
before and after, and 380 of 896 pages changed. Most conformance and regression
changes were one or two levels in a channel, from the color transform rounding
fix. The large changes were repairs: pages that rendered blank or with scrambled
or missing JPEG 2000 images (a Ghostscript magazine cover, a pdf.js article, the
FOP image test sheet) now render them, and stray noise and false color casts are
gone from a PDFium form test and two government documents.
