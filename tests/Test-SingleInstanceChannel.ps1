param(
    [Parameter(Mandatory=$true)][string]$WorkDirectory,
    [ValidateSet('sessions','missing')][string]$Mode = 'sessions'
)
$ErrorActionPreference = 'Stop'
$work = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { throw 'Use a new work directory under scratch.' }
$null = New-Item -ItemType Directory -Path $work
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../Services/SingleInstanceChannel.cs'))
$project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><Compile Include="' + $source + '" Link="SingleInstanceChannel.cs" /></ItemGroup></Project>'
[IO.File]::WriteAllText((Join-Path $work 'Probe.csproj'),$project)
[IO.File]::Copy((Join-Path $PSScriptRoot 'KillerPDF.BoundarySmoke/SingleInstanceProbe.cs.txt'),(Join-Path $work 'Program.cs'))
& dotnet build (Join-Path $work 'Probe.csproj') -v:q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $work 'bin/Debug/net10.0-windows/Probe.exe') $Mode
exit $LASTEXITCODE
