# Run in a fresh Windows PowerShell 5.1 process with -Sta, passing the built launcher exe.
# Measures the actual installer offscreen without showing it or starting installation.
param([Parameter(Mandatory = $true)][string]$AssemblyPath)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop' -or [Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    throw 'Use Windows PowerShell 5.1 with -Sta for the .NET Framework launcher.'
}
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath).Path)
$flags = [Reflection.BindingFlags]'Instance,NonPublic'
$constructor = $assembly.GetType('KillerLauncher.InstallerWizard', $true).GetConstructor($flags, $null, [Type[]]@(), $null)
$setVersion = $assembly.GetType('KillerLauncher.SetupVersionText', $true).GetMethod('SetVersion', $flags)
$getString = $assembly.GetType('KillerLauncher.LauncherStrings', $true).GetMethod('Get', [Reflection.BindingFlags]'Static,NonPublic')
$application = New-Object Windows.Application
$application.ShutdownMode = 'OnExplicitShutdown'
$originalCulture = [Threading.Thread]::CurrentThread.CurrentCulture
$originalUiCulture = [Threading.Thread]::CurrentThread.CurrentUICulture
$window = $null
$count = 0
try {
    $window = $constructor.Invoke(@())
    $versionLine = $window.FindName('SetupVersionLabel')
    $header = $versionLine.Parent
    $sidebar = $header.Parent
    $columns = $sidebar.Parent.Parent.Parent
    $tagline = $sidebar.Children | Where-Object { $_ -is [Windows.Controls.TextBlock] -and [Windows.Controls.Grid]::GetRow($_) -eq 2 }
    # Repeat both languages on the same control to catch stale order and resize state.
    foreach ($locale in @('en-US', 'it-IT', 'en-US', 'it-IT')) {
        [Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo($locale)
        [Threading.Thread]::CurrentThread.CurrentUICulture = [Threading.Thread]::CurrentThread.CurrentCulture
        $format = $getString.Invoke($null, @('SetupVersion'))
        $label = if ($locale -eq 'it-IT') { 'INSTALLAZIONE' } else { 'SETUP' }
        foreach ($width in @(90, 115, 180, 115)) {
            $columns.ColumnDefinitions[0].Width = New-Object Windows.GridLength ($width + 37)
            foreach ($version in @('1.9.0', '1.9.100')) {
                $setVersion.Invoke($versionLine, @($format, $version)) | Out-Null
                $window.Content.Measure((New-Object Windows.Size 650,([double]::PositiveInfinity)))
                $window.Content.Arrange((New-Object Windows.Rect 0,0,650,([Math]::Max(390,$window.Content.DesiredSize.Height))))
                $window.Content.UpdateLayout()
                $case = "$locale / $width / $version"
                $lines = $versionLine.Child.Children
                $expected = if ($locale -eq 'it-IT') { @($version, $label) } else { @($label, $version) }
                if ($lines.Count -ne 2 -or $lines[0].Text -cne $expected[0] -or $lines[1].Text -cne $expected[1]) {
                    throw "Incorrect complete number or localized order: $case"
                }
                foreach ($line in $lines) {
                    if ($line.TextWrapping -ne 'NoWrap' -or $line.Text.Contains([char]10) -or $line.TextAlignment -ne 'Center') {
                        throw "Wrapped or uncentered line: $case"
                    }
                    $measure = New-Object Windows.Controls.TextBlock
                    $measure.FontFamily = $line.FontFamily
                    $measure.FontSize = $line.FontSize
                    $measure.Text = $line.Text
                    [Windows.Media.TextOptions]::SetTextFormattingMode($measure, [Windows.Media.TextOptions]::GetTextFormattingMode($line))
                    $measure.Measure((New-Object Windows.Size ([double]::PositiveInfinity),([double]::PositiveInfinity)))
                    if ($measure.DesiredSize.Width -gt $line.ActualWidth + 1 -or $measure.DesiredSize.Height -gt $line.ActualHeight + 1) {
                        throw "Clipped line: $case"
                    }
                }
                if ($versionLine.Child.ActualHeight -gt $versionLine.ActualHeight + 1 -or $versionLine.ActualHeight -gt $header.ActualHeight) {
                    throw "Clipped parent: $case"
                }
                $bottom = $versionLine.TranslatePoint((New-Object Windows.Point 0,$versionLine.ActualHeight), $sidebar).Y
                $top = $tagline.TranslatePoint((New-Object Windows.Point 0,0), $sidebar).Y
                if ($bottom -gt $top) { throw "Tagline overlap: $case" }
                $count++
            }
        }
    }
} finally {
    if ($window) { $window.Close() }
    $application.Shutdown()
    [Threading.Thread]::CurrentThread.CurrentCulture = $originalCulture
    [Threading.Thread]::CurrentThread.CurrentUICulture = $originalUiCulture
}
"Installer version layout passed: $count offscreen cases, complete numbers and English/Italian order."
