param(
    [Parameter(Mandatory)][string]$AppPath,
    [Parameter(Mandatory)][string]$EvidenceRoot
)
# Checks that single-output CLI commands refuse to write over their own input and publish
# their output atomically. Runs the built KillerPDF.dll through dotnet against synthetic PDFs.

$ErrorActionPreference = 'Stop'
$runRoot = Join-Path $EvidenceRoot ('cli-output-' + [guid]::NewGuid().ToString('N'))
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
    if (!$process.WaitForExit(60000)) { $process.Kill(); throw 'CLI timed out.' }
    $result = [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult() }
    $process.Dispose()
    return $result
}

$failures = @()
function Check([string]$Name, [bool]$Condition, [string]$Detail) {
    if ($Condition) { Write-Output "PASS $Name" } else { Write-Output "FAIL $Name : $Detail"; $script:failures += $Name }
}

$source = Join-Path $runRoot 'in\source.pdf'
New-SyntheticPdf $source 'KEEP-ME'
$before = [IO.File]::ReadAllBytes($source)

# 1. Extract pages onto the input itself is refused and the input is untouched.
$r = Invoke-Cli @('--extract-pages', $source, '1', $source)
Check 'extract refuses same path' ($r.ExitCode -eq 2) "exit $($r.ExitCode)"
Check 'extract leaves input intact' ([Convert]::ToBase64String([IO.File]::ReadAllBytes($source)) -eq [Convert]::ToBase64String($before)) 'input changed'

# 2. Same file reached through a different spelling is still refused.
$mixed = Join-Path (Split-Path $source) 'SOURCE.PDF'
$r = Invoke-Cli @('--extract-pages', $source, '1', $mixed)
Check 'extract refuses other casing' ($r.ExitCode -eq 2) $r.Output

# 3. Normal extraction still works and leaves no temp file behind.
$out = Join-Path $runRoot 'out\page1.pdf'
$r = Invoke-Cli @('--extract-pages', $source, '1', $out)
Check 'extract succeeds' ($r.ExitCode -eq 0 -and (Test-Path -LiteralPath $out)) $r.Output
Check 'extract leaves no temp files' (@(Get-ChildItem (Split-Path $out) -Force | Where-Object { $_.Name -like '.*.tmp' }).Count -eq 0) 'temp file left'

# 4. Export to text onto the input is refused.
$r = Invoke-Cli @('--export', $source, $source, '--format', 'txt')
Check 'export refuses same path' ($r.ExitCode -eq 2 -and (Test-Path -LiteralPath $source)) $r.Output
Check 'export leaves input intact' ([Convert]::ToBase64String([IO.File]::ReadAllBytes($source)) -eq [Convert]::ToBase64String($before)) 'input changed'

# 5. Export form data onto the input is refused.
$r = Invoke-Cli @('--export-form-data', $source, $source)
Check 'export-form-data refuses same path' ($r.ExitCode -eq 2) $r.Output

if ($failures.Count -gt 0) { Write-Output "FAILED: $($failures -join ', ')"; exit 1 }
Write-Output "All CLI output safety checks passed. Evidence: $runRoot"
exit 0
