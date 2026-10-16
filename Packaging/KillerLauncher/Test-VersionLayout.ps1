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
$application = New-Object Windows.Application
$application.ShutdownMode = 'OnExplicitShutdown'
$window = $null
$count = 0
try {
    $window = $constructor.Invoke(@())
    $versionLine = $window.FindName('SetupVersionLabel')
    $setupLabel = $window.FindName('SetupLabel')
    $setupBox = $setupLabel.Parent
    $footerBox = $window.FindName('FooterBox')
    $header = $versionLine.Parent
    $sidebar = $header.Parent
    $columns = $sidebar.Parent.Parent.Parent
    $tagline = $sidebar.Children | Where-Object { $_ -is [Windows.Controls.TextBlock] -and [Windows.Controls.Grid]::GetRow($_) -eq 2 }
    # Repeat widths on the same control to catch stale resize state.
    foreach ($width in @(90, 115, 180, 115)) {
        $columns.ColumnDefinitions[0].Width = New-Object Windows.GridLength ($width + 37)
        foreach ($version in @('1.9.0', '1.9.100')) {
            # The optional link arguments stay null, so the version renders as plain text.
            $setVersion.Invoke($versionLine, @($version, $null, $null)) | Out-Null
            $window.Content.Measure((New-Object Windows.Size 650,([double]::PositiveInfinity)))
            $window.Content.Arrange((New-Object Windows.Rect 0,0,650,([Math]::Max(390,$window.Content.DesiredSize.Height))))
            $window.Content.UpdateLayout()
            $case = "$width / $version"
            $line = $versionLine.Child
            if ($line -isnot [Windows.Controls.TextBlock] -or $line.Text -cne $version) { throw "Incomplete version or extra label: $case" }
            if ($line.TextWrapping -ne 'NoWrap' -or $line.Text.Contains([char]10) -or $line.TextAlignment -ne 'Center') {
                throw "Wrapped or uncentered version: $case"
            }
            $measure = New-Object Windows.Controls.TextBlock
            $measure.FontFamily = $line.FontFamily
            $measure.FontSize = $line.FontSize
            $measure.Text = $line.Text
            [Windows.Media.TextOptions]::SetTextFormattingMode($measure, [Windows.Media.TextOptions]::GetTextFormattingMode($line))
            $measure.Measure((New-Object Windows.Size ([double]::PositiveInfinity),([double]::PositiveInfinity)))
            if ($measure.DesiredSize.Width -gt $line.ActualWidth + 1 -or $measure.DesiredSize.Height -gt $line.ActualHeight + 1) {
                throw "Clipped version: $case"
            }
            if ($versionLine.ActualHeight -gt $header.ActualHeight) { throw "Clipped parent: $case" }
            if ([string]::IsNullOrWhiteSpace($setupLabel.Text) -or $setupLabel.TextWrapping -ne 'NoWrap') { throw "Missing or wrapped Setup line: $case" }
            $versionBottom = $versionLine.TranslatePoint((New-Object Windows.Point 0,$versionLine.ActualHeight), $sidebar).Y
            $setupTop = $setupBox.TranslatePoint((New-Object Windows.Point 0,0), $sidebar).Y
            if ($setupTop -lt $versionBottom) { throw "Setup line is not below the version: $case" }
            if ($setupBox.ActualWidth -gt $width + 1) { throw "Setup line wider than the sidebar: $case" }
            if ($footerBox.ActualWidth -gt $width + 1) { throw "Copyright line wider than the sidebar: $case" }
            $bottom = $setupBox.TranslatePoint((New-Object Windows.Point 0,$setupBox.ActualHeight), $sidebar).Y
            $top = $tagline.TranslatePoint((New-Object Windows.Point 0,0), $sidebar).Y
            if ($bottom -gt $top) { throw "Tagline overlap: $case" }
            $count++
        }
    }
} finally {
    if ($window) { $window.Close() }
    $application.Shutdown()
}
"Installer version layout passed: $count offscreen cases with the complete version centered and the Setup line below it."
