#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the single-file Dekh Bhai installer (dist\release\DekhBhai-Setup.exe) that wraps the
    signed MSIX and its certificate - the one file to hand a friend. See
    packaging/installer/DekhBhai.iss for what it actually does and why (MSIX package identity is
    load-bearing for Graphics Capture border suppression - this wraps it, it doesn't replace it).

.DESCRIPTION
    1. Builds the MSIX (scripts/build-msix.ps1) if dist\DekhBhai.msix doesn't already exist -
       pass -SkipMsixBuild if you've already built it and don't want to rebuild.
    2. Locates the Inno Setup compiler (ISCC.exe), installing it via winget if missing.
    3. Compiles packaging/installer/DekhBhai.iss to dist/release/DekhBhai-Setup.exe.

.PARAMETER SkipMsixBuild
    Skip step 1 - use whatever dist\DekhBhai.msix already exists. Fails if it doesn't exist.
#>
param(
    [switch]$SkipMsixBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$msixPath = Join-Path $repoRoot "dist\DekhBhai.msix"

if (-not $SkipMsixBuild) {
    if (-not (Test-Path $msixPath)) {
        Write-Host "=== dist\DekhBhai.msix not found - building it first ===" -ForegroundColor Cyan
        & (Join-Path $repoRoot "scripts\build-msix.ps1")
        if ($LASTEXITCODE -ne 0) { throw "build-msix.ps1 failed" }
    } else {
        Write-Host "dist\DekhBhai.msix already exists - rebuilding to make sure the installer wraps the latest code." -ForegroundColor Cyan
        & (Join-Path $repoRoot "scripts\build-msix.ps1")
        if ($LASTEXITCODE -ne 0) { throw "build-msix.ps1 failed" }
    }
} elseif (-not (Test-Path $msixPath)) {
    throw "dist\DekhBhai.msix not found and -SkipMsixBuild was passed - nothing to wrap. Run scripts\build-msix.ps1 first."
}

Write-Host "=== Locating Inno Setup compiler (ISCC.exe) ===" -ForegroundColor Cyan
$iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if (-not $iscc) {
    $candidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($found) {
        $isccPath = $found
    } else {
        Write-Host "Inno Setup not found - installing via winget (free/open-source: https://jrsoftware.org/isinfo.php)..." -ForegroundColor Yellow
        winget install --id JRSoftware.InnoSetup -e --accept-source-agreements --accept-package-agreements
        if ($LASTEXITCODE -ne 0) { throw "winget install of Inno Setup failed - install it manually from https://jrsoftware.org/isdl.php" }
        $found = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $found) { throw "Inno Setup installed but ISCC.exe still not found in the expected locations - check the winget install output above." }
        $isccPath = $found
    }
} else {
    $isccPath = $iscc.Source
}
Write-Host "Using: $isccPath"

Write-Host "=== Compiling installer ===" -ForegroundColor Cyan
$issPath = Join-Path $repoRoot "packaging\installer\DekhBhai.iss"
& $isccPath $issPath
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed" }

$outPath = Join-Path $repoRoot "dist\release\DekhBhai-Setup.exe"
if (-not (Test-Path $outPath)) { throw "Expected output not found at $outPath" }

Write-Host "`nBuilt: $outPath" -ForegroundColor Green
Get-Item $outPath | Select-Object Name, Length, LastWriteTime
