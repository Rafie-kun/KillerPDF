param(
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$EvidenceRoot
)

$ErrorActionPreference = 'Stop'
$runRoot = Join-Path $EvidenceRoot ('bates-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($runRoot) | Out-Null

function New-SyntheticPdf([string]$Path, [string]$Label) {
    $contents = "BT /F1 12 Tf 72 720 Td ($Label) Tj ET"
    $objects = @(
        '<< /Type /Catalog /Pages 2 0 R >>',
        '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
        '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
        '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
        ("<< /Length " + $contents.Length + " >>`nstream`n" + $contents + "`nendstream")
    )
    $pdf = "%PDF-1.4`n"
    $offsets = @()
    for ($i = 0; $i -lt $objects.Count; $i++) {
        $offsets += $pdf.Length
        $pdf += "$($i + 1) 0 obj`n$($objects[$i])`nendobj`n"
    }
    $xref = $pdf.Length
    $pdf += "xref`n0 6`n0000000000 65535 f `n"
    foreach ($offset in $offsets) { $pdf += ('{0:d10} 00000 n ' -f $offset) + "`n" }
    $pdf += "trailer`n<< /Size 6 /Root 1 0 R >>`nstartxref`n$xref`n%%EOF`n"
    if (Test-Path -LiteralPath $Path) { throw "Fixture already exists: $Path" }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllBytes($Path, [Text.Encoding]::ASCII.GetBytes($pdf))
}

function Invoke-Cli([string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $allArguments = @([IO.Path]::GetFullPath($AppPath)) + $Arguments
    $start.Arguments = ($allArguments | ForEach-Object {
        '"' + [regex]::Replace([regex]::Replace($_, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
    }) -join ' '
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'CLI timed out.' }
    $result = [pscustomobject]@{
        ExitCode = $process.ExitCode
        Output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
    }
    $process.Dispose()
    return $result
}

$first = Join-Path $runRoot 'first/same.pdf'
$second = Join-Path $runRoot 'second/same.pdf'
$output = Join-Path $runRoot 'output'
New-SyntheticPdf $first 'FIRST-INPUT'
New-SyntheticPdf $second 'SECOND-INPUT'
$firstHash = (Get-FileHash -LiteralPath $first).Hash
$secondHash = (Get-FileHash -LiteralPath $second).Hash
$result = Invoke-Cli @('--bates', $output, $first, $second)
$count = if (Test-Path -LiteralPath $output) { @(Get-ChildItem -LiteralPath $output -File).Count } else { 0 }
Write-Output "Duplicate basenames: exit=$($result.ExitCode), outputs=$count"
Write-Output $result.Output
if ($result.ExitCode -ne 2 -or $count -ne 0) { throw 'Bates must reject duplicate destinations before publishing any output.' }
if ((Get-FileHash -LiteralPath $first).Hash -ne $firstHash -or
    (Get-FileHash -LiteralPath $second).Hash -ne $secondHash) { throw 'Bates modified an input.' }
Write-Output 'PASS duplicate basenames preserve inputs and publish nothing.'

$caseFirst = Join-Path $runRoot 'case-first/Same.pdf'
$caseSecond = Join-Path $runRoot 'case-second/SAME.PDF'
$caseOutput = Join-Path $runRoot 'case-output'
New-SyntheticPdf $caseFirst 'CASE-FIRST'
New-SyntheticPdf $caseSecond 'CASE-SECOND'
$result = Invoke-Cli @('--bates', $caseOutput, $caseFirst, $caseSecond)
if ($result.ExitCode -ne 2 -or (Test-Path -LiteralPath $caseOutput)) {
    throw 'Bates must detect Windows case-insensitive output collisions before creating the output folder.'
}
Write-Output 'PASS case-insensitive destination collision is refused.'

$overlapRoot = Join-Path $runRoot 'source-overlap'
$overlapFirst = Join-Path $overlapRoot 'one.pdf'
$overlapSecond = Join-Path $runRoot 'other-input/two.pdf'
New-SyntheticPdf $overlapFirst 'OVERLAP-FIRST'
New-SyntheticPdf $overlapSecond 'OVERLAP-SECOND'
$overlapFirstHash = (Get-FileHash -LiteralPath $overlapFirst).Hash
$overlapSecondHash = (Get-FileHash -LiteralPath $overlapSecond).Hash
$result = Invoke-Cli @('--bates', $overlapRoot, $overlapFirst, $overlapSecond)
if ($result.ExitCode -ne 2 -or (Test-Path -LiteralPath (Join-Path $overlapRoot 'two.pdf'))) {
    throw 'Bates must refuse the whole batch when any destination replaces an input.'
}
if ((Get-FileHash -LiteralPath $overlapFirst).Hash -ne $overlapFirstHash -or
    (Get-FileHash -LiteralPath $overlapSecond).Hash -ne $overlapSecondHash) {
    throw 'Bates changed an input in the rejected source-overlap batch.'
}
Write-Output 'PASS source overlap preserves both inputs and publishes no other output.'

$validFirst = Join-Path $runRoot 'valid-input/one.pdf'
$validSecond = Join-Path $runRoot 'valid-input/two.pdf'
$validOutput = Join-Path $runRoot 'valid-output'
New-SyntheticPdf $validFirst 'VALID-FIRST'
New-SyntheticPdf $validSecond 'VALID-SECOND'
$validFirstHash = (Get-FileHash -LiteralPath $validFirst).Hash
$validSecondHash = (Get-FileHash -LiteralPath $validSecond).Hash
$result = Invoke-Cli @('--bates', $validOutput, $validFirst, $validSecond)
if ($result.ExitCode -ne 0 -or @(Get-ChildItem -LiteralPath $validOutput -File).Count -ne 2) {
    throw 'Bates must still stamp distinct destinations successfully.'
}
$writtenFirst = Join-Path $validOutput 'one.pdf'
$writtenSecond = Join-Path $validOutput 'two.pdf'
$writtenFirstHash = (Get-FileHash -LiteralPath $writtenFirst).Hash
$writtenSecondHash = (Get-FileHash -LiteralPath $writtenSecond).Hash
if ($writtenFirstHash -eq $validFirstHash -or $writtenSecondHash -eq $validSecondHash) {
    throw 'Bates outputs must contain the stamp rather than unchanged input bytes.'
}
$result = Invoke-Cli @('--bates', $validOutput, $validFirst, $validSecond, '--start', '42')
if ($result.ExitCode -ne 0 -or
    (Get-FileHash -LiteralPath $writtenFirst).Hash -eq $writtenFirstHash -or
    (Get-FileHash -LiteralPath $writtenSecond).Hash -eq $writtenSecondHash) {
    throw 'Bates must retain its existing replacement behavior for unrelated output files.'
}
if ((Get-FileHash -LiteralPath $validFirst).Hash -ne $validFirstHash -or
    (Get-FileHash -LiteralPath $validSecond).Hash -ne $validSecondHash) {
    throw 'Bates changed an input during a successful batch.'
}
Write-Output 'PASS distinct outputs are stamped, can be replaced, and preserve inputs.'
