# NTwain vendoring record

NTwain talks to scanners through TWAIN for Scan to PDF (#73). It is built from vendored
source, the same way as CoreJ2K, so it can be audited and patched in place.

## Upstream

- Repository: https://github.com/soukoku/ntwain
- Branch: `v3`
- Commit: `5f17d4044e3817aa614e1f133ab8c2406e5e8416`
- Commit date: 2026-05-08
- Version: 3.7.6, the last release line that the NuGet package `NTwain` ships.
- License: MIT. The upstream `LICENSE.txt` is kept beside this file. The notice text also
  appears in `engine/THIRD-PARTY-NOTICES.txt`, which ships with the app.

## What was copied

Only the `src/NTwain` library project. The upstream repository also holds the TWAIN
specification, tests, samples and a solution file. None of those are used by KillerPDF
and none were copied. `Sign.snk` was left out because the vendored build is not
strong-named.

## Who references it

`KillerPDF.csproj` references this project. The engine does not.

## Runtime requirement

A 64-bit app reaches scanners through the 64-bit TWAIN Data Source Manager,
`TWAINDSM.dll` in the Windows system folder. Scanner drivers with 64-bit TWAIN support
install it. It is LGPL and is not bundled; when it is missing, Scan to PDF says so.

## Local changes

`NTwain/NTwain.csproj` is rewritten. The changes are:

1. `TargetFrameworks` reduced from four frameworks to `net10.0-windows` only, matching
   the app.
2. `IsPackable` set to `false` and the package properties, SourceLink reference and
   signing key removed, since this builds as part of KillerPDF rather than as a package.
3. The upstream version, author and copyright metadata are kept so the compiled assembly
   carries them.

The library sources are unmodified apart from three documentation comments, two in
`Data/TwainTypesExtended.cs` and one in `Triplets/DGImage/DGImage.ImageLayout.cs`, where a dash
character was reworded because this repository does not use them. No code changed.
