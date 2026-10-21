#Requires -Version 7
<#
.SYNOPSIS
    Runs locally what CI checks on a release candidate, so a branch can be verified before it
    is pushed: Release build, both test suites, the synthetic PDF fixture, website shortcut data,
    translation parity and the package vulnerability audit.
.DESCRIPTION
    Changes nothing outside bin/obj and the results folder. It does not sign, push, tag or
    publish. Exit code 0 means every check passed.
.PARAMETER ResultsDirectory
    Where logs and test results are written. Defaults to a fresh folder under the system temp.
#>
param(
    [string]$ResultsDirectory = (Join-Path ([IO.Path]::GetTempPath()) ("KillerPDF-candidate-" + (Get-Date -Format 'yyyyMMdd-HHmmss')))
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Force $ResultsDirectory | Out-Null
if (!$env:WINDIR) { $env:WINDIR = $env:SYSTEMROOT }   # WPF FontCache needs it even in tests
$failures = [System.Collections.Generic.List[string]]::new()

function Step([string]$Name, [scriptblock]$Body) {
    Write-Host "`n==> $Name" -ForegroundColor Cyan
    $log = Join-Path $ResultsDirectory (($Name -replace '[^A-Za-z0-9]+', '-').Trim('-').ToLowerInvariant() + '.log')
    try {
        & $Body *>&1 | Tee-Object -FilePath $log | Out-Null
        if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw "exit code $LASTEXITCODE" }
        Write-Host "    passed" -ForegroundColor Green
    }
    catch {
        Write-Host "    FAILED: $($_.Exception.Message) (see $log)" -ForegroundColor Red
        $failures.Add($Name)
    }
    $global:LASTEXITCODE = 0
}

Push-Location $root
try {
    Step 'Build application (Release)' {
        dotnet build KillerPDF.csproj --configuration Release --verbosity minimal -nodeReuse:false
    }
    Step 'Website shortcut data matches the app' {
        dotnet run --project tools/KillerPDF.ShortcutSite/KillerPDF.ShortcutSite.csproj --configuration Release -- $root --check
    }
    Step 'Application tests' {
        dotnet test KillerPDF.Tests/KillerPDF.Tests.csproj --configuration Release -nodeReuse:false `
            --logger 'trx;LogFileName=app-tests.trx' --results-directory $ResultsDirectory
    }
    Step 'Engine tests' {
        dotnet test engine/KillerPdf.Engine.Tests/KillerPdf.Engine.Tests.csproj --configuration Release -nodeReuse:false `
            --logger 'trx;LogFileName=engine-tests.trx' --results-directory $ResultsDirectory
    }
    Step 'Synthetic PDF fixture without a window' {
        dotnet build tests/KillerPDF.UiSmoke/KillerPDF.UiSmoke.csproj --configuration Release --verbosity minimal -nodeReuse:false
        if ($LASTEXITCODE -ne 0) { return }
        dotnet run --project tests/KillerPDF.UiSmoke/KillerPDF.UiSmoke.csproj --configuration Release --no-build -- `
            --verify-fixture bin/Release/net10.0-windows/KillerPDF.dll (Join-Path $ResultsDirectory 'fixture')
    }
    Step 'Translation parity' {
        function Read-StringMap([string]$Path) {
            [xml]$document = Get-Content -Path $Path -Raw
            $map = @{}
            foreach ($node in $document.ResourceDictionary.ChildNodes) {
                if ($node.NodeType -ne [System.Xml.XmlNodeType]::Element) { continue }
                $key = $node.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
                if ($key) { $map[$key] = [string]$node.InnerText }
            }
            return $map
        }
        $english = Read-StringMap (Join-Path $root 'Strings\en-US.xaml')
        foreach ($file in Get-ChildItem (Join-Path $root 'Strings') -Filter '*.xaml') {
            if ($file.Name -eq 'en-US.xaml') { continue }
            $local = Read-StringMap $file.FullName
            $missing = @($english.Keys | Where-Object { -not $local.ContainsKey($_) })
            $extra = @($local.Keys | Where-Object { -not $english.ContainsKey($_) })
            $empty = @($local.Keys | Where-Object { [string]::IsNullOrWhiteSpace($local[$_]) })
            $placeholders = @($english.Keys | Where-Object {
                $local.ContainsKey($_) -and
                (([regex]::Matches($english[$_], '\{\d+(?::[^}]*)?\}') | ForEach-Object Value | Sort-Object) -join '|') -ne
                (([regex]::Matches($local[$_], '\{\d+(?::[^}]*)?\}') | ForEach-Object Value | Sort-Object) -join '|')
            })
            if ($missing.Count -or $extra.Count -or $empty.Count -or $placeholders.Count) {
                throw "$($file.Name): missing $($missing.Count), extra $($extra.Count), empty $($empty.Count), placeholder mismatches $($placeholders.Count)"
            }
        }
        "$($english.Count) keys match across all locales"
    }
    Step 'Package vulnerability audit' {
        foreach ($project in 'KillerPDF.csproj', 'engine/KillerPdf.Engine/KillerPdf.Engine.csproj') {
            $report = dotnet list $project package --vulnerable --include-transitive 2>&1
            $report
            if ($LASTEXITCODE -ne 0) { throw "The audit could not run for $project." }
            if ($report -match 'has the following vulnerable packages') { throw "Vulnerable packages in $project." }
        }
    }
}
finally { Pop-Location }

Write-Host "`nResults: $ResultsDirectory"
if ($failures.Count) {
    Write-Host "FAILED: $($failures -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host 'All candidate checks passed.' -ForegroundColor Green
exit 0
