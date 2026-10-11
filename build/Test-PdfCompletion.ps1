# Read-only completion/preflight check. Never builds, signs, fetches, or publishes.
# Test receipts are produced separately by Check-PdfCompletion.py --run-tests.
[CmdletBinding()]
param(
    [string]$MaintenanceRef = 'refs/heads/main',
    [string]$DevelopmentRef = 'refs/heads/dev/1.9-overkill'
)
$ErrorActionPreference = 'Stop'
try {
    $python = Get-Command python.exe -ErrorAction Stop
    & $python.Source (Join-Path $PSScriptRoot 'Check-PdfCompletion.py') --repo (Join-Path $PSScriptRoot '..') --maintenance-ref $MaintenanceRef --development-ref $DevelopmentRef --verify-only
    if ($LASTEXITCODE -ne 0) { throw 'PDF source, forward-port, or test evidence is missing, stale, or incomplete.' }
    exit 0
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
