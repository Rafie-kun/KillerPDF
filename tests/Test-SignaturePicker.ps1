param(
    [Parameter(Mandatory=$true)][string]$AssemblyPath,
    [Parameter(Mandatory=$true)][string]$WorkDirectory,
    [ValidateSet('dismiss','switch','direct','create-cancel','pane','orphan')][string]$Mode = 'dismiss'
)
$ErrorActionPreference = 'Stop'
$work = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { throw 'Use a new work directory under scratch.' }
$null = New-Item -ItemType Directory -Path $work
$project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup></Project>'
[IO.File]::WriteAllText((Join-Path $work 'Probe.csproj'),$project)
[IO.File]::Copy((Join-Path $PSScriptRoot 'KillerPDF.UiSmoke/SignaturePickerProbe.cs.txt'),(Join-Path $work 'Program.cs'))
$appXaml = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../App.xaml'))
& dotnet run --project (Join-Path $work 'Probe.csproj') -- ([IO.Path]::GetFullPath($AssemblyPath)) $appXaml (Join-Path $work 'evidence') $Mode --startup-smoke
exit $LASTEXITCODE
