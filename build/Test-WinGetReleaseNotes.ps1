#Requires -Version 5.1
# Run the WinGet manifest's release-notes shaping without contacting GitHub or WinGet.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $PSScriptRoot 'New-WinGetManifest.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$errors)
if ($errors.Count -gt 0) { throw 'WinGet manifest script contains syntax errors.' }
$shaper = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-WinGetReleaseNotes'
}, $false)
if ($null -eq $shaper) { throw 'WinGet release-notes shaping is missing.' }
. ([scriptblock]::Create($shaper.Extent.Text))
$url = 'https://github.com/SteveTheKiller/KillerPDF/releases/tag/v1.9.0'
function Assert-Notes([string]$Body, [string]$Name) {
    $notes = Get-WinGetReleaseNotes -Body $Body -ReleaseUrl $url
    if ($notes.Length -lt 1 -or $notes.Length -gt 10000) { throw "${Name}: $($notes.Length) characters." }
    $lines = $Body.Trim() -split '\r?\n'
    $kept = @($notes -split '\r?\n')
    if ($Body.Trim().Length -le 10000) {
        if ($notes -ne $Body.Trim()) { throw "${Name}: notes within the limit were changed." }
        return
    }
    if ($kept[-1] -ne "The full release notes are at $url") { throw "${Name}: no pointer to the full notes." }
    # Every kept line is a whole line of the original, in order.
    for ($index = 0; $index -lt $kept.Count - 2; $index++) {
        if ($kept[$index] -ne $lines[$index]) { throw "${Name}: line $($index + 1) was cut or reordered." }
    }
}
function Reject([string]$Body, [string]$Expected) {
    try { Get-WinGetReleaseNotes -Body $Body -ReleaseUrl $url | Out-Null }
    catch {
        if ($_.Exception.Message -notlike "*$Expected*") { throw "Unexpected rejection: $_" }
        return
    }
    throw "Expected rejection: $Expected"
}
Reject '' 'release notes'
Reject "  `r`n " 'release notes'
Assert-Notes 'One line.' 'short'
Assert-Notes ('x' * 10000) 'exact limit'
Assert-Notes ((1..400 | ForEach-Object { "- Entry $_ describes one change in a full sentence." }) -join "`r`n") 'long list'
Assert-Notes ((1..3 | ForEach-Object { 'y' * 6000 }) -join "`n") 'long lines'
$changelog = [IO.File]::ReadAllText((Join-Path $repo 'CHANGELOG.md'))
$section = [regex]::Match($changelog, '(?s)## \[1\.9\.0\][^\n]*\n(.*?)(?=\n## \[)').Groups[1].Value
if ($section.Length -le 10000) { throw 'The 1.9.0 fixture no longer exceeds the WinGet limit; pick a longer section.' }
Assert-Notes $section 'CHANGELOG 1.9.0'
Write-Host 'PASS WinGet release-notes fixtures and the 1.9.0 changelog section.'
