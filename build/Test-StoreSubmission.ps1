#Requires -Version 5.1
# Offline regression fixture. HTTP, listing subprocess and waits are simulated.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$global:storeSubmissionFixtureState = @{
    requestedUrl = 'https://example.invalid/store/1.9.1/KillerPDF-Store.exe'
    patchedUrl = $null
    committed = $false
    submitted = $false
}
$previous = @{}
foreach ($name in @('TENANT_ID', 'CLIENT_ID', 'CLIENT_SECRET', 'SELLER_ID')) {
    $key = "KILLER_STORE_$name"
    $previous[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
    [Environment]::SetEnvironmentVariable($key, 'offline-fixture', 'Process')
}
function Invoke-RestMethod { param($Method, $Uri, $ContentType, $Body) return @{ access_token = 'offline-token' } }
function Invoke-WebRequest {
    param($Method, $Uri, $Headers, $Body, $ContentType, [switch]$UseBasicParsing)
    if ($Method -eq 'Head') { return @{ Headers = @{ 'Content-Length' = '1234' } } }
    $path = ([uri]$Uri).AbsolutePath -replace '^/submission/v1/product/fixture', ''
    $data = @{}
    switch ("$Method $path") {
        'GET /status' { $data = @{ isReady = $true } }
        'GET /packages' { $data = @{ packages = @(@{ packageId = '123'; packageUrl = 'https://example.invalid/store/1.8.91/KillerPDF-Store.exe' }) } }
        'PATCH /packages/123' { $global:storeSubmissionFixtureState.patchedUrl = ($Body | ConvertFrom-Json).packageUrl }
        'POST /packages/commit' { $global:storeSubmissionFixtureState.committed = $true }
        'POST /submit' {
            if ($global:storeSubmissionFixtureState.patchedUrl -ne $global:storeSubmissionFixtureState.requestedUrl -or -not $global:storeSubmissionFixtureState.committed) { throw 'The requested installer URL was not committed before submission.' }
            $global:storeSubmissionFixtureState.submitted = $true
            $data = @{ submissionId = 'offline-submission' }
        }
        'GET /submission/offline-submission/status' { $data = @{ publishingStatus = 'INPROGRESS'; hasFailed = $false } }
        default { throw "Unexpected offline request: $Method $path" }
    }
    return @{ Content = (@{ isSuccess = $true; responseData = $data } | ConvertTo-Json -Depth 8) }
}
function powershell {
    if ($args -notcontains '-ListingsOnly' -or $args -notcontains '-DraftOnly') { throw 'Unexpected listing subprocess.' }
    $global:LASTEXITCODE = 0
}
function Start-Sleep { param($Seconds) }
try {
    & (Join-Path $root 'store\Submit-StoreRelease.ps1') -ProductId 'fixture' -PackageUrl $global:storeSubmissionFixtureState.requestedUrl
    if (-not $global:storeSubmissionFixtureState.submitted -or $global:storeSubmissionFixtureState.patchedUrl -ne $global:storeSubmissionFixtureState.requestedUrl) { throw 'Requested package URL was not submitted.' }
    Write-Output 'PASS: requested installer replaces the old Store URL before submission; no live services contacted.'
} finally {
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key], 'Process') }
    Remove-Variable -Name storeSubmissionFixtureState -Scope Global
}
