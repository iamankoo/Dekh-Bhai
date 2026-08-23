#Requires -Version 5.1
<#
.SYNOPSIS
    Starts the Dekh Bhai signaling server + viewer host.
#>
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location (Join-Path $repoRoot "signaling")

if (-not (Test-Path "node_modules")) {
    Write-Host "Installing signaling server dependencies..." -ForegroundColor Cyan
    npm install
}

Write-Host "Starting signaling server on http://localhost:8787 ..." -ForegroundColor Green
npm start
