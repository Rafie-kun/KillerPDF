# Window tests on the lab VMs, October 24, 2026

KillerPDF.Tests and every window test runner ran on SN01 (standard local agent account) and
SN02 (administrator agent account), both Windows 11 Proxmox guests: the UI smoke runner's
fixture, interactive and 22 `--verify` modes, and the nine document-state cases. Each guest
built the commit under test from a git archive, with a portable .NET SDK 10.0.402 kept inside
the task folder, and ran the tests in the signed-in agent's desktop session through a temporary
interactive scheduled task. Exclusive leases were held throughout and released afterwards. The
SDK, packages and build copies were removed; results and build logs remain under
`C:\Users\agent\AgentLab\kp-window-tests-20261024b\` on each guest and are copied to
`C:/Users/steve/scratch/kp-vm-tests2/`. The first run's SDK added its tools folder to the agent
user PATH on both guests; that entry was removed and later runs kept the SDK from adding it.

## Results

The app code has not changed since 38d0bcf; the later commits change tests only.

| Run | SN01 | SN02 |
| --- | --- | --- |
| UI smoke, 24 runs, full run at cf3caf1 | 24 pass | 24 pass |
| Document state, 9 cases, at cf3caf1 | 8 pass; print-switch raced (below) | 9 pass |
| KillerPDF.Tests at cf3caf1 | 1,309 of 1,311 | 1,309 of 1,311 |
| Rerun at d9c0713: toolbar-availability, sidebar-actions, 9 state cases | all pass | all pass |
| KillerPDF.Tests at d9c0713 | 1,309 of 1,312 | 1,310 of 1,312 |

The two KillerPDF.Tests failures on both guests are `FileAndDirectoryLinksCannotAliasTheSource`,
which creates symbolic links and needs Developer Mode or the symbolic-link privilege. The third
on SN01 at d9c0713 was `SilentClientIsDroppedAndTheNextOneIsServed`, whose client timed out
before the test's server thread had created its pipe; 607fefc creates the first pipe before the
client connects. Locally, the headless suite passes 1,301 of 1,301 at 607fefc.

## Product defects found and fixed

- **Save menu with no PDF open (228a195).** Since 2213a2c the toolbar disabled the whole Save
  group without a document, including the dropdown arrow, so a mouse could no longer reach
  Document tools (Macros and the certificate catalog) with no PDF open; tool search hides both
  without a document. The arrow now stays enabled while the Save button waits for a document.
  The rules moved to `Services/ToolbarAvailability.cs` with unit tests.
- **Screen readers were told no text was visible (38d0bcf).** Since dcdba6e, the text
  provider's visible ranges mapped characters to the screen with a matrix built from one-unit
  `PointToScreen` steps. `PointToScreen` returns whole pixels and a page canvas unit is usually
  under half a pixel, so the step rounded to zero and every character became an empty box.
  The mapping now spans the whole page, in `Services/VisibleTextGeometry.cs`, with unit tests
  that round as WPF does and cover a quarter-turned page.

## Window tests brought up to date

These failures were in the tests, mostly written against earlier app behavior:

- Tool search (paragraph-edit, accessibility-review, guided-tasks, sidebar-actions): results drop
  down after the box's 140 ms grow animation; the tests now wait for the row.
- Watched-folder: the folder picker lists folders in the background and ignores OK until it has;
  the test clicked OK at once and waited forever. It now waits for the listing.
- Render-recovery: the test rewrote the folder's whole security descriptor, including the audit
  list a standard user cannot write, and its restore persisted nothing. It now adds and removes
  one deny rule.
- Toolbar and sidebar: e511090 moved the sidebar position menu off the button and added the
  measuring and signing commands to the panel menus; one reflection call also lacked a new
  argument. The never-shown toolbar window checks what it can observe, and sidebar-actions checks
  the menus in a shown window, including the Pages rail menu.
- Toolbar-availability and watched-navigation: updated for the Save menu fix and for the
  certificate catalog beside Macros (eaa6125). The toolbar test also waits for the page
  selection to stay clear after opening, and its reading check found the screen-reader defect.
- Accessibility-review: since 2213a2c the app reviews its own complete copy of the document, so
  another program changing the user's file no longer makes the review stale. The source-changed
  case now expects the fix to apply and the user's file to keep the outside change; the
  source-switched case still checks the stale-review rejection. The runner's cleanup now
  dismisses the unsaved-changes prompt for this test, so a failure reports instead of hanging.
- Document state print-switch: waits for the 350 form fields before editing one.
- WatchedFolderServiceTests (b237918) uses the system temp folder instead of a path on this PC.

The run scripts set `KILLERPDF_SMOKE_ENGLISH_MODEL` to an installed English OCR model for the
watched-host test. The shipped app embeds the engine, while the document-state runner compares
it with a loose copy beside the app, so those cases ran against a copy of the app folder with
the engine beside it.

## Reviews

228a195 and 2842683 went through a three-stage review: two valid findings, both stale window
test expectations, fixed in 17c5d61 and 3f6715b. 38d0bcf and cf3caf1 went through a second:
no product defect, four valid low findings in tests and one invalid, fixed in 6a652ad and
d9c0713.

## Open

- The save-conflict prompt after an outside change (`Str_Dlg_SaveConflict`) has no window test;
  FileRevisionTests covers only the detection.
- Several Save As helpers in other window tests click OK as soon as the picker exists. They
  passed on both guests but share the race fixed in the watched-folder test.
