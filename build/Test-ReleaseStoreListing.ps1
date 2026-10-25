#Requires -Version 5.1
# Load only the listing guard, never the release workflow or Store submission.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'release.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw 'Release script contains syntax errors.' }
$guard = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-ReleaseStoreListing'
}, $false)
if ($null -eq $guard) { throw 'Release listing guard is missing.' }
. ([scriptblock]::Create($guard.Extent.Text))
foreach ($stale in @('PDFium rendering', 'pdfium rendering', 'PDFIUM')) {
    $rejected = $false
    try { Assert-ReleaseStoreListing "Field,Type,English`nDescription,Text,$stale" }
    catch {
        if ($_.Exception.Message -notlike '*PDFium*') { throw "Unexpected rejection: $_" }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Expected stale PDFium listing rejection.' }
}
Assert-ReleaseStoreListing "Field,Type,English`nDescription,Text,KillerPDF engine"
Assert-ReleaseStoreListing ([IO.File]::ReadAllText((Join-Path $root 'store\KillerPDF-Store-listing.csv')))
Write-Host 'PASS release Store listing rejection fixtures and current CSV.'
