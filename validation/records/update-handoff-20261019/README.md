# BF-B003 custom update destination

The installed update command now forwards the directory containing the current executable with the launcher's existing `/install-dir64:` argument. Portable update behavior is unchanged.

The original installed branch was extracted without changing its command. A real batch file launched an external capture executable in place of the installer. Both scopes failed: the process received only `/install-user` or `/silent`, with no destination. After adding the destination, both scopes pass. The retained probe additionally covers an ampersand and Unicode paths.

Ranked hypotheses before the repair were omitted destination, argument loss in batch parsing, and an incompatible launcher format. The captured original arguments establish the omission. Captured repaired arguments decode exactly to the requested directory, and the format matches `Program.ReadInstallDirectory` in the launcher.

See [the probe and commands](../../../tests/KillerPDF.UpdateHandoffSmoke/README.md). Its public production boundary is the command handed to the external installer; the fake replaces only that process. Real Office and installer execution were not performed on the host. Full signed update, elevation, registration, rollback, and relaunched-version acceptance require the disposable lab.
