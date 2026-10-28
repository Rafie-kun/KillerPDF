# Run with PowerShell 7 on .NET 10 after a normal or KillerPayloadBuild release build, on a lab VM:
# it opens the real main window. Loads the actual app resources and the kit theme and language
# menus (Controls/ThemePicker.cs, Controls/LanguageMenu.cs) with preferences redirected to owned evidence.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$AssemblyPath,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory
)
$ErrorActionPreference = 'Stop'
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') { throw 'Run with pwsh -Sta.' }
if ([Environment]::Version.Major -ne 10) { throw 'Use PowerShell on .NET 10.' }
$AssemblyPath = (Resolve-Path -LiteralPath $AssemblyPath).Path
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Use a fresh evidence directory.' }
$null = [IO.Directory]::CreateDirectory($EvidenceDirectory)
$appDirectory = Split-Path -Parent $AssemblyPath
$desktop = @(& dotnet --list-runtimes | ForEach-Object {
    if ($_ -match '^Microsoft\.WindowsDesktop\.App (10\.\d+\.\d+) \[(.+)\]$') {
        [pscustomobject]@{ Version = [version]$Matches[1]; Path = Join-Path $Matches[2] $Matches[1] }
    }
} | Sort-Object Version -Descending)[0].Path
$resolver = {
    param($context, $name)
    foreach ($root in @($appDirectory, $desktop)) {
        $candidate = Join-Path $root ($name.Name + '.dll')
        if (Test-Path -LiteralPath $candidate) { return $context.LoadFromAssemblyPath($candidate) }
    }
    return $null
}.GetNewClosure()
[Runtime.Loader.AssemblyLoadContext]::Default.add_Resolving($resolver)
foreach ($library in @('WindowsBase', 'PresentationCore', 'PresentationFramework')) {
    Add-Type -Path (Join-Path $desktop ($library + '.dll'))
}
$assembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
$static = [Reflection.BindingFlags]'Static,Public,NonPublic'
$instance = [Reflection.BindingFlags]'Instance,Public,NonPublic'
[Windows.Application].GetField('_resourceAssembly', $static).SetValue($null, $assembly)
$paths = $assembly.GetType('KillerPDF.Services.AppDataPaths', $true)
$paths.GetProperty('StartupSmokeRoot', $static).SetValue($null, (Join-Path $EvidenceDirectory 'data'))
$app = [Activator]::CreateInstance($assembly.GetType('KillerPDF.App', $true))
$app.ShutdownMode = [Windows.ShutdownMode]::OnExplicitShutdown
$app.InitializeComponent()
$manager = $assembly.GetType('KillerPDF.Services.ThemeManager', $true)
$themes = $assembly.GetType('KillerPDF.Services.Theme', $true)
$accents = $assembly.GetType('KillerPDF.Services.Accent', $true)
$getSetting = $assembly.GetType('KillerPDF.App').GetMethod('GetSetting', $static)
# The app's own startup order (App.xaml.cs): the kit hooks, then the theme, Film Grain and locale.
$assembly.GetType('KillerPDF.Services.AppTheme', $true).GetMethod('Configure', $static).Invoke($null, $null) | Out-Null
foreach ($service in @('ThemeManager', 'FilmGrain', 'LocaleManager')) {
    $assembly.GetType("KillerPDF.Services.$service", $true).GetMethod('Initialize', $static).Invoke($null, $null) | Out-Null
}
$windowType = $assembly.GetType('KillerPDF.Shell.MainWindow', $true)
$window = $null
$states = 0
$swatchCount = 0
$swatches = [Windows.Controls.WrapPanel]::new()
$swatches.Width = 400
function Pump {
    [Windows.Threading.Dispatcher]::CurrentDispatcher.Invoke([Action]{}, [Windows.Threading.DispatcherPriority]::ApplicationIdle)
}
function Call-Window($name, [object[]]$arguments) {
    $windowType.GetMethod($name, $instance).Invoke($window, $arguments) | Out-Null
    Pump
}
function Find-Options($node, [string]$group, [System.Collections.Generic.List[object]]$found) {
    if ($node -is [Windows.Controls.RadioButton] -and $node.GroupName -ceq $group) { $found.Add($node) }
    foreach ($child in [Windows.LogicalTreeHelper]::GetChildren($node)) {
        if ($child -is [Windows.DependencyObject]) { Find-Options $child $group $found }
    }
}
function Open-Picker {
    $menu = $window.FindName('ThemeMenu')
    $menu.IsOpen = $false
    Call-Window 'ThemeButton_Click' @($window.FindName('ThemeButton'), [Windows.RoutedEventArgs]::new())
    if (!$menu.IsOpen) { throw 'Theme menu did not open.' }
    return $menu
}
function Options([string]$group) {
    $menu = Open-Picker
    $found = [System.Collections.Generic.List[object]]::new()
    foreach ($item in $menu.Items) { Find-Options $item $group $found }
    return $found
}
try {
    $window = [Activator]::CreateInstance($windowType)
    $window.Left = -20000; $window.Top = -20000; $window.ShowActivated = $false; $window.ShowInTaskbar = $false
    $window.Show()
    Pump
    foreach ($themeName in [Enum]::GetNames($themes)) {
        $theme = [Enum]::Parse($themes, $themeName)
        $row = @(Options 'ThemeGroup' | Where-Object { $_.Tag -eq $theme })
        if ($row.Count -ne 1) { throw "The theme menu has $($row.Count) rows for $themeName." }
        $row[0].IsChecked = $true
        Pump
        if ($manager.GetProperty('Current', $static).GetValue($null) -ne $theme) { throw "Choosing $themeName did not apply it." }
        $states++
        $accentOptions = @(Options 'ThemeAccentGroup')
        if ($themeName -notin @('Dark', 'Light', 'Black', 'SE98')) {
            if ($accentOptions.Count -ne 0) { throw "$themeName shows an accent strip." }
            continue
        }
        if ($accentOptions.Count -ne [Enum]::GetValues($accents).Length) { throw "$themeName shows $($accentOptions.Count) accents." }
        foreach ($accent in [Enum]::GetValues($accents)) {
            $option = @(Options 'ThemeAccentGroup' | Where-Object { $_.Tag -eq $accent })[0]
            if ([string]::IsNullOrEmpty([Windows.Automation.AutomationProperties]::GetName($option))) { throw "Unnamed accent: $themeName/$accent" }
            $option.IsChecked = $true
            Pump
            if ($manager.GetMethod('AccentChoiceFor', $static).Invoke($null, @($theme)) -ne $accent) { throw "Choosing $themeName/$accent did not apply it." }
            $brush = $option.Content.Background
            if ($null -eq $brush -or !$brush.IsFrozen) { throw "Accent swatch is not a frozen brush: $themeName/$accent" }
            $tile = [Windows.Controls.Border]::new()
            $tile.Width = 48; $tile.Height = 32; $tile.Margin = [Windows.Thickness]::new(1)
            $tile.Background = $brush
            $swatches.Children.Add($tile) | Out-Null
            $states++
            $swatchCount++
        }
    }
    $locales = $assembly.GetType('KillerPDF.Services.Locale', $true)
    $localeManager = $assembly.GetType('KillerPDF.Services.LocaleManager', $true)
    $localeCount = 0
    foreach ($localeName in [Enum]::GetNames($locales)) {
        $localeManager.GetMethod('Apply', $static).Invoke($null, @([Enum]::Parse($locales, $localeName))) | Out-Null
        Pump
        $null = Open-Picker
        Call-Window 'LangButton_Click' @($window.FindName('LangButton'), [Windows.RoutedEventArgs]::new())
        if (!$window.FindName('LangMenu').IsOpen) { throw "Language menu did not open: $localeName" }
        $window.FindName('LangMenu').IsOpen = $false
        if ([string]::IsNullOrEmpty($app.TryFindResource('Str_Crash_Continue'))) { throw "Missing locale resources: $localeName" }
        if ($getSetting.Invoke($null, @('Locale')) -cne $localeName) { throw 'Locale setting did not persist in the isolated profile.' }
        $localeCount++
    }
    $filmGrain = $assembly.GetType('KillerPDF.Services.FilmGrain', $true)
    foreach ($enabled in @($false, $true, $false)) {
        $filmGrain.GetMethod('Set', $static).Invoke($null, @($enabled)) | Out-Null
        Pump
        $null = Open-Picker
        if ($getSetting.Invoke($null, @('FilmGrain')) -cne $(if ($enabled) { '1' } else { '0' })) { throw 'Film Grain setting did not persist.' }
        if (!$enabled -and $app.Resources['GrainOpacity'] -ne 0.0) { throw 'Film Grain is still visible.' }
    }
    $swatches.Measure([Windows.Size]::new(400, 136))
    $swatches.Arrange([Windows.Rect]::new(0, 0, 400, 136))
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(400, 136, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($swatches)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create((Join-Path $EvidenceDirectory 'swatches.png'))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
    $proof = "PASS $states theme/accent states chosen through the kit theme menu; $swatchCount named, frozen accent swatches; $localeCount locales with the language menu; persisted Film Grain toggles. Assembly=$($assembly.FullName); SHA256=$((Get-FileHash -LiteralPath $AssemblyPath).Hash)"
    [IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'result.txt'), $proof)
    Write-Output $proof
}
catch {
    [IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'failure.txt'), $_.Exception.ToString())
    throw
}
finally {
    if ($null -ne $window) { $window.FindName('ThemeMenu').IsOpen = $false; $window.Close() }
    $app.Shutdown()
}
