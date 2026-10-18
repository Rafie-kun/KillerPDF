# Editable image color conversion, October 18, 2026

The public macro converter accepted an ICCBased image even though profile-aware
editable conversion is outside its supported scope. The generic image decoder
returns raw profile samples and a component count; treating those samples as
DeviceRGB or DeviceCMYK can select the wrong gray value.

The minimized public regression constructs a one-pixel CMYK image with a valid
synthetic ICC profile, then executes `PdfColorConversionMacro.Execute`. It must
reject the unsupported conversion before returning output. Before the guard,
the regression fails in 211 ms because no exception is thrown.

The relevant alternatives are profile-aware conversion, implicit substitution
of the profile's alternate device space, or raw sample conversion. Inspection
of `ConvertImage` and `PdfImageRedaction.Decode` confirms the last path: only the
component count reaches the gray conversion. The repair explicitly limits
image conversion to supported device spaces before decoding samples. Stencil
images retain their separate path because their paint color is converted by
the surrounding content instructions.

Evidence root:
`C:\Users\steve\scratch\kp-bench-render\form-workflows-20261018`.
`macro-image-profile-red-runtime` contains the actual red result. The earlier
`macro-image-profile-red` attempt has a fixture compile error, corrected by
passing an explicit byte array to the public image builder. It is not red
runtime evidence. All 21 color-conversion tests pass after the guard, and the
full engine suite passes 6,569 tests. Debug and Release engine builds and the
Release app build pass without warnings or errors using built dependencies.

The actual executable reproduction uses a one-pixel RGB image with the embedded
profile extracted from the retained `transparency_group.pdf` corpus source.
`Program.cs --profile-image-fixture` creates the isolated PDF and typed macros
under `profile-image-cli` before constructing any WPF application. The previous
packaged app accepts both modes and writes PDFs. The rebuilt app rejects both
at ConvertColor with exit code 1, writes no output, and leaves the source hash
unchanged. The first CLI assertion incorrectly expected argument-error exit 2;
the corrected `*-verified.log` checks use the macro failure contract, exit 1.

Release app hash:
`93304C793E6FA642CCDCD6279901F5578FEF8905998E42F5BD17143F59BD963C`.
Its compressed embedded engine matches the built Release engine:
`F1D2CF176C6CEF9EBB12466737D520000AB9D46633B42FEB0EFEC34E802B558B`.
Debug engine hash:
`5D7446ECAD3BE0E5E3B8E8EBEFD94A074D50A5C8D2398E0F4A0E9207446E50CE`.
The Debug app build passes without warnings or errors, and all 834 source-linked
ordinary app tests pass. Renderer code is unchanged by this macro guard; the
existing pixel evidence remains labeled with the payload that produced it.
