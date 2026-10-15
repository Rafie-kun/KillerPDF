#Requires -Version 5.1
# Library only. Exact first-party targets; third-party files are never signed here.
# The preview handler (#410) ships in the installed payload only, so it is signed when present;
# build-portable.ps1 already fails when the installed payload lacks it.
$script:InstalledOnlyFirstPartyFiles = @('KillerPDF.PreviewHandler.dll', 'KillerPDF.PreviewHandler.comhost.dll')

function Get-FirstPartyPayloadFiles([string]$PayloadDirectory) {
    foreach ($name in @('KillerPDF.App.exe', 'KillerPDF.App.dll', 'KillerPdf.Engine.dll')) {
        $path = Join-Path $PayloadDirectory $name
        if (-not [IO.File]::Exists($path)) { throw "First-party payload file is missing: $path" }
        $path
    }
    foreach ($name in $script:InstalledOnlyFirstPartyFiles) {
        $path = Join-Path $PayloadDirectory $name
        if ([IO.File]::Exists($path)) { $path }
    }
}

function Assert-FirstPartyPayloadSignatures([string]$PayloadDirectory) {
    foreach ($path in @(Get-FirstPartyPayloadFiles $PayloadDirectory)) {
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
            $signature.SignatureType -ne 'Authenticode') {
            throw "First-party Authenticode validation failed for $path with status $($signature.Status)."
        }
    }
}

function Write-PayloadManifest([string]$ManifestPath, [string]$PayloadDirectory, [string[]]$Files) {
    $lines = foreach ($file in $Files) {
        $relative = $file.Substring($PayloadDirectory.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        $size = ([IO.FileInfo]$file).Length
        "$hash`t$size`t$relative"
    }
    [IO.File]::WriteAllLines($ManifestPath, [string[]]$lines, [Text.UTF8Encoding]::new($false))
}

# Inspect the final embedded bytes even in PublishOnly mode. Loading managed resources
# does not execute the launcher. Only the owned binaries are written to a test root.
function Assert-EmbeddedFirstPartySignatures([string]$PackagePath) {
    Add-Type -AssemblyName System.IO.Compression
    $root = Join-Path ([IO.Path]::GetTempPath()) ('KillerPDF-signature-check-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $stream = $null
    $archive = $null
    try {
        # Load from bytes so the package file is not locked by this PowerShell process afterward.
        $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes([IO.Path]::GetFullPath($PackagePath)))
        $stream = $assembly.GetManifestResourceStream('KillerLauncher.payload.zip')
        if (-not $stream) { throw "Missing embedded payload: $PackagePath" }
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read)
        foreach ($name in @('KillerPDF.App.exe', 'KillerPDF.App.dll', 'KillerPdf.Engine.dll') + $script:InstalledOnlyFirstPartyFiles) {
            $entry = $archive.GetEntry($name)
            if (-not $entry) {
                if ($script:InstalledOnlyFirstPartyFiles -contains $name) { continue }
                throw "Missing embedded first-party file: $name"
            }
            $input = $entry.Open()
            $output = [IO.File]::Create((Join-Path $root $name))
            try { $input.CopyTo($output) }
            finally { $output.Dispose(); $input.Dispose() }
        }
        Assert-FirstPartyPayloadSignatures $root
    }
    finally {
        if ($archive) { $archive.Dispose() }
        if ($stream) { $stream.Dispose() }
        if ([IO.Directory]::Exists($root)) { [IO.Directory]::Delete($root, $true) }
    }
}
