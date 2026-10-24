# Save conflict content identity, October 24, 2026

The save conflict check compared only file size and modification time. An outside replacement that preserved both could be overwritten without a conflict decision. The repair includes a SHA-256 content digest in `FileRevision`, which all in-place save routes already consult. If an existing file cannot be read, its revision is treated as unknown and requires confirmation. Older recovery records still deserialize; their missing digest also requires confirmation before replacing an existing file.

The actual-file regression replaces three bytes with different bytes, restores the exact captured timestamp and asserts equal lengths before checking for a conflict. It fails before the repair in 101 ms. A second real-file regression holds the destination exclusively and proves that an unreadable revision cannot count as unchanged; it fails before its repair in 6 ms. Fifteen save, published-file and recovery checks pass after both repairs, including current recovery serialization and legacy metadata-only records.

Both failures and the focused pass are retained under `C:/Users/steve/scratch/kp-bench-render/release-sn01-20261024/functional-save-results/`:

| Evidence | SHA-256 |
| --- | --- |
| Preserved-metadata RED | `31F95E02689397205962D209692B6023E153DCB69C483F464505CB25DEF6688F` |
| Unreadable-revision RED | `3EEED0FAA7DF966BE2C72B7D29F4B8F822FCCA7086A3B5CB7D5BC956299457BD` |
| Fifteen focused checks | `BC957E41F2C751A9FA7AEA43D1231BFC79B5AEC98F67DCD3FEED969CC867137A` |

A clean `6f7c11e` source archive plus only the current save helper, its regression tests and the consolidated changelog change builds successfully through the normal portable pipeline. The isolated snapshot verifies those three files byte-for-byte against the live checkout and excludes unfinished benchmark work. Payload ZIP SHA-256 is `ED9C3B5430D1348584AC0475F607DD1FE191358D369066388EEE4110E60BE923`; app DLL is `9B81F94959984DDBB65CEB10F9C4520AC67E1A169519240A44387A22E05282A2`. No engine source changes are included.

The full application suite currently has 1,314 passes and one failure: `AboutPreferenceRemovesMenuShortcutSpaceAndRestoresTheSavedChoice` exceeds its fifteen-second STA-thread deadline. The same case also times out in isolation. Both results are retained. No cause or full-suite pass is claimed, and its timeout is not increased.

Actual Save, Cancel and Save As acceptance is assigned to the existing SN01 worker using a fresh owned form and isolated profiles. Native proof is pending. The original installed app remains outside the test target. This record does not claim release readiness, pixel-baseline acceptance or performance measurements.
