# Installer shortcut repair, October 22, 2026

Scope: KillerPDF 1.9.0 on dev/1.9-overkill. No installation, signing, elevation,
publication or changes to the host's real shortcuts were performed.

The 1.8.80 tag and current application shared two defects: all-users registration
excluded the desktop shortcut, and WScript shortcut failures were suppressed.
The legacy elevation path also dropped the requested desktop flag. Its direct-copy
fallback registered the running download instead of the installed executable.

Repairs:

- Per-user desktop links use the account's Windows desktop location. All-users
  links use the public desktop. An explicit choice to omit the shortcut is retained.
- Elevation and silent compatibility installation preserve the desktop request.
- Native Windows Shell Link creation removes the dependency on WScript.Shell.
  Saved target, working directory and executable icon are read back and verified.
  Temporary output is verified before replacing an existing shortcut.
- Missing targets, unavailable folders and failed writes propagate through setup's
  existing failure reporting. A failed direct-copy installation returns failure.
- Compatibility registration uses the installed executable. Machine uninstall and
  scope-conflict removal include the public desktop shortcut.

Focused evidence is under C:/Users/steve/scratch/kp-release-20261021/shortcut-regression/.
The initial extracted old-behavior seam failed the all-users destination assertion
(scope-red.trx), then failed the blocked-write assertion because no exception was
reported (write-failure-red.trx). The final native Windows set passes 5 of 5 in
results/shortcuts-final-verified.trx: both scopes, reported write failure, native link
header and metadata, Unicode/spaced targets, replacement, retained old bytes on a
blocked replacement, missing targets and temporary-output cleanup. Cases within a
test are not counted as separate tests.

This is native Shell Link and application-build evidence, not an end-to-end
installer pass. Clean per-user and elevated all-users installation, upgrade,
unchecked desktop choice, launching the resulting shortcuts, uninstall, redirected
desktop and policy-denied desktop scenarios remain disposable-VM acceptance gates.
The shared Hyper-V ownership board still assigns KillerShell. SN01/SN02 access is
not yet configured. Do not run actual installer or host policy changes to replace
those gates. Windows permissions can prevent creation; setup must report that
failure rather than claim successful shortcut creation.
