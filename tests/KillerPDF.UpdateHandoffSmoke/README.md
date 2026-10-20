# Installed update handoff

This probe runs the production installed-update arguments against a capture process standing in for the installer. It checks both installation scopes, spaces, an ampersand, and Unicode in the custom destination and in the document to reopen. It never installs software, changes registration, or shuts down KillerPDF.

Build with output and intermediates in scratch:

```powershell
dotnet build tests/KillerPDF.UpdateHandoffSmoke/UpdateHandoffSmoke.csproj -c Release -o C:/Users/steve/scratch/kp-boundaries-20261019/update/probe -p:BaseIntermediateOutputPath=C:/Users/steve/scratch/kp-boundaries-20261019/update/obj/
& C:/Users/steve/scratch/kp-boundaries-20261019/update/probe/UpdateHandoffSmoke.exe C:/Users/steve/scratch/kp-boundaries-20261019/update/new-run
```

The run directory must not exist. A passing result proves the installer receives the destination, the relaunch request and the document to reopen in the launcher argument format, with no batch file in between. It does not prove signed payload validation, installation, elevation, registration, rollback, or the relaunch itself.
