[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ReferenceExe,
    [Parameter(Mandatory)][string]$ComparisonExe,
    [Parameter(Mandatory)][string]$InputDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(16,8192)][int]$Size = 2048,
    [ValidateRange(1,1000)][int]$Pages = 3,
    [ValidateRange(0,64)][int]$ComparisonParallelism = 0,
    [switch]$Measure
)
$ErrorActionPreference = 'Stop'
$reference = (Resolve-Path -LiteralPath $ReferenceExe).Path
$comparison = (Resolve-Path -LiteralPath $ComparisonExe).Path
$inputRoot = (Resolve-Path -LiteralPath $InputDirectory).Path
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputRoot) { throw 'Use a new output directory to preserve earlier evidence.' }
New-Item -ItemType Directory -Path $outputRoot | Out-Null
function Quote-Argument([string]$Value) {
    '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
$inputs = @{}
function Backend-Identity([string]$Exe) {
    $folder = Split-Path -Parent $Exe
    $components = @{}
    Get-ChildItem -LiteralPath $folder -File -Recurse | Where-Object { $_.Extension -in '.exe','.dll','.json' } | ForEach-Object {
        $components[$_.FullName.Substring($folder.Length).TrimStart('\')] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    [ordered]@{Path=$Exe; Sha256=(Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash; Version=(Get-Item -LiteralPath $Exe).VersionInfo.FileVersion; Components=$components}
}
$referenceIdentity = Backend-Identity $reference
$comparisonIdentity = Backend-Identity $comparison
Get-ChildItem -LiteralPath $inputRoot -Filter '*.pdf' -Recurse -File | ForEach-Object {
    $inputs[$_.FullName.Substring($inputRoot.Length).TrimStart('\')] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}
$results = @()
$seen = @{}
$statuses = @{}
$order = if ($Measure) { @('reference','comparison','comparison','reference','comparison','reference','reference','comparison') } else { @('reference','comparison','comparison','reference') }
$sequence = if ($Measure) { @('reference','comparison') + $order } else { $order }
for ($index = 0; $index -lt $sequence.Count; $index++) {
    $backend = $sequence[$index]
    $exe = if ($backend -eq 'reference') { $reference } else { $comparison }
    $label = '{0:D2}-{1}' -f $index, $backend
    $folder = Join-Path $outputRoot $label
    $log = Join-Path $outputRoot ($label + '.csv')
    $arguments = @('--batch-render', $inputRoot, $folder, '--size', "$Size", '--pages', "$Pages", '--quiet', '--log', $log)
    if ($backend -eq 'reference') { $arguments += @('--parallel', '1') }
    elseif ($ComparisonParallelism -gt 0) { $arguments += @('--parallel', "$ComparisonParallelism") }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $exe
    $info.Arguments = ($arguments | ForEach-Object { Quote-Argument $_ }) -join ' '
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $process = [Diagnostics.Process]::Start($info)
    $peak = 0L
    while (-not $process.WaitForExit(20)) {
        $process.Refresh()
        $peak = [Math]::Max($peak, $process.PeakWorkingSet64)
    }
    $process.Refresh()
    $peak = [Math]::Max($peak, $process.PeakWorkingSet64)
    $watch.Stop()
    $evidenceWatch = [Diagnostics.Stopwatch]::StartNew()
    if ($process.ExitCode -gt 1) { throw "Backend $backend exited with $($process.ExitCode). Retained output: $folder" }
    $pngs = @{}
    Get-ChildItem -LiteralPath $folder -Filter '*.png' -Recurse -File | ForEach-Object {
        $pngs[$_.FullName.Substring($folder.Length).TrimStart('\')] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    $rows = @(Import-Csv -LiteralPath $log)
    $failedPages = @($rows | Where-Object { $_.Status -eq 'FAIL' })
    $unrenderableRows = @($rows | Where-Object { $_.Status -ne 'OK' })
    $status = @($rows | Select-Object File,Page,Width,Height,Status,Detail) | ConvertTo-Json -Depth 5 -Compress
    if ($statuses.ContainsKey($backend) -and $statuses[$backend] -ne $status) { throw "Backend $backend changed page statuses or diagnostics." }
    $statuses[$backend] = $status
    # Each backend must remain deterministic. Cross-backend differences require source and visual review.
    if ($seen.ContainsKey($backend)) {
        $old = $seen[$backend]
        if ($old.Count -ne $pngs.Count) { throw "Backend $backend changed its output count." }
        foreach ($key in $pngs.Keys) { if ($old[$key] -ne $pngs[$key]) { throw "Backend $backend changed pixels: $key" } }
    } else { $seen[$backend] = $pngs }
    $results += [pscustomobject]@{ Backend=$backend; Warmup=($Measure -and $index -lt 2); PngCount=$pngs.Count; FailedPages=$failedPages; ExitCode=$process.ExitCode;
        UnrenderableRows=$unrenderableRows; RepresentedFiles=@($rows.File | Sort-Object -Unique).Count;
        PeakWorkingSetBytes=$peak; WallMilliseconds=$watch.Elapsed.TotalMilliseconds;
        EvidenceMilliseconds=$evidenceWatch.Elapsed.TotalMilliseconds; Csv=$log; Output=$folder }
}
$differences = @()
foreach ($key in @(@($seen['reference'].Keys) + @($seen['comparison'].Keys) | Sort-Object -Unique)) {
    if ($seen['reference'][$key] -ne $seen['comparison'][$key]) { $differences += $key }
}
$manifest = [ordered]@{
    Reference=$referenceIdentity
    Comparison=$comparisonIdentity
    Inputs=$inputs; Size=$Size; Pages=$Pages; Parallel=1; ComparisonParallelism=$ComparisonParallelism;
    Measured=[bool]$Measure; Runs=$results;
    TimingScope='Process launch through exit. PNG hashing and evidence collection are measured separately. This does not measure viewer interaction latency.';
    DifferentImages=$differences; VisualReviewComplete=($differences.Count -eq 0 -and $seen['reference'].Count -gt 0 -and
        $seen['comparison'].Count -gt 0 -and @($results | Where-Object { $_.ExitCode -ne 0 }).Count -eq 0); AutomaticBackendSwitch=$false
}
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $outputRoot 'comparison.json') -Encoding UTF8
Write-Output "Backend comparison retained: $outputRoot; different images: $($differences.Count)"
