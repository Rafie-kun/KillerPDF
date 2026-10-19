#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $EvidenceDirectory) {
    throw 'Use a new evidence directory so previous test files cannot affect the result.'
}
[IO.Directory]::CreateDirectory($EvidenceDirectory) | Out-Null

function New-TestPdf {
    param([string]$Path)
    $pdf = [Text.StringBuilder]::new()
    [void]$pdf.Append("%PDF-1.7`n")
    $objects = @(
        '<< /Type /Catalog /Pages 2 0 R >>',
        '<< /Type /Pages /Count 3 /Kids [3 0 R 4 0 R 5 0 R] >>',
        '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> >>',
        '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Resources << >> >>',
        '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << >> >>'
    )
    $offsets = @()
    for ($index = 0; $index -lt $objects.Count; $index++) {
        $offsets += $pdf.Length
        [void]$pdf.Append("$($index + 1) 0 obj`n$($objects[$index])`nendobj`n")
    }
    $xref = $pdf.Length
    [void]$pdf.Append("xref`n0 6`n0000000000 65535 f `n")
    foreach ($offset in $offsets) {
        [void]$pdf.Append($offset.ToString('0000000000', [Globalization.CultureInfo]::InvariantCulture))
        [void]$pdf.Append(" 00000 n `n")
    }
    [void]$pdf.Append("trailer`n<< /Size 6 /Root 1 0 R >>`nstartxref`n$xref`n%%EOF`n")
    [IO.File]::WriteAllBytes($Path, [Text.Encoding]::ASCII.GetBytes($pdf.ToString()))
}

function Invoke-Split {
    param([string]$Source, [string]$Destination, [string[]]$Options, [string]$LogStem)
    $arguments = @('--split', $Source, $Destination) + $Options
    # Test paths and options contain no quotes; quote each argument to preserve spaces.
    foreach ($argument in $arguments) {
        if ($argument.Contains('"') -or $argument.EndsWith('\')) { throw 'Unsupported test argument.' }
    }
    $quoted = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $ExePath
    $start.Arguments = $quoted
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) {
            $process.Kill()
            $process.WaitForExit()
            throw 'The headless split command exceeded 30 seconds.'
        }
        [IO.File]::WriteAllText("$LogStem.stdout.txt", $stdout.GetAwaiter().GetResult())
        [IO.File]::WriteAllText("$LogStem.stderr.txt", $stderr.GetAwaiter().GetResult())
        return $process.ExitCode
    }
    finally { $process.Dispose() }
}

$controlRoot = Join-Path $EvidenceDirectory 'control'
[IO.Directory]::CreateDirectory($controlRoot) | Out-Null
$controlSource = Join-Path $controlRoot 'source.pdf'
New-TestPdf $controlSource
$controlHash = (Get-FileHash -LiteralPath $controlSource -Algorithm SHA256).Hash
$controlCode = Invoke-Split $controlSource $controlRoot @('--every', '1', '--name', 'safe-{index}') (Join-Path $EvidenceDirectory 'control')
if ($controlCode -ne 0 -or (Get-FileHash -LiteralPath $controlSource -Algorithm SHA256).Hash -ne $controlHash `
    -or @(Get-ChildItem -LiteralPath $controlRoot -Filter 'safe-*.pdf').Count -ne 3) {
    throw 'The noncolliding control must split three pages without changing its source.'
}

$caseRoot = Join-Path $EvidenceDirectory 'source-collision'
[IO.Directory]::CreateDirectory($caseRoot) | Out-Null
$source = Join-Path $caseRoot 'source.pdf'
New-TestPdf $source
$before = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$code = Invoke-Split $source $caseRoot @('--every', '1', '--name', '{name}') (Join-Path $EvidenceDirectory 'source-collision')
$after = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$result = [pscustomobject]@{
    Case = 'source-collision'
    ExitCode = $code
    SourceUnchanged = $before -eq $after
    NoPartialOutputs = @(Get-ChildItem -LiteralPath $caseRoot -File).Count -eq 1
    ExeSha256 = (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash
    AssemblySha256 = (Get-FileHash -LiteralPath ([IO.Path]::ChangeExtension($ExePath, '.dll')) -Algorithm SHA256).Hash
}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'result.json') -Encoding UTF8
$result | Format-List
if ($code -eq 0 -or -not $result.SourceUnchanged -or -not $result.NoPartialOutputs) {
    throw 'Split must refuse a source collision without changing the source or publishing any parts.'
}

$caseRoot = Join-Path $EvidenceDirectory 'existing-later-output'
[IO.Directory]::CreateDirectory($caseRoot) | Out-Null
$source = Join-Path $caseRoot 'source.pdf'
New-TestPdf $source
$sentinel = Join-Path $caseRoot 'part-2.pdf'
[IO.File]::WriteAllText($sentinel, 'Existing destination must survive.')
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$sentinelHash = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
$code = Invoke-Split $source $caseRoot @('--every', '1', '--name', 'part-{index}') (Join-Path $EvidenceDirectory 'existing-later-output')
if ($code -eq 0 -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $sourceHash `
    -or (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -ne $sentinelHash `
    -or @(Get-ChildItem -LiteralPath $caseRoot -File -Force).Count -ne 2) {
    throw 'An existing later output must be detected before any part is published or replaced.'
}
Write-Output 'PASS: existing later output is preserved without partial publication.'

$code = Invoke-Split $source $caseRoot @('--every', '1', '--name', 'part-{index}', '--overwrite') (Join-Path $EvidenceDirectory 'explicit-overwrite')
if ($code -ne 0 -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $sourceHash `
    -or (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -eq $sentinelHash `
    -or @(Get-ChildItem -LiteralPath $caseRoot -File -Force).Count -ne 4) {
    throw 'Explicit overwrite must replace non-source outputs and preserve the source.'
}
Write-Output 'PASS: explicit overwrite replaces non-source outputs and preserves the source.'

foreach ($variant in 'case-difference', 'normalized-path', 'later-source') {
    $caseRoot = Join-Path $EvidenceDirectory $variant
    [IO.Directory]::CreateDirectory($caseRoot) | Out-Null
    $sourceName = if ($variant -eq 'later-source') { 'part-2.pdf' } else { 'source.pdf' }
    $source = Join-Path $caseRoot $sourceName
    New-TestPdf $source
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $template = if ($variant -eq 'case-difference') { 'SOURCE' } elseif ($variant -eq 'later-source') { 'part-{index}' } else { '{name}' }
    $destination = $caseRoot
    if ($variant -eq 'normalized-path') {
        [IO.Directory]::CreateDirectory((Join-Path $caseRoot 'child')) | Out-Null
        $destination = Join-Path $caseRoot 'child\..'
    }
    $code = Invoke-Split $source $destination @('--every', '1', '--name', $template, '--overwrite') (Join-Path $EvidenceDirectory $variant)
    if ($code -eq 0 -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $sourceHash `
        -or @(Get-ChildItem -LiteralPath $caseRoot -File -Force).Count -ne 1) {
        throw "Source collision variant failed: $variant"
    }
    Write-Output "PASS: $variant refuses publication even with --overwrite."
}

$caseRoot = Join-Path $EvidenceDirectory 'default-split'
[IO.Directory]::CreateDirectory($caseRoot) | Out-Null
$source = Join-Path $caseRoot 'source.pdf'
New-TestPdf $source
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$sentinel = Join-Path $caseRoot 'source-page-002.pdf'
[IO.File]::WriteAllText($sentinel, 'Existing default output must survive.')
$sentinelHash = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
$code = Invoke-Split $source $caseRoot @() (Join-Path $EvidenceDirectory 'default-refusal')
if ($code -eq 0 -or (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -ne $sentinelHash `
    -or @(Get-ChildItem -LiteralPath $caseRoot -File -Force).Count -ne 2) {
    throw 'Default splitting must refuse a later existing output before publishing any parts.'
}
$code = Invoke-Split $source $caseRoot @('--overwrite') (Join-Path $EvidenceDirectory 'default-overwrite')
if ($code -ne 0 -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $sourceHash `
    -or (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -eq $sentinelHash `
    -or @(Get-ChildItem -LiteralPath $caseRoot -File -Force).Count -ne 4) {
    throw 'Default splitting must support explicit overwrite without changing the source.'
}
Write-Output 'PASS: default splitting follows the same overwrite policy.'

$caseRoot = Join-Path $EvidenceDirectory 'locked-output'
[IO.Directory]::CreateDirectory($caseRoot) | Out-Null
$source = Join-Path $caseRoot 'source.pdf'
New-TestPdf $source
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$sentinel = Join-Path $caseRoot 'part-1.pdf'
[IO.File]::WriteAllText($sentinel, 'Locked destination must survive.')
$sentinelHash = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
$held = [IO.File]::Open($sentinel, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
try {
    $code = Invoke-Split $source $caseRoot @('--every', '1', '--name', 'part-{index}', '--overwrite') (Join-Path $EvidenceDirectory 'locked-output')
}
finally { $held.Dispose() }
if ($code -eq 0 -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $sourceHash `
    -or (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -ne $sentinelHash `
    -or @(Get-ChildItem -LiteralPath $caseRoot -File -Force).Count -ne 2) {
    throw 'A failed replacement must preserve existing bytes and remove owned temporary files.'
}
Write-Output 'PASS: failed replacement preserves existing bytes and cleans temporary files.'

$result | Add-Member -NotePropertyName AllCasesPassed -NotePropertyValue $true
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'result.json') -Encoding UTF8
