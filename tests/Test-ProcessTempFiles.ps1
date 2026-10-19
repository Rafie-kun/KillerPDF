param([Parameter(Mandatory=$true)][string]$WorkDirectory)
$ErrorActionPreference = 'Stop'
$work = [IO.Path]::GetFullPath($WorkDirectory)
if (Test-Path -LiteralPath $work) { throw 'Use a new work directory under scratch.' }
$null = New-Item -ItemType Directory -Path $work
$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../Services/ProcessTempFiles.cs'))
$project = '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup><ItemGroup><Compile Include="' + $source + '" Link="ProcessTempFiles.cs" /></ItemGroup></Project>'
[IO.File]::WriteAllText((Join-Path $work 'Probe.csproj'),$project)
[IO.File]::Copy((Join-Path $PSScriptRoot 'KillerPDF.BoundarySmoke/ProcessTempProbe.cs.txt'),(Join-Path $work 'Program.cs'))
& dotnet build (Join-Path $work 'Probe.csproj') -v:q
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $work 'bin/Debug/net10.0-windows/Probe.exe') verify (Join-Path $work 'data')
exit $LASTEXITCODE
