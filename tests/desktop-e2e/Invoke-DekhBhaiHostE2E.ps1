#Requires -Version 5.1
<#
.SYNOPSIS
    Drives the REAL installed Dekh Bhai MSIX application (not `dotnet run`, not a mock) through
    the full Start Sharing -> Live -> Stop Sharing flow using Windows UI Automation, against a
    real production signaling/viewer configuration. This covers the HOST side of the end-to-end
    test - see README.md in this directory for how the browser/viewer side is verified alongside
    it (Chrome + WebRTC getStats(), driven separately - there is no scripted browser driver
    installed in this environment, and installing one was judged not worth it for a one-off
    verification; see README.md for the reasoning).

.DESCRIPTION
    Locates the installed Aniket.DekhBhai package, launches its real DekhBhai.exe directly
    (bypassing Explorer/AUMID activation specifically so this process can be given custom
    DEKHBHAI_SIGNALING_WS_URL / DEKHBHAI_VIEWER_BASE_URL environment variables without requiring
    a sign-out/sign-in - see docs/deployment/phase-2.md section 5 for why that's normally needed;
    a directly-created child process does not have that limitation, since it inherits this
    script's own environment block rather than Explorer's).

    Automates via System.Windows.Automation against AutomationIds. WPF exposes a control's
    x:Name as its AutomationId by default (confirmed by inspecting the running app's automation
    tree before writing this script - see README.md) - every control this script touches
    (StartSharingButton, Duration15Button, StartButton, ShareUrlBox, StopButton,
    StartAgainButton, and the panel/status TextBlocks) already has an x:Name in
    desktop/src/DekhBhai.App/MainWindow.xaml, so no AutomationId changes were needed anywhere in
    application code.

    Retries session creation because the production signaling server (Render free tier) is known
    to be intermittently unavailable (returns "x-render-routing: no-server") - see
    docs/deployment/phase-2.md. A retry here is a signaling-availability retry, not a UI
    automation retry - the distinction is recorded in the JSON report's `signaling.attempts`
    array so a run can be judged as "app worked, infra was flaky" vs. "app itself failed".

.PARAMETER SignalingWsUrl
    Production signaling WebSocket URL. Defaults to the deployed Render service.

.PARAMETER ViewerBaseUrl
    Production viewer base URL. Defaults to the deployed Vercel viewer.

.PARAMETER Duration
    Which duration button to click: FifteenMinutes | OneHour | FiveHours | UntilStopped.
    Defaults to FifteenMinutes (shortest fixed option) per the test brief's guidance to keep
    automated runs fast; this does not wait for expiration - see README.md for why automatic
    expiration is out of scope for this run.

.PARAMETER MaxSessionAttempts
    How many times to retry Start Sharing -> duration -> START if session creation fails
    (attributed to signaling availability, not the app) before giving up.

.NOTES
    This script only performs Launch -> Start Sharing -> duration -> Live -> capture share URL.
    It intentionally leaves the app running (Live) on exit so the browser/viewer side can be
    checked against a real live session. Use Invoke-DekhBhaiHostStop.ps1 -ProcessId <pid>
    afterwards to click Stop Sharing and verify the post-session screen, and to clean up.
#>
param(
    [string]$SignalingWsUrl = 'wss://dekh-bhai-signaling.onrender.com/ws?role=host',
    [string]$ViewerBaseUrl = 'https://viewer-theta-ashy.vercel.app/',
    [ValidateSet('FifteenMinutes', 'OneHour', 'FiveHours', 'UntilStopped')]
    [string]$Duration = 'FifteenMinutes',
    [int]$MaxSessionAttempts = 5,
    [string]$LogDir = (Join-Path $PSScriptRoot 'logs')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$runId = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $LogDir $runId
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
$logPath = Join-Path $runDir 'run.log'
$report = [ordered]@{
    runId              = $runId
    startedAtUtc       = (Get-Date).ToUniversalTime().ToString('o')
    signalingWsUrl     = $SignalingWsUrl
    viewerBaseUrl      = $ViewerBaseUrl
    duration           = $Duration
    app                = [ordered]@{}
    signaling          = [ordered]@{ attempts = @() }
    shareUrl           = $null
    errors             = @()
}

function Write-Log([string]$msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss.fff'), $msg
    Write-Host $line
    Add-Content -Path $logPath -Value $line
}

function Save-Report {
    ($report | ConvertTo-Json -Depth 8) | Set-Content -Path (Join-Path $runDir 'report.json')
}

# --- UI Automation helpers -------------------------------------------------

function Find-ById($root, [string]$id) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke-ButtonById($root, [string]$id) {
    $el = Find-ById $root $id
    if (-not $el) { throw "UI Automation: could not find element with AutomationId '$id'" }
    $pattern = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Get-TextById($root, [string]$id) {
    $el = Find-ById $root $id
    if (-not $el) { return $null }
    try {
        $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        return $vp.Current.Value
    } catch {
        return $el.Current.Name
    }
}

function Get-VisibilityById($root, [string]$id) {
    # This app toggles panel Visibility rather than adding/removing elements, so a Collapsed
    # panel's descendants still exist in the tree with IsOffscreen=$true. Used to confirm which
    # screen (Idle/Duration/Live/Stopping/PostSession) is actually showing.
    $el = Find-ById $root $id
    if (-not $el) { return $null }
    return -not $el.Current.IsOffscreen
}

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
if (-not ('DekhBhaiE2E.Win32' -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
namespace DekhBhaiE2E {
    public struct RECT { public int Left, Top, Right, Bottom; }
    public class Win32 {
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    }
}
"@
}

function Save-Screenshot([IntPtr]$hwnd, [string]$path) {
    # Only meaningful while the window is not minimized - a minimized WPF window has nothing
    # rendered to capture, and this is expected (see report note "minimized (expected)").
    if ([DekhBhaiE2E.Win32]::IsIconic($hwnd)) { return $false }
    $r = New-Object DekhBhaiE2E.RECT
    [DekhBhaiE2E.Win32]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { return $false }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return $true
}

# --- Locate and launch the INSTALLED app (not dotnet run) ------------------

function Start-DekhBhaiInstalled {
    $pkg = Get-AppxPackage -Name 'Aniket.DekhBhai'
    if (-not $pkg) { throw 'Aniket.DekhBhai is not installed. Build+install it first (scripts/build-msix.ps1 + Add-AppxPackage) - see docs/development/packaging.md.' }
    $report.app.packageFullName = $pkg.PackageFullName
    $report.app.installLocation = $pkg.InstallLocation
    $exe = Join-Path $pkg.InstallLocation 'DekhBhai.exe'
    if (-not (Test-Path $exe)) { throw "Expected exe not found at $exe" }

    Write-Log "Launching installed exe directly: $exe"
    Write-Log "  DEKHBHAI_SIGNALING_WS_URL=$SignalingWsUrl"
    Write-Log "  DEKHBHAI_VIEWER_BASE_URL=$ViewerBaseUrl"
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.UseShellExecute = $false
    $psi.EnvironmentVariables['DEKHBHAI_SIGNALING_WS_URL'] = $SignalingWsUrl
    $psi.EnvironmentVariables['DEKHBHAI_VIEWER_BASE_URL'] = $ViewerBaseUrl
    $proc = [System.Diagnostics.Process]::Start($psi)

    $deadline = (Get-Date).AddSeconds(15)
    while (-not $proc.MainWindowHandle -or $proc.MainWindowHandle -eq [IntPtr]::Zero) {
        if ((Get-Date) -gt $deadline) { throw 'Timed out waiting for the Dekh Bhai main window to appear.' }
        Start-Sleep -Milliseconds 250
        $proc.Refresh()
    }
    Write-Log "Window appeared: PID=$($proc.Id) Handle=$($proc.MainWindowHandle) Title='$($proc.MainWindowTitle)'"
    $report.app.launch = 'ok'
    $report.app.pid = $proc.Id
    return $proc
}

# --- Main flow ---------------------------------------------------------

try {
    $proc = Start-DekhBhaiInstalled
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)

    $idleVisible = Get-VisibilityById $root 'StartSharingButton'
    Write-Log "Idle screen visible: $idleVisible"
    $report.app.idleScreenVisible = [bool]$idleVisible
    Save-Screenshot $proc.MainWindowHandle (Join-Path $runDir '01-idle.png') | Out-Null

    Write-Log 'Clicking START SHARING'
    Invoke-ButtonById $root 'StartSharingButton'
    Start-Sleep -Milliseconds 500
    $report.app.startSharingClicked = $true

    $durationButtonId = switch ($Duration) {
        'FifteenMinutes' { 'Duration15Button' }
        'OneHour'        { 'Duration1hButton' }
        'FiveHours'      { 'Duration5hButton' }
        'UntilStopped'   { 'DurationUntilStoppedButton' }
    }
    Write-Log "Selecting duration: $Duration ($durationButtonId)"
    Invoke-ButtonById $root $durationButtonId
    Start-Sleep -Milliseconds 300
    $report.app.durationSelected = $Duration

    Save-Screenshot $proc.MainWindowHandle (Join-Path $runDir '02-duration-selected.png') | Out-Null

    # --- Session creation, with retry attributed to signaling availability ---
    $sessionLive = $false
    for ($attempt = 1; $attempt -le $MaxSessionAttempts -and -not $sessionLive; $attempt++) {
        $attemptRecord = [ordered]@{
            attempt = $attempt
            atUtc   = (Get-Date).ToUniversalTime().ToString('o')
        }
        Write-Log "Session attempt $attempt/$MaxSessionAttempts - clicking START"
        Invoke-ButtonById $root 'StartButton'

        $deadline = (Get-Date).AddSeconds(14)
        $outcome = $null
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
            $liveVisible = Get-VisibilityById $root 'ShareUrlBox'
            if ($liveVisible) { $outcome = 'live'; break }
            $statusText = Get-TextById $root 'DurationStatusText'
            if ($statusText -and $statusText -match 'unable to connect|failed to start|something went wrong') {
                $outcome = 'error'
                $attemptRecord.errorText = $statusText
                break
            }
        }
        if (-not $outcome) { $outcome = 'timeout' }
        $attemptRecord.outcome = $outcome
        $report.signaling.attempts += $attemptRecord
        Write-Log "  attempt $attempt outcome: $outcome $(if($attemptRecord.errorText){"- $($attemptRecord.errorText)"})"

        if ($outcome -eq 'live') {
            $sessionLive = $true
        } else {
            Start-Sleep -Seconds 2
        }
    }

    $report.signaling.successfulAttempt = if ($sessionLive) { $report.signaling.attempts.Count } else { $null }
    $report.signaling.totalAttempts = $report.signaling.attempts.Count

    if (-not $sessionLive) {
        Write-Log 'FAILED: session never went Live after all retries. Treating as signaling-availability failure unless errorText indicates otherwise.'
        Save-Screenshot $proc.MainWindowHandle (Join-Path $runDir '03-session-failed.png') | Out-Null
        $report.app.reachedLive = $false
        Save-Report
        throw 'Session did not reach Live state - see report.json signaling.attempts for per-attempt detail.'
    }

    $report.app.reachedLive = $true
    Write-Log 'Session reached LIVE.'

    $shareUrl = Get-TextById $root 'ShareUrlBox'
    Write-Log "Captured share URL from app UI: $shareUrl"
    $report.shareUrl = $shareUrl

    Start-Sleep -Seconds 2   # let MinimizeAndExcludeFromCapture() run
    $proc.Refresh()
    $wp = $root.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern)
    $visualState = $wp.Current.WindowVisualState
    Write-Log "Window visual state after going Live: $visualState"
    $report.app.windowVisualStateAfterLive = $visualState.ToString()

    Save-Report
    Write-Log "HOST-SIDE START COMPLETE. Share URL: $shareUrl"
    Write-Log "PID $($proc.Id) left running (Live) for the browser-side check - stop it with:"
    Write-Log "  powershell -File `"$PSCommandPath`" -StopOnly -ProcessId $($proc.Id)"
    $proc.Id | Out-File -FilePath (Join-Path $runDir 'pid.txt')
    Write-Output $shareUrl
}
catch {
    $report.errors += $_.Exception.Message
    Write-Log "ERROR: $($_.Exception.Message)"
    Save-Report
    throw
}
