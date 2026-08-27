#Requires -Version 5.1
<#
.SYNOPSIS
    Second half of the host-side E2E harness: clicks STOP SHARING on an already-Live Dekh Bhai
    instance (started by Invoke-DekhBhaiHostE2E.ps1), verifies the post-session screen
    ("Sharing Stopped" / "Designed by Aniket" / "START AGAIN"), then terminates the process so no
    background instance is left running after the test.

.PARAMETER ProcessId
    PID of the running Dekh Bhai instance, as printed by Invoke-DekhBhaiHostE2E.ps1 (also saved
    to logs/<runId>/pid.txt).

.PARAMETER RunDir
    The specific logs/<runId> directory from the Start run, so this appends to the same
    report.json/run.log instead of starting a new one. Optional - if omitted, writes its own
    logs/<timestamp>-stop directory.
#>
param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [string]$RunDir
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if (-not $RunDir) {
    $RunDir = Join-Path $PSScriptRoot ("logs\{0}-stop" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $RunDir | Out-Null
}
$logPath = Join-Path $RunDir 'run.log'
$reportPath = Join-Path $RunDir 'report.json'
$report = if (Test-Path $reportPath) { Get-Content $reportPath -Raw | ConvertFrom-Json } else { [ordered]@{} }

function Write-Log([string]$msg) {
    $line = "[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss.fff'), $msg
    Write-Host $line
    Add-Content -Path $logPath -Value $line
}

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

function Get-NameById($root, [string]$id) {
    $el = Find-ById $root $id
    if (-not $el) { return $null }
    return $el.Current.Name
}

function Get-VisibilityById($root, [string]$id) {
    $el = Find-ById $root $id
    if (-not $el) { return $null }
    return -not $el.Current.IsOffscreen
}

try {
    $proc = Get-Process -Id $ProcessId -ErrorAction Stop
    Write-Log "Attaching to PID $ProcessId"

    # The window may be minimized (expected - Live state auto-minimizes). UI Automation can
    # still find and invoke elements on a minimized WPF window without restoring it.
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)

    Write-Log 'Clicking STOP SHARING'
    Invoke-ButtonById $root 'StopButton'

    $deadline = (Get-Date).AddSeconds(15)
    $stopped = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if (Get-VisibilityById $root 'StartAgainButton') { $stopped = $true; break }
    }

    $stopResult = [ordered]@{
        stoppedWithinTimeout = $stopped
    }

    if ($stopped) {
        # PostSessionPanel (a plain StackPanel with no AutomationProperties set) is not exposed
        # as its own node in UI Automation's Control view at all - WPF's default automation peer
        # for an uncustomized layout Panel reports IsControlElement=false, so FindFirst/FindAll
        # with an AutomationId condition for the panel itself always returns null (confirmed by
        # inspecting the live tree before writing this script - the Idle/Duration/Live panels
        # never appear as nodes either, only their content does). Search the whole window's Text
        # controls directly instead, and rely on IsOffscreen to distinguish the currently-visible
        # screen from the other (Collapsed but still tree-resident) panels' text.
        $textCond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::Text)
        $visibleTexts = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCond) |
            Where-Object { -not $_.Current.IsOffscreen -and $_.Current.Name } |
            ForEach-Object { $_.Current.Name }
        Write-Log "Visible text content: $($visibleTexts -join ' | ')"
        $stopResult.visibleTexts = $visibleTexts
        $stopResult.hasSharingStopped = ($visibleTexts -contains 'Sharing Stopped')
        $stopResult.hasDesignedByAniket = ($visibleTexts -contains 'Designed by Aniket')
        Write-Log "  'Sharing Stopped' visible: $($stopResult.hasSharingStopped)"
        Write-Log "  'Designed by Aniket' visible: $($stopResult.hasDesignedByAniket)"
        Write-Log "  'START AGAIN' visible (button, checked above): $stopped"
    } else {
        Write-Log 'FAILED: post-session screen (START AGAIN) did not appear within 15s of clicking Stop.'
    }

    # $report was loaded via ConvertFrom-Json, which returns a PSCustomObject - dot-assignment
    # only works for properties that already exist on one of those, so a new property needs
    # Add-Member instead (a plain hashtable wouldn't have this restriction, but staying
    # consistent with the JSON round-trip type here).
    $report | Add-Member -NotePropertyName 'stop' -NotePropertyValue $stopResult -Force
    ($report | ConvertTo-Json -Depth 8) | Set-Content -Path $reportPath
}
finally {
    # Cleanup always runs, independent of whether verification above threw - a bug in a
    # verification check must never leave the process running in the background.
    Write-Log "Terminating PID $ProcessId (cleanup - no background instance left running)"
    Stop-Process -Id $ProcessId -Force -ErrorAction SilentlyContinue
}

if (-not $stopResult.stoppedWithinTimeout) {
    throw 'Stop Sharing did not reach the post-session screen within timeout.'
}
if (-not ($stopResult.hasSharingStopped -and $stopResult.hasDesignedByAniket)) {
    throw 'Post-session screen appeared but expected text was not found - see visibleTexts in report.json.'
}
Write-Log 'STOP-SIDE VERIFICATION COMPLETE.'
