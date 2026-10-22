# Installer VM validation, October 22, 2026

Unsigned Release packages built from a hash-verified copy of the 1.9 working tree.
This includes concurrent uncommitted changes; it is not a signed release artifact.
Source manifest and logs are retained under
C:/Users/steve/scratch/kp-release-20261021/.

SN01 tests ran as the standard local agent account without a shared .NET runtime.
SN02 tests ran as the administrator agent account with Desktop Runtime 10.0.13.
Both are Windows 11 Proxmox guests with baseline snapshots. Exclusive leases were
held during guest changes. No DC changes, snapshot restores or host installs ran.

## Installer and shortcut results

- Store package /silent /no-preview: exit 0 on both guests, without desktop links.
- Store package /silent /desktop /no-preview: exit 0 on both. SN01 created the
  user desktop link; SN02 created the Public Desktop link. Native link headers,
  installed targets, working directories and executable icon index 0 were verified.
- SN01 visibly launched the installed app from its Start-menu entry. An earlier
  Explorer address-field attempt opened Edge and does not prove desktop-link launch.
- SN02's Public Desktop link visibly launched the app in desktop session 2 through
  a temporary interactive scheduled task. Screenshot: sn02-public-shortcut-launch.png.
- Regular installer on SN02 replaced the Store installation successfully, retaining
  the Public Desktop shortcut with its installed target and executable icon.
- Regular installer on SN01 returned 10 for the missing runtime and preserved the
  existing Store executable and desktop link.

Original package hashes:

- Store: 924590754D58392F5D2F2DE4CB3B89BA474B4F361AEDB7B83399F089CF5F3022
- Regular: 7B25569DF1D5F5B04D0E65C76EAABE6D04B48F8CF57F98271CFDF1FF0D82F189

## Cross-session uninstall regression

The real CLI boundary reproduced the failure on both guests. With KillerPDF open
in desktop session 2, SSH session 0 launched the installed executable with
/uninstall-silent. It returned 0, removed shortcuts and registry entries, and
deleted unlocked payload files while the desktop process retained loaded files.
The manifest disappeared. Reinstallation returned 1 because the remaining folder
was no longer a verified payload. Evidence: sn01-uninstall-reinstall.json and
sn02-uninstall-reinstall.json.

Ranked hypotheses were a missed session-scoped instance, delayed cleanup, and an
independent file lock. Both desktop processes remained alive after cleanup and the
surviving files were loaded modules. SingleInstanceChannel includes the session
ID in both the Local mutex and pipe names. The uninstaller interpreted an absent
session-0 mutex as proof that no app was running. File locks explain the leftover
modules, but not why uninstall began while the session-2 app was still running.

The fix refuses uninstall while another session has a same-named app process.
It deliberately preserves the existing account/session isolation of file-open IPC.
The uninstaller now returns 1618 for a running instance rather than reporting
success. It also returns 1 for an unregistered copy and 1223 for cancellation;
those latter two status changes have source/build evidence only.

The failed installations were preserved under each guest's task directory after
stopping only the empty test-owned app processes. No recursive deletion was used
to recover them. Fresh fixed packages were then installed.

The same cross-session CLI scenario passed on both guests: exit 1618, unchanged
manifest and app assembly hashes, desktop links and registration retained, and
the desktop process still alive. Evidence: sn01-uninstall-cross-session-green.json
and sn02-uninstall-cross-session-green.json.

After stopping only those test-owned empty app processes, normal silent uninstall
returned 0 and removed the payload root, desktop link and registration on both
guests. Reinstallation returned 0 and restored all three. Evidence:
sn01-uninstall-reinstall-green.json and sn02-uninstall-reinstall-green.json.

Fixed package hashes:

- Store: 319931680FCEAE149442988326BF2043E344A28A98B0C0784F078F054297FB4A
- Regular: 1DC80C93D3A1D254234D11E230F3973B189732A9F7D6F37975E70C4140FEF35A

Both packaging runs passed GUI startup, payload inventory and staging-install
checks. Third-party compiler warnings remain in the build logs. This is not a
zero-warning build claim.

The full application test suite also passed 1,142 of 1,142 with no skips, using the
snapshot's normal project output layout so source-discovery tests can find their
repository inputs. TRX: vm-validation-tests/app-suite-native-layout.trx. All 49
failed test names from the earlier external-artifacts run are present and passing
in this run; none are unmatched. Individual errors and results are retained in
prior-49-failure-comparison.json. This resolves the earlier failed-run checkpoint;
it does not substitute for the remaining interactive or release gates.

## Limits

Signed artifacts, UAC wizard transitions, upgrade from a released 1.8 build,
redirected or denied desktop folders, Explorer previews and Store delivery remain
unverified. SN01 desktop-link launching is not yet independently demonstrated.
The guard conservatively refuses uninstall for any same-named app in another
session, including a separate portable copy. Snapshot rollback and domain-trust
recovery remain untested. This does not waive app-specific disposable-lab rules.
