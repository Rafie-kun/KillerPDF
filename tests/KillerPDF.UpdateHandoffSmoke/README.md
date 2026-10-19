# Installed update handoff

This probe runs the production installed-update command through a real batch file and substitutes a capture process for the external installer. It checks both installation scopes, spaces, an ampersand, and Unicode in the custom destination. It never installs software, changes registration, or shuts down KillerPDF.

Build with output and intermediates in scratch:

```powershell
dotnet build tests/KillerPDF.UpdateHandoffSmoke/UpdateHandoffSmoke.csproj -c Release -o C:/Users/steve/scratch/kp-boundaries-20261019/update/probe -p:BaseIntermediateOutputPath=C:/Users/steve/scratch/kp-boundaries-20261019/update/obj/
& C:/Users/steve/scratch/kp-boundaries-20261019/update/probe/UpdateHandoffSmoke.exe C:/Users/steve/scratch/kp-boundaries-20261019/update/new-run
```

The run directory must not exist. A passing result proves the actual batch handoff preserves the destination in the existing launcher argument format. It does not prove signed payload validation, installation, elevation, registration, rollback, or relaunch after an update.
