# Continuous reading acceptance, October 24, 2026

NVDA and Narrator each complete both pages of the tagged and untagged fixtures on the current ordinary portable application. All twelve expected phrases appear in order, ending with "Page two ends here. The reading check is complete." These four bounded native cases close the earlier continuous-reading gap. They do not certify arbitrary document structure, every Narrator navigation command or audible output quality.

Product code is `73308b6b3446b13f52f096a7e35285a921076890`. The unchanged portable wrapper is `FD41D2F9E7DDB2248D461C400DAB2CB5D77DCEBAB970086EE3137B770D83B93B`, payload ZIP is `0A8389B83AACADF42B286D000260E90923F5917E3677DE4FF73444E86E71A14D`, and actual app DLL is `5B29927D27778279439A10F5B0DA3CE9B6589DA2EDC76C80661DAB5F1EB54B86`. Each launch verifies all 415 extracted payload entries. No startup-smoke host or installed-app forwarding supplies these results.

## Guest and inputs

The separate disposable Sandbox is `2b202ed5-bd46-47ec-b896-1bb52c03be28`, with exclusive claim `f0eba988-bfb6-4999-8222-2a5c805d342a`. Runtime checks require the matching claim, Microsoft Virtual Machine hardware, a name other than SHREDDER and no domain membership. Actual guest is `01F45D00-AB19-4`, Windows 10.0.26100.0, WDAGUtilityAccount in session 1. Networking and input redirection are disabled; only task inputs and results are mapped. Shared KillerLab and the installed host app are untouched.

Both fixtures contain the same 450 characters across two pages: the reading-order headings and cedar, bicycle, notebook, bell, kite and river sentences. Tagged input is `5882BA65712CE8F157A0FD9427F9C898292896C11FA07DD07B818AF471F1AF10`; the existing untagged input copied from SN02 is `A8DE5FBA1C76040012566BFFDA9B4A3F70A35D8A32947734ED1DE7EFCF9F0576`. Their uncompressed bytes respectively contain and omit `/StructTreeRoot`; neither uses object streams. An initial actual-app page extraction preserves tags and is excluded as an untagged fixture. Its result-serialization error and successful tagged output remain retained as setup evidence.

The guest has a real default render endpoint, HRESULT 0. Portable NVDA 2026.2 has a valid NV Access signature and EXE hash `D239FD2BE81FC7D97F62616A6ACDFB08010B92DE6DF65CA314E19F2D8C03C8B0`. It uses its unmodified eSpeak driver, a separate owned profile, volume 0 and disabled add-ons. Config hash is `6DACD5AA7A42809AEFA8993E502E3E3D7F37176D005F26090E7CBB16AA82D377`. Installed Microsoft Narrator is unchanged, signature Valid, EXE hash `96D2CA6A6391127B04BD52E1D5C108F004518B3388A2C46D437912EF96761175`.

## Actual reader results

All Windows input uses Computer Use on the identified Windows Sandbox window. The coordinator visually inspects the guest frame between actions. No PowerShell UI Automation, speech injection, callback modification, replacement text provider or reader-code edit is used.

| Reader and fixture | Evidence |
| --- | --- |
| NVDA tagged | Native document focus, Ctrl+Home and NVDA+Down produce all twelve phrases in order. Actual eSpeak `lineReached` callbacks culminate in `say-all:stop` after the final phrase at 21:05:47 Pacific. |
| NVDA untagged | A fresh profile and app extraction repeat the same actual command sequence. All phrases and the final stop callback occur by 21:13:59. Normal NVDA+Q exits the reader; visible Quit closes the candidate before its deadline. |
| Narrator tagged | Native document focus, Ctrl+Home and Narrator+R read through page two. The actual Speech Recap visibly contains every expected phrase and the final completion sentence. The already inspected frame is retained as evidence. |
| Narrator untagged | A separate launch reads the same complete text. After minimizing Narrator Home, document focus and Narrator+R advance through page two. The actual recap contains all twelve phrases in order. Narrator+Escape and visible app Quit finish normally before the deadline. |

Microsoft documents [read from cursor and Speech Recap](https://support.microsoft.com/en-us/accessibility/windows/narrator/appendix-b-narrator-keyboard-commands-and-touch-gestures). The evidence is actual reader output, with Narrator's protected recap observed through the guest frame. No subjective listening or waveform claim is made. Earlier NoSpeech, unavailable OneCore endpoint, host focus and protected-window copy failures remain preserved; these new independent cases supply the previously missing complete reading proof.

## Cleanup and retained evidence

Each case has a three-minute process-identity deadline. Tagged NVDA and tagged Narrator complete reading before their deadlines, but their guards later force-stop the exact tracked reader/app/wrapper processes. Those cases do not establish normal exit. Both untagged cases exit through normal reader and app controls; guards confirm all tracked PIDs absent without forced termination. Numeric app exit codes are not retained for these reader cases.

Final guest process inventory contains zero Narrator, NVDA or KillerPDF processes. The guest-only default endpoint volume is restored from 0 to its captured 1.0, and the screen-reader flag is verified at its original false value. No host audio setting is changed. After export, the exact Sandbox is stopped, CLI inventory is empty and only the matching lease is removed. Both SN leases had already been released after their independent accepted handbacks and cleanup.

Raw evidence root is `C:/Users/steve/scratch/kp-bench-render/release-sn01-20261024/installer-continuation/sandbox-associations-01/results/`.

| File | SHA-256 |
| --- | --- |
| `audio-preflight.json` | `2B753165372CF0F0AA5C228A84947C617B1A9F323B033236A5B7C4D1CA658049` |
| `nvda-sandbox.log` | `77A3DB7156BF1630537B4BF28E4DB740DF4A3FFD164AE39EC15DE40A40849194` |
| `nvda-untagged.log` | `38C00E1D8D2E88E6A93D11FBBE8BBE424C70FF12E6B2973519D6E5434E7FDD2D` |
| `narrator-tagged-recap.png` | `9F313C4CC53478B23E999AA96E55DCF5D2A4F56C290F3E0E9850F06ADCF4A99E` |
| `narrator-untagged-recap.png` | `2F343D45C5FCC0384A1F4A8D689AD5FA576ABC3EA62C6BFF0392E14304AF5B36` |
| `reader-watchdog-cleanup.json` | `A9E1D75CCCE4FFFF6E3AB86D3E2F738C1E5B6BBD010F6748C3F5BD87B565AC87` |
| `reader-untagged-watchdog-cleanup.json` | `ACBC0CFC2A3516C6059BA44D3678478162F0CB7224F2E389FEDA1DFC4C99AFA0` |
| `narrator-watchdog-cleanup.json` | `9E7AA227E6E05594339D6D4FE93271EEF8108F90C3360A8317B8F4C00086B0A9` |
| `narrator-untagged-watchdog-cleanup.json` | `55EDD686890DDE44553F3D0D4C8EC14E9091F714D8294E7CE17336B9C260B030` |
| `reader-final-cleanup.json` | `2D59249F2BDFCB616F0F0AFD21C5DA9EB2D22B6C2B54501CAE83B8CA5D4F6FE6` |

Earlier native NVDA navigation, selection, terminal movement, zoomed visibility and two-pane ownership retain their separately pinned scope in [the readiness record](release-readiness-20261024.md). Signing, installers, adverse-state recovery and rendering/performance gates are separate. No product code or reader package changes in this validation batch.
