# Co-signing repair, October 22, 2026

## Scope

The application signer reused the engine's default `Signature1` field even when it
already held an approval signature. This blocked the co-signing portion of BF-003.
This record supplements the pinned October 21 review; it does not change its verdicts.

Work began at `4664ed7e1cd40ba4f94d4c77eea56336ef29f5dd` on `dev/1.9-overkill`.
Concurrent working and staged changes were preserved. The isolated app snapshot is
`C:/Users/steve/scratch/kp-release-20261021/cosign-candidate-source`.

## Reproduction and diagnosis

The regression calls the real application `PdfSigner.Sign` twice with a disposable,
self-signed test certificate. The second call failed with
`The signature field 'Signature1' is already signed.`

Ranked hypotheses and predictions:

1. The application reuses an occupied field name. Choosing an available name should
   permit the second signature without replacing the original field.
2. The source copy is incomplete. Appending a signature would then fail byte-prefix
   or original-integrity checks even after resolving the name collision.
3. The writer rejects appended signatures generally. An available field name would
   still fail to produce two verifiable signatures.

The first hypothesis was confirmed. Both resulting CMS signatures verify, and the
second output begins with every byte of the first signed revision. The source file
also remains unchanged. A separate regression caught a valid nonterminal form field
named `Signature1`; name collection now preserves that hierarchy and selects another
name without relaxing explicit signing-target validation.

## Implementation and automated evidence

- `PdfDetachedSignatureWriter.FindAvailableSignatureFieldName` collects existing
  field names, retains the existing unsigned default signature field, and otherwise
  selects an unused numbered name. Explicit signing still applies its existing
  certification, permissions, field-lock and seed-value checks.
- The application signer supplies that field name before creating the CMS signature.
- Four application cases cover initial signing with and without an unsigned default
  field, and co-signing with and without another field occupying the next name.
- The final focused set passes 38 cases: those four, the new field-hierarchy
  regression, and 33 existing signature-writer cases.

Evidence under `C:/Users/steve/scratch/kp-release-20261021/signing-regression/results`:

| Artifact | Outcome |
| --- | --- |
| `cosign-red.trx` | One failed co-signing regression; one existing signing case passed. |
| `cosign-green.trx` | Both initial cases passed. |
| `cosign-controls.trx` | All 37 expanded signing cases passed. |
| `field-hierarchy-red.trx` | The nonterminal-name regression failed as expected. |
| `signing-final.trx` | All 38 cases passed. |

The repeatable loop is `dotnet test SigningRegression.csproj -c Release` from the
scratch signing-regression directory, with its recorded artifacts path and TRX
logger. The test project links the real product sources and references the real
engine project; it introduces no product dependency.

## Application workflow

The frozen full-app Release build succeeded with zero errors. It emitted 671
warnings in vendored CoreJ2K and NTwain code; this was not a zero-warning build.
The smoke host built with zero warnings and errors. The app DLL SHA-256 is
`41B628D5914C63564B91AB1EF065360FB4919C729FE144FB32702C5B470415B2`.

The actual offscreen WPF Save As / Save Copy / co-sign workflow passed all three
groups: measurement Save As, measurement Save Copy followed by co-signing, and
external replacement of the original file. Unchanged copies equal their signed
input byte-for-byte and retain measurement and CMS integrity. The co-signed PDF
retains the complete original signed revision and has two valid CMS signatures.
The original source files remain unchanged by those operations.

Evidence is in `cosign-workflow-green.log` and `cosign-workflow-green/` under the
task scratch directory. This host used only synthetic files and disposable test
certificates, with offscreen windows and no desktop input. The snapshot contains
other concurrent work; this check does not approve every change in that snapshot.

## Remaining acceptance

This service, engine and offscreen application evidence does not establish native
certificate-dialog behavior, Acrobat interoperability, certificate trust, timestamp
authority interoperability, or every certification-permission combination. BF-003's
broader measurement types and native acceptance requirements remain open. No release
artifacts were signed or published.
