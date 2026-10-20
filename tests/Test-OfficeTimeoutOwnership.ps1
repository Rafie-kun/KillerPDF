# Requires PowerShell 7 and a Release build of KillerPDF.Tests.
param(
    [Parameter(Mandatory)][string]$TestAssembly,
    [Parameter(Mandatory)][string]$EvidenceRoot
)
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path $TestAssembly).Path)
$method = $assembly.GetType('KillerPDF.Services.OfficeConversion').GetMethods(
    [Reflection.BindingFlags]'Static,NonPublic') |
    Where-Object { $_.Name -eq 'ToPdf' -and $_.GetParameters().Length -eq 3 }
$root = Join-Path $EvidenceRoot ('office-timeout-' + [guid]::NewGuid().ToString('N'))
$null = [IO.Directory]::CreateDirectory($root)
# Only this disposable child owns a real window. No Office process is started.
$hostCode = @'
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WindowHost {
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(int e, string c, string n, int s,
        int x, int y, int w, int h, IntPtr p, IntPtr m, IntPtr i, IntPtr a);
}
"@
$window = [WindowHost]::CreateWindowEx(0, 'STATIC', 'Office timeout test',
    0, 0, 0, 0, 0, [IntPtr]::new(-3), [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero)
if ($window -eq [IntPtr]::Zero) { throw 'Window creation failed' }
[Console]::WriteLine($window.ToInt64())
$null = [Console]::ReadLine()
'@
Add-Type @'
using System;
using System.Threading;
public sealed class TimeoutOffice {
    public long Hwnd { get; set; }
    public bool Visible { get; set; }
    public object DisplayAlerts { get; set; } = true;
    public int AutomationSecurity { get; set; } = 1;
    public TimeoutDocuments Documents { get; } = new();
    public TimeoutDocuments Workbooks => Documents;
    public void Quit() { }
    public void Convert(System.Reflection.MethodInfo method, string path) =>
        method.Invoke(null, new object[] { path, new Func<bool, object>(_ => this), TimeSpan.FromSeconds(2) });
}
public sealed class TimeoutDocuments {
    public int Count { get; set; }
    public ManualResetEventSlim Entered { get; } = new();
    public ManualResetEventSlim Release { get; } = new();
    public ManualResetEventSlim Finished { get; } = new();
    public object Open(string path, object links, bool readOnly,
        [System.Runtime.InteropServices.Optional] object format,
        [System.Runtime.InteropServices.Optional] object password) {
        Entered.Set();
        try { Release.Wait(); throw new InvalidOperationException("Synthetic Open released."); }
        finally { Finished.Set(); }
    }
}
'@
$failures = 0
foreach ($extension in @('doc', 'xls')) {
    foreach ($scenario in @(
        @{ Name = 'hidden-existing'; Visible = $false; Count = 1; Terminate = $false },
        @{ Name = 'visible-existing'; Visible = $true; Count = 1; Terminate = $false },
        @{ Name = 'hidden-empty'; Visible = $false; Count = 0; Terminate = $true },
        @{ Name = 'visible-empty'; Visible = $true; Count = 0; Terminate = $false }
    )) {
        $start = [Diagnostics.ProcessStartInfo]::new((Get-Command pwsh).Source)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardInput = $true
        $start.RedirectStandardOutput = $true
        foreach ($argument in @('-NoProfile', '-EncodedCommand',
            [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($hostCode)))) {
            $start.ArgumentList.Add($argument)
        }
        $child = [Diagnostics.Process]::Start($start)
        $app = [TimeoutOffice]::new()
        try {
            $line = $child.StandardOutput.ReadLineAsync()
            if (!$line.Wait(10000)) { throw 'Window host did not report a handle.' }
            $app.Hwnd = [long]$line.Result
            if ($app.Hwnd -eq 0) { throw 'Invalid test window handle.' }
            $app.Visible = $scenario.Visible
            $app.Documents.Count = $scenario.Count
            $source = Join-Path $root ($scenario.Name + '.' + $extension)
            [IO.File]::WriteAllBytes($source, [byte[]](0xD0, 0xCF, 0x11, 0xE0))
            $timedOut = $false
            try { $app.Convert($method, $source) }
            catch {
                $cause = $_.Exception
                while ($cause.InnerException) { $cause = $cause.InnerException }
                if ($cause -isnot [TimeoutException]) { throw }
                $timedOut = $true
            }
            if (!$timedOut -or !$app.Documents.Entered.IsSet) { throw 'Timeout seam was not reached.' }
            $terminated = $child.WaitForExit(1000)
            if ($terminated -ne $scenario.Terminate) { $failures++ }
            "extension=$extension scenario=$($scenario.Name) terminated=$terminated expected=$($scenario.Terminate)"
        }
        finally {
            $app.Documents.Release.Set()
            $null = $app.Documents.Finished.Wait(5000)
            if (!$child.HasExited) {
                $child.StandardInput.WriteLine('exit')
                if (!$child.WaitForExit(5000)) { $child.Kill() }
            }
            $child.Dispose()
        }
    }
}
if ($failures) { throw "$failures Office timeout ownership cases failed." }
'PASS all eight Office timeout ownership cases'
