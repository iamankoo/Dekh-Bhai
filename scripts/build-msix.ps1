#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the installable Dekh Bhai MSIX package end to end:
    publish (self-contained win-x64) -> stage layout -> makeappx pack -> signtool sign.

.PARAMETER WindowsSdkBinDir
    Folder containing makeappx.exe/signtool.exe. Defaults to the 10.0.26100.0 x64 tools from
    a winget-installed Windows SDK (see docs/development/packaging.md).

.PARAMETER PfxPath / -PfxPassword
    Code-signing certificate to sign the package with. Defaults to the Phase 1 dev/sideload
    cert at packaging/msix/DekhBhaiSigning.pfx (see docs/development/packaging.md for how it
    was generated, and how to generate your own). PfxPassword has no default - it protects a
    private key and must never be hardcoded in a committed script; pass it explicitly or set
    DEKHBHAI_PFX_PASSWORD in your own (never-committed) environment.
#>
param(
    [string]$WindowsSdkBinDir = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64",
    [string]$PfxPath = "",
    [string]$PfxPassword = $env:DEKHBHAI_PFX_PASSWORD
)

if ([string]::IsNullOrEmpty($PfxPassword)) {
    throw "PfxPassword not set - pass -PfxPassword or set the DEKHBHAI_PFX_PASSWORD environment variable. Never hardcode a signing-key password in this script."
}

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$desktop = Join-Path $repoRoot "desktop"
$pkgDir = Join-Path $repoRoot "packaging\msix"
$publishDir = Join-Path $repoRoot "packaging\publish"
$layoutDir = Join-Path $pkgDir "layout"
$distDir = Join-Path $repoRoot "dist"
$outMsix = Join-Path $distDir "DekhBhai.msix"

if ([string]::IsNullOrEmpty($PfxPath)) { $PfxPath = Join-Path $pkgDir "DekhBhaiSigning.pfx" }

Write-Host "=== 1/5: dotnet publish (self-contained, win-x64, Release) ===" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
dotnet publish (Join-Path $desktop "src\DekhBhai.App\DekhBhai.App.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:IncludeNativeLibrariesForSelfExtract=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "=== 2/5: generate icons/logos (if missing) ===" -ForegroundColor Cyan
if (-not (Test-Path (Join-Path $pkgDir "Assets\Square150x150Logo.png"))) {
    & (Join-Path $repoRoot "scripts\generate-icons.ps1")
}

Write-Host "=== 3/5: stage MSIX layout ===" -ForegroundColor Cyan
if (Test-Path $layoutDir) { Remove-Item -Recurse -Force $layoutDir }
New-Item -ItemType Directory -Force -Path $layoutDir | Out-Null
Copy-Item -Path (Join-Path $publishDir "*") -Destination $layoutDir -Recurse
Copy-Item -Path (Join-Path $pkgDir "AppxManifest.xml") -Destination (Join-Path $layoutDir "AppxManifest.xml")
New-Item -ItemType Directory -Force -Path (Join-Path $layoutDir "Assets") | Out-Null
Copy-Item -Path (Join-Path $pkgDir "Assets\*.png") -Destination (Join-Path $layoutDir "Assets")
Remove-Item -ErrorAction SilentlyContinue (Join-Path $layoutDir "*.pdb")

Write-Host "=== 4/5: makeappx pack ===" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$makeappx = Join-Path $WindowsSdkBinDir "makeappx.exe"
& $makeappx pack /d $layoutDir /p $outMsix /overwrite
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed" }

Write-Host "=== 5/5: signtool sign ===" -ForegroundColor Cyan
$signtool = Join-Path $WindowsSdkBinDir "signtool.exe"
& $signtool sign /fd SHA256 /a /f $PfxPath /p $PfxPassword $outMsix
if ($LASTEXITCODE -ne 0) { throw "signtool sign failed" }

Write-Host "`nBuilt and signed: $outMsix" -ForegroundColor Green
Get-Item $outMsix | Select-Object Name, Length, LastWriteTime
