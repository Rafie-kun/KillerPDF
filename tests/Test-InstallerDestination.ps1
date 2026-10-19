param(
    [Parameter(Mandatory=$true)][string]$Launcher,
    [Parameter(Mandatory=$true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($root) | Out-Null
$assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($Launcher))
$type = $assembly.GetType('KillerLauncher.Program', $true)
$validate = $type.GetMethod('ValidateInstallDestination', [Reflection.BindingFlags]'Static,NonPublic')
if (-not $validate) { throw 'The production destination validation entry is missing.' }
[string]$folder = [IO.Path]::Combine($root, ('work-folder-' + [Guid]::NewGuid().ToString('N')))
[IO.Directory]::CreateDirectory($folder) | Out-Null
$sentinel = Join-Path $folder 'customer-notes.txt'
[IO.File]::WriteAllText($sentinel, 'Keep these unrelated notes.')
$before = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
$rejected = $false
try { $null = $validate.Invoke($null, @($folder)) }
catch {
    $failure = $_.Exception
    while ($failure.InnerException) { $failure = $failure.InnerException }
    if ($failure -isnot [InvalidOperationException]) { throw }
    $rejected = $true
}
if ((Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash -ne $before) { throw 'Sentinel changed.' }
if (-not $rejected) { throw 'FAIL: A nonempty unrelated work folder was accepted as an installation destination.' }
Write-Output 'PASS: Production destination validation rejected the unrelated work folder and preserved its sentinel.'

function Assert-Destination([string]$Path, [bool]$Allowed) {
    $accepted = $true
    try { $null = $validate.Invoke($null, @($Path)) }
    catch {
        $failure = $_.Exception
        while ($failure.InnerException) { $failure = $failure.InnerException }
        if ($failure -isnot [InvalidOperationException] -and $failure -isnot [IO.InvalidDataException]) { throw }
        $accepted = $false
    }
    if ($accepted -ne $Allowed) { throw ('Unexpected destination verdict: ' + $Path) }
    Write-Output ('PASS: ' + [IO.Path]::GetFileName($Path))
}
function New-Owned([string]$Name) {
    [string]$path = [IO.Path]::Combine($root, ($Name + '-' + [Guid]::NewGuid().ToString('N')))
    [IO.Directory]::CreateDirectory($path) | Out-Null
    [string]$exe = [IO.Path]::Combine($path, 'KillerPDF.App.exe')
    [IO.File]::WriteAllBytes($exe, [byte[]](1,2,3,4))
    [string]$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    [IO.File]::WriteAllText([IO.Path]::Combine($path, 'payload.manifest'), ($hash + "`t4`tKillerPDF.App.exe`n"))
    return $path
}
Assert-Destination ([IO.Path]::Combine($root, 'missing')) $true
[string]$empty = [IO.Path]::Combine($root, 'empty')
[IO.Directory]::CreateDirectory($empty) | Out-Null
Assert-Destination $empty $true
[string]$owned = New-Owned 'owned'
Assert-Destination $owned $true
[IO.File]::WriteAllText([IO.Path]::Combine($owned, 'KillerPDF.cmd'), "@`"%~dp0KillerPDF.App.exe`" %*`r`n@exit /b %ERRORLEVEL%`r`n")
Assert-Destination $owned $true
[IO.File]::WriteAllText([IO.Path]::Combine($owned, 'customer-notes.txt'), 'Preserve this file.')
Assert-Destination $owned $false
[string]$tampered = New-Owned 'tampered-payload'
[IO.File]::WriteAllBytes([IO.Path]::Combine($tampered, 'KillerPDF.App.exe'), [byte[]](4,3,2,1))
Assert-Destination $tampered $false
[string]$wrapper = New-Owned 'unowned-wrapper'
[IO.File]::WriteAllText([IO.Path]::Combine($wrapper, 'KillerPDF.cmd'), 'Customer command script')
Assert-Destination $wrapper $false
[string]$badManifest = New-Owned 'invalid-manifest'
[IO.File]::WriteAllText([IO.Path]::Combine($badManifest, 'payload.manifest'), 'not a manifest')
Assert-Destination $badManifest $false
[string]$emptyChild = New-Owned 'unowned-empty-directory'
[IO.Directory]::CreateDirectory([IO.Path]::Combine($emptyChild, 'customer-folder')) | Out-Null
Assert-Destination $emptyChild $false
[string]$linked = New-Owned 'junction-parent'
[string]$junction = [IO.Path]::Combine($root, ('junction-' + [Guid]::NewGuid().ToString('N')))
New-Item -ItemType Junction -Path $junction -Target $linked | Out-Null
Assert-Destination $junction $false
Assert-Destination ([IO.Path]::Combine($junction, 'new-child')) $false
Write-Output 'PASS: 12 destination checks completed without installation, registration, or process shutdown.'
