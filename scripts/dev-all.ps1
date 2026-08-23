#Requires -Version 5.1
<#
.SYNOPSIS
    Starts the signaling server in a background job, then runs the host app in the foreground.
    Stops the signaling server when the app exits.
#>
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

$signalingDir = Join-Path $repoRoot "signaling"
if (-not (Test-Path (Join-Path $signalingDir "node_modules"))) {
    Write-Host "Installing signaling server dependencies..." -ForegroundColor Cyan
    Push-Location $signalingDir
    npm install
    Pop-Location
}

Write-Host "Starting signaling server in the background..." -ForegroundColor Cyan
$job = Start-Job -ScriptBlock {
    param($dir)
    Set-Location $dir
    npm start
} -ArgumentList $signalingDir

Start-Sleep -Seconds 1
try {
    $health = Invoke-RestMethod -Uri "http://localhost:8787/healthz" -TimeoutSec 5
    Write-Host "Signaling server is up: $($health | ConvertTo-Json -Compress)" -ForegroundColor Green
} catch {
    Write-Warning "Signaling server did not respond to /healthz yet - check 'Receive-Job $($job.Id)' for errors."
}

try {
    Write-Host "Starting Dekh Bhai host app..." -ForegroundColor Cyan
    Set-Location (Join-Path $repoRoot "desktop")
    dotnet run --project src/DekhBhai.App -c Debug
} finally {
    Write-Host "Stopping signaling server..." -ForegroundColor Cyan
    Stop-Job $job -ErrorAction SilentlyContinue
    Remove-Job $job -ErrorAction SilentlyContinue
}
