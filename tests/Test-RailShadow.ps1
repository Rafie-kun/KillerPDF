param(
    [Parameter(Mandatory=$true)][string]$AssemblyPath,
    [Parameter(Mandatory=$true)][string]$WorkDirectory,
    [ValidateSet('Dark','Light','SE98')][string]$Theme = 'Dark',
    [switch]$Split,
    [switch]$Expanded,
    [switch]$CycleThemes,
    [string]$BaselineDirectory
)
$ErrorActionPreference = 'Stop'
$work = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { throw 'Use a new work directory under scratch.' }
$null = New-Item -ItemType Directory -Path $work
$project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>'
[IO.File]::WriteAllText((Join-Path $work 'Probe.csproj'),$project)
[IO.File]::Copy((Join-Path $PSScriptRoot 'KillerPDF.UiSmoke/RailShadowProbe.cs.txt'),(Join-Path $work 'Program.cs'))
$appXaml = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../App.xaml'))
& dotnet build (Join-Path $work 'Probe.csproj')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$flags = @('--startup-smoke')
if ($Split) { $flags += 'split' }
if ($Expanded) { $flags += 'open' }
if ($CycleThemes) { $flags += 'cycle' }
foreach ($side in @('Top','Bottom')) {
    & dotnet (Join-Path $work 'bin/Debug/net10.0-windows/Probe.dll') ([IO.Path]::GetFullPath($AssemblyPath)) $appXaml (Join-Path $work $side) $side $Theme @flags
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if ($BaselineDirectory) {
        $before = Get-Content -Raw (Join-Path $BaselineDirectory "$side/result.json") | ConvertFrom-Json
        $after = Get-Content -Raw (Join-Path $work "$side/result.json") | ConvertFrom-Json
        if ($before.footerHash -ne $after.footerHash) { throw "Footer pixels changed at $side." }
    }
}
$top = Get-Content -Raw (Join-Path $work 'Top/result.json') | ConvertFrom-Json
$bottom = Get-Content -Raw (Join-Path $work 'Bottom/result.json') | ConvertFrom-Json
$difference = [Math]::Abs(($top.samples | Measure-Object -Average).Average - ($bottom.samples | Measure-Object -Average).Average)
Write-Output "Mean rail brightness difference: $difference (maximum 2)."
if ($difference -gt 2) { exit 1 }
exit 0
