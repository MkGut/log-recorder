<#
.SYNOPSIS
  Keyboard and UI Automation check for the lightweight shell (log.exe).
.DESCRIPTION
  For every screen, starts `log.exe --fixture-window <screen>` (hidden fake-data window, isolated data root),
  then checks each control's name and class, that every button is enabled, visible and a Tab stop, and the
  Tab order. Real Tab presses are used when Windows gives this script a foreground window; otherwise the
  order is checked structurally (z-order + WS_TABSTOP), which is what the dialog manager follows.
  Run from an interactive session for the real-key result.
#>
[CmdletBinding()]
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\artifacts\shell-publish\log.exe'),
    [string]$ResultPath = (Join-Path $env:TEMP 'log-shell\keyboard-results.json')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if (-not ('LogKb' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class LogKb
{
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    public static void Key(byte vk) { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2, UIntPtr.Zero); }
}
'@
}

$AE = [System.Windows.Automation.AutomationElement]
$ellipsis = [string][char]0x2026
$dot = [string][char]0x00B7
$dash = [string][char]0x2014

# screen -> expected static texts and buttons in Tab order (names as a user hears them)
$screens = [ordered]@{
    'invitation'        = @{ Title = 'Wanna lock in?'; Texts = @('Wanna lock in?', 'A little time for what matters.'); Buttons = @('Not now', "Let's go", 'Close') }
    'source'            = @{ Title = 'Your time starts here.'; Texts = @('Your time starts here.', 'Windows will ask which screen or window to share. Recording starts after you confirm.'); Buttons = @('Back', "Choose source$ellipsis", 'Close') }
    'recording'         = @{ Title = 'Recording'; Texts = @("Recording $dot Entire display", 'Click the timer to finish', 'Saved on this device. Close this window to keep going.'); Buttons = @('Finish recording, elapsed 24 minutes', 'Close') }
    'goodbye'           = @{ Title = 'Sure you wanna say goodbye?'; Texts = @('Elapsed 24 minutes', "Still recording $dot Entire display", 'Sure you wanna say goodbye?', 'Keep this session as a timelapse.'); Buttons = @('Keep going', 'Finish & preview', 'Close') }
    'finishing'         = @{ Title = "Finishing your session$ellipsis"; Texts = @("Finishing your session$ellipsis", 'Recording has stopped. Your source is kept on this device.'); Buttons = @('Close') }
    'preview'           = @{ Title = 'A little time, kept.'; Texts = @('A little time, kept.', '24 minutes of focus. Ready to take with you.'); Buttons = @('Keep for later', 'Save timelapse', 'Close') }
    'saved'             = @{ Title = 'All yours.'; Texts = @('All yours.', 'Your timelapse is saved.', 'Videos / log / session-2026-11-05.mp4'); Buttons = @('Open file', 'Done', 'Close') }
    'consent-cancelled' = @{ Title = 'No rush.'; Texts = @('No rush.', "Nothing was recorded. Choose a source whenever you're ready."); Buttons = @('Not now', "Choose source$ellipsis", 'Close') }
    'source-lost'       = @{ Title = "Paused $dot source unavailable"; Texts = @("Paused $dot source unavailable", 'Your session is safe.'); Buttons = @('Finish session', 'Try again', 'Close') }
    'discard'           = @{ Title = 'Delete this recording?'; Texts = @('Delete this recording?', 'It will be removed from this device.', "This can't be undone."); Buttons = @('Delete', 'Keep it', 'Close') }
    'save-error'        = @{ Title = "Couldn't save there."; Texts = @("Couldn't save there.", 'Your recording is still on this device.', 'The folder is not available.'); Buttons = @('Keep for later', "Choose folder$ellipsis", 'Close') }
}

if (-not (Test-Path -LiteralPath $Exe)) { throw "log.exe not found: $Exe (publish log.Shell first)" }
$dataRoot = Join-Path $env:TEMP 'log-shell\keyboard-data'
New-Item -ItemType Directory -Force $dataRoot | Out-Null
$previousRoot = $env:LOG_DATA_ROOT
$env:LOG_DATA_ROOT = $dataRoot
New-Item -ItemType Directory -Force (Split-Path $ResultPath) | Out-Null

# Button text doubles '&' so it is not read as a mnemonic; compare the spoken form.
function Normalize([string]$name) { $name.Replace('&&', '&') }

function Get-Children([IntPtr]$parent) {
    $h = [LogKb]::GetWindow($parent, 5)  # GW_CHILD
    while ($h -ne [IntPtr]::Zero) { $h; $h = [LogKb]::GetWindow($h, 2) }  # GW_HWNDNEXT
}

$results = @()
$failures = 0
foreach ($screen in $screens.Keys) {
    $expected = $screens[$screen]
    $entry = [ordered]@{ screen = $screen; problems = @(); tabMode = 'structural'; tabOrder = @() }
    $proc = Start-Process -FilePath $Exe -ArgumentList @('--fixture-window', $screen) -PassThru
    try {
        $win = $null
        for ($i = 0; $i -lt 40 -and -not $win; $i++) {
            Start-Sleep -Milliseconds 250
            $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, [int]$proc.Id)
            $win = $AE::RootElement.FindFirst('Children', $cond)
        }
        if (-not $win) { $entry.problems += 'window did not appear'; $results += $entry; $failures++; continue }
        $hwnd = [IntPtr]$win.Current.NativeWindowHandle
        Start-Sleep -Milliseconds 400

        if ($win.Current.Name -ne "log $dash $($expected.Title)") { $entry.problems += "window name '$($win.Current.Name)'" }
        $controls = @($win.FindAll('Descendants', [System.Windows.Automation.Condition]::TrueCondition) | ForEach-Object {
            [pscustomobject]@{ Name = Normalize $_.Current.Name; Class = $_.Current.ClassName; Enabled = $_.Current.IsEnabled; Handle = [IntPtr]$_.Current.NativeWindowHandle }
        })

        foreach ($t in $expected.Texts) {
            if (-not ($controls | Where-Object { $_.Class -eq 'Static' -and $_.Name -eq $t })) { $entry.problems += "missing text '$t'" }
        }
        foreach ($b in $expected.Buttons) {
            $c = $controls | Where-Object { $_.Class -eq 'Button' -and $_.Name -eq $b }
            if (-not $c) { $entry.problems += "missing button '$b'"; continue }
            if (-not $c.Enabled) { $entry.problems += "button '$b' is disabled" }
            if (-not [LogKb]::IsWindowVisible($c.Handle)) { $entry.problems += "button '$b' is not visible" }
            if (([LogKb]::GetWindowLong($c.Handle, -16) -band 0x10000) -eq 0) { $entry.problems += "button '$b' is not a Tab stop" }
        }
        foreach ($e in @($controls | Where-Object { $_.Class -eq 'Button' -and $_.Name -notin $expected.Buttons })) {
            $entry.problems += "unexpected button '$($e.Name)'"
        }

        # Structural Tab order: z-order of Tab-stop controls, which is what the dialog manager follows.
        $order = @(Get-Children $hwnd | Where-Object { ([LogKb]::GetWindowLong($_, -16) -band 0x10000) -ne 0 } | ForEach-Object {
            $h = $_; ($controls | Where-Object { $_.Handle -eq $h } | Select-Object -First 1).Name })
        $entry.tabOrder = $order
        if (($order -join '|') -ne ($expected.Buttons -join '|')) { $entry.problems += "tab order is [$($order -join ', ')]" }

        # Real Tab presses, when Windows lets this session take (and keep) the foreground.
        [void][LogKb]::SetForegroundWindow($hwnd)
        Start-Sleep -Milliseconds 500
        if ([LogKb]::GetForegroundWindow() -eq $hwnd) {
            $seen = @()
            $lost = $false
            for ($i = 0; $i -lt ($expected.Buttons.Count + 1); $i++) {
                # Never send a key to another app: re-check the foreground before every press.
                if ([LogKb]::GetForegroundWindow() -ne $hwnd) { [void][LogKb]::SetForegroundWindow($hwnd); Start-Sleep -Milliseconds 200 }
                if ([LogKb]::GetForegroundWindow() -ne $hwnd) { $lost = $true; break }
                $seen += Normalize $AE::FocusedElement.Current.Name
                [LogKb]::Key(0x09); Start-Sleep -Milliseconds 250
            }
            if ($lost) {
                $entry.tabMode = 'structural (focus lost)'
            }
            else {
                $entry.tabMode = 'keys'
                $entry.tabOrder = $seen
                foreach ($b in $expected.Buttons) { if ($seen -notcontains $b) { $entry.problems += "Tab never reached '$b' (saw: $($seen -join ', '))" } }
            }
        }
    }
    finally {
        if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    }
    if ($entry.problems.Count -gt 0) { $failures++ }
    $results += $entry
}

$env:LOG_DATA_ROOT = $previousRoot
[pscustomobject]@{ failures = $failures; screens = $results } | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 -Path $ResultPath
foreach ($r in $results) {
    $status = if ($r.problems.Count) { 'FAIL' } else { 'ok  ' }
    '{0} {1,-18} tab={2,-10} order=[{3}]' -f $status, $r.screen, $r.tabMode, ($r.tabOrder -join ', ')
    foreach ($p in $r.problems) { "       - $p" }
}
"Result: $ResultPath  failures=$failures"
exit ([int]($failures -gt 0))
