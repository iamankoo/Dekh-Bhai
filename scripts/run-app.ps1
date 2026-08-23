#Requires -Version 5.1
<#
.SYNOPSIS
    Builds and runs the Dekh Bhai host app. Assumes the signaling server is already running
    (see run-signaling.ps1) - the app will fail to start a session otherwise.
#>
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location (Join-Path $repoRoot "desktop")

dotnet run --project src/DekhBhai.App -c Debug
