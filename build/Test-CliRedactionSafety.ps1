#Requires -Version 5.1
param(
    [Parameter(Mandatory = $true)][string]$ExePath,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Use a new evidence directory.' }
[IO.Directory]::CreateDirectory($EvidenceDirectory) | Out-Null

function New-MarkedPdf([string]$Path, [bool]$AllBlocked) {
    $text = 'BT /F1 12 Tf 30 50 Td (SECRET) Tj ET'
    $drawing = '1 0 0 rg 0 0 100 100 re f ' + $text
    $second = if ($AllBlocked) { $drawing } else { $text }
    $objects = @(
        '<< /Type /Catalog /Pages 2 0 R >>',
        '<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>',
        '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 7 0 R >> >> /Contents 5 0 R /Annots [8 0 R] >>',
        '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /Font << /F1 7 0 R >> >> /Contents 6 0 R /Annots [9 0 R] >>',
        "<< /Length $($drawing.Length) >>`nstream`n$drawing`nendstream",
        "<< /Length $($second.Length) >>`nstream`n$second`nendstream",
        '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>',
        '<< /Type /Annot /Subtype /Redact /Rect [25 25 90 75] >>',
        '<< /Type /Annot /Subtype /Redact /Rect [25 25 90 75] >>'
    )
    $pdf = [Text.StringBuilder]::new("%PDF-1.7`n")
    $offsets = @()
    for ($i = 0; $i -lt $objects.Count; $i++) {
        $offsets += $pdf.Length
        [void]$pdf.Append("$($i + 1) 0 obj`n$($objects[$i])`nendobj`n")
    }
    $xref = $pdf.Length
    [void]$pdf.Append("xref`n0 10`n0000000000 65535 f `n")
    foreach ($offset in $offsets) {
        [void]$pdf.Append($offset.ToString('0000000000', [Globalization.CultureInfo]::InvariantCulture))
        [void]$pdf.Append(" 00000 n `n")
    }
    [void]$pdf.Append("trailer`n<< /Size 10 /Root 1 0 R >>`nstartxref`n$xref`n%%EOF`n")
    [IO.File]::WriteAllBytes($Path, [Text.Encoding]::ASCII.GetBytes($pdf.ToString()))
}

function Invoke-Headless([string[]]$Arguments, [string]$LogStem) {
    foreach ($argument in $Arguments) {
        if ($argument.Contains('"') -or $argument.EndsWith('\')) { throw 'Unsupported test argument.' }
    }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $ExePath
    $start.Arguments = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
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
            throw 'The headless redaction command exceeded 30 seconds.'
        }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText("$LogStem.txt", $output)
        return [pscustomobject]@{ Code = $process.ExitCode; Output = $output }
    }
    finally { $process.Dispose() }
}

$macro = Join-Path $EvidenceDirectory 'redact.json'
[IO.File]::WriteAllText($macro, '{"version":1,"name":"Redact acceptance","steps":[{"operation":"redact"},{"operation":"save"}]}')
$results = @()
foreach ($mode in 'redact', 'macro') {
    foreach ($allBlocked in $false, $true) {
        foreach ($existing in $false, $true) {
            $name = "$mode-all$allBlocked-existing$existing"
            $source = Join-Path $EvidenceDirectory "$name-source.pdf"
            $destination = Join-Path $EvidenceDirectory "$name-output.pdf"
            New-MarkedPdf $source $allBlocked
            $sourceHash = (Get-FileHash -LiteralPath $source).Hash
            $before = $null
            if ($existing) {
                [IO.File]::WriteAllText($destination, 'Preserve this existing destination.')
                $before = (Get-FileHash -LiteralPath $destination).Hash
            }
            $arguments = if ($mode -eq 'redact') { @('--redact', $source, $destination, '--marked') }
                else { @('--macro', $source, $macro, $destination, '--json') }
            $run = Invoke-Headless $arguments (Join-Path $EvidenceDirectory $name)
            $preserved = if ($existing) { (Get-FileHash -LiteralPath $destination).Hash -eq $before }
                else { -not (Test-Path -LiteralPath $destination) }
            $failedReport = $mode -ne 'macro' -or ($run.Output -match '"succeeded":\s*false' `
                -and $run.Output -match '"failedStepIndex":\s*0' -and $run.Output -match '"failedOperation":\s*4')
            $results += [pscustomobject]@{
                Case = $name; ExitCode = $run.Code
                Passed = $run.Code -eq 1 -and $preserved -and $failedReport -and $run.Output -match 'pages? 1' `
                    -and (Get-FileHash -LiteralPath $source).Hash -eq $sourceHash
            }
            $approved = Invoke-Headless ($arguments + '--rasterize') (Join-Path $EvidenceDirectory "$name-approved")
            $results += [pscustomobject]@{
                Case = "$name-approved"; ExitCode = $approved.Code
                Passed = $approved.Code -eq 0 -and (Test-Path -LiteralPath $destination) `
                    -and (Get-FileHash -LiteralPath $source).Hash -eq $sourceHash
            }
        }
    }
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'results.json') -Encoding UTF8
$results | Format-Table -AutoSize
if (@($results | Where-Object { -not $_.Passed }).Count) { throw 'Redaction acceptance failed; see the case logs.' }
