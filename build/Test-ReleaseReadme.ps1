#Requires -Version 5.1
# Run the release script's public README guard without building, signing or publishing.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$releasePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'release.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($releasePath, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw 'Release script contains syntax errors.' }
$guard = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-ReleaseReadme'
}, $false)
if ($null -eq $guard) { throw 'Release README guard is missing.' }
. ([scriptblock]::Create($guard.Extent.Text))
function Reject([string]$Text, [string]$Expected) {
    try { Assert-ReleaseReadme $Text }
    catch {
        if ($_.Exception.Message -notlike "*$Expected*") { throw "Unexpected rejection: $_" }
        return
    }
    throw "Expected rejection: $Expected"
}
$source = 'https://github.com/SteveTheKiller/KillerPDF/releases/download/v1.9.0/KillerPDF-1.9.0-src.zip'
Reject ("Development build.`n" + $source) 'Development build'
Reject ("DEVELOPMENT BUILD.`n" + $source) 'Development build'
Reject '# KillerPDF' 'corresponding-source'
Reject $source.Replace('KillerPDF-1.9.0-src.zip', 'KillerPDF-1.8.91-src.zip') 'corresponding-source'
Assert-ReleaseReadme ("# KillerPDF`n[Source]($source)")
# Future releases retain the existing automatic source-link update behavior.
Assert-ReleaseReadme $source.Replace('1.9.0', '1.9.1')
Assert-ReleaseReadme ([IO.File]::ReadAllText((Join-Path (Split-Path -Parent $PSScriptRoot) 'README.md')))
Write-Host 'PASS release README rejection fixtures and current README.'
