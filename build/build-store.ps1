#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the signed Microsoft Store installer, KillerPDF-Store.exe.
.DESCRIPTION
    The Microsoft Store runs an EXE submission silently and installs nothing else first, so this
    package carries its own .NET runtime. It is built separately from release.ps1, which never
    uploads it.

    1. Builds the Store package: the installer launcher with a self-contained payload and no
       preview handler.
    2. Signs the payload's first-party files, repacks the payload, then signs the public EXE.
    3. Verifies each signature chain and prints the size and SHA-256 of the result.

    Partner Center installer parameter: /silent

.PARAMETER CertThumbprint
    Preferred. SHA1 thumbprint of the code-signing certificate.
.PARAMETER CertName
    Fallback. CN of the certificate. Ignored when CertThumbprint is supplied.
.PARAMETER SkipSign
    Local test build only. An unsigned build also runs the /silent install smoke test.
.EXAMPLE
    .\build\build-store.ps1 -CertThumbprint "AABBCC..."
#>
param(
    [string]$CertThumbprint = "",
    [string]$CertName       = "Open Source Developer Stephen Riley",
    [switch]$SkipSign
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectDir = Split-Path -Parent $PSScriptRoot
$packageBuild = Join-Path $PSScriptRoot 'build-portable.ps1'
$payloadDir = Join-Path $projectDir 'bin\Release\net10.0-windows\store-package\payload'
$storeExe = Join-Path $projectDir 'bin\Release\net10.0-windows\publish\KillerPDF-Store.exe'
. (Join-Path $PSScriptRoot 'payload-security.ps1')

$tsaList = @(
    "http://timestamp.digicert.com",
    "http://timestamp.sectigo.com",
    "http://ts.ssl.com"
)

function Invoke-Sign([string]$Signtool, [string[]]$CertArgs, [string]$Path, [string]$Description) {
    foreach ($tsa in $tsaList) {
        & $Signtool sign /fd sha256 /tr $tsa /td sha256 @CertArgs /d $Description /du "https://killerpdf.net" /v $Path
        if ($LASTEXITCODE -eq 0) {
            & $Signtool verify /pa /v $Path
            if ($LASTEXITCODE -ne 0) { throw "Signature verification failed: $Path" }
            return
        }
        Start-Sleep -Seconds 3
    }
    throw "Signing failed on all TSA endpoints: $Path"
}

Write-Host "`n==> Building the Store package..." -ForegroundColor Cyan
$buildArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $packageBuild, '-PackageKind', 'Store')
if (-not $SkipSign) { $buildArguments += '-RequireSignature' }
& powershell @buildArguments
if ($LASTEXITCODE -ne 0) { throw 'Store package build failed.' }
if (-not (Test-Path $storeExe)) { throw "Store package not found at: $storeExe" }

if ($SkipSign) {
    Write-Host "`n    UNSIGNED Store package. Do not submit this build." -ForegroundColor Red
} else {
    $kitBase = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    $signtool = $null
    if (Test-Path $kitBase) {
        $signtool = Get-ChildItem "$kitBase\*\x64\signtool.exe" -Recurse -ErrorAction SilentlyContinue |
                    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $signtool) { throw "signtool.exe not found. Install the Windows SDK." }
    $certArgs = if ($CertThumbprint) { @("/sha1", $CertThumbprint) } else { @("/n", $CertName) }

    # Signing changes the payload hashes, so the payload is repacked before the public EXE is signed.
    Write-Host "`n==> Signing the payload's first-party files..." -ForegroundColor Cyan
    foreach ($inner in @(Get-FirstPartyPayloadFiles $payloadDir)) {
        Invoke-Sign $signtool $certArgs $inner "KillerPDF Application"
    }
    Write-Host "`n==> Repacking the signed payload..." -ForegroundColor Cyan
    & powershell -NoProfile -ExecutionPolicy Bypass -File $packageBuild -PackageKind Store -RepackOnly -RequireSignature
    if ($LASTEXITCODE -ne 0) { throw 'Signed Store payload repack failed.' }

    Write-Host "`n==> Signing KillerPDF-Store.exe..." -ForegroundColor Cyan
    Invoke-Sign $signtool $certArgs $storeExe "KillerPDF"
}

$hash = (Get-FileHash $storeExe -Algorithm SHA256).Hash
Write-Host "`n    Store EXE : $storeExe" -ForegroundColor Green
Write-Host "    Size      : $((Get-Item $storeExe).Length) bytes" -ForegroundColor Green
Write-Host "    SHA256    : $hash" -ForegroundColor Green
Write-Host "    Partner Center installer parameter: /silent" -ForegroundColor Green
