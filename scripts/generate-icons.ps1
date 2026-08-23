#Requires -Version 5.1
<#
.SYNOPSIS
    Generates the Dekh Bhai app icon (.ico) and the MSIX package logo PNGs from a simple
    programmatic design (dark rounded square, green "D" glyph) - there is no separate design
    asset pipeline for Phase 1, this is a minimal placeholder consistent with the app's own
    dark/green theme.
#>
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$appAssets = Join-Path $repoRoot "desktop\src\DekhBhai.App\Assets"
$msixAssets = Join-Path $repoRoot "packaging\msix\Assets"
New-Item -ItemType Directory -Force -Path $appAssets | Out-Null
New-Item -ItemType Directory -Force -Path $msixAssets | Out-Null

$bg = [System.Drawing.Color]::FromArgb(255, 11, 12, 15)      # #0B0C0F
$accent = [System.Drawing.Color]::FromArgb(255, 61, 220, 132) # #3DDC84

function New-DekhBhaiBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $radius = [Math]::Max(2, [int]($size * 0.18))
    $rect = New-Object System.Drawing.Rectangle 0, 0, $size, $size
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $bgBrush = New-Object System.Drawing.SolidBrush $bg
    $g.FillPath($bgBrush, $path)

    # Simple glyph: a filled circle (the "live dot") with a play-style triangle - evokes
    # casting/mirroring without spelling out letters that won't read at 16px.
    $dotSize = $size * 0.62
    $dotRect = New-Object System.Drawing.RectangleF (($size - $dotSize) / 2), (($size - $dotSize) / 2), $dotSize, $dotSize
    $accentBrush = New-Object System.Drawing.SolidBrush $accent
    $g.FillEllipse($accentBrush, $dotRect)

    $triSize = $size * 0.26
    $cx = $size / 2
    $cy = $size / 2
    $offset = $triSize * 0.15
    $pts = @(
        (New-Object System.Drawing.PointF ($cx - $triSize/2 + $offset), ($cy - $triSize/1.8)),
        (New-Object System.Drawing.PointF ($cx - $triSize/2 + $offset), ($cy + $triSize/1.8)),
        (New-Object System.Drawing.PointF ($cx + $triSize/1.4 + $offset), $cy)
    )
    $bgBrush2 = New-Object System.Drawing.SolidBrush $bg
    $g.FillPolygon($bgBrush2, $pts)

    $g.Dispose()
    return $bmp
}

function Save-Png([int]$size, [string]$path) {
    $bmp = New-DekhBhaiBitmap $size
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "wrote $path ($size x $size)"
}

# --- .ico for the WPF app (multi-resolution) ---
$icoSizes = @(16, 32, 48, 256)
$icoPath = Join-Path $appAssets "DekhBhai.ico"
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ms
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$icoSizes.Count)

$imageDataList = @()
foreach ($s in $icoSizes) {
    $bmp = New-DekhBhaiBitmap $s
    $pngStream = New-Object System.IO.MemoryStream
    $bmp.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
    $imageDataList += ,($pngStream.ToArray())
    $bmp.Dispose()
}

$headerSize = 6 + (16 * $icoSizes.Count)
$offset = $headerSize
for ($i = 0; $i -lt $icoSizes.Count; $i++) {
    $s = $icoSizes[$i]
    $data = $imageDataList[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0)   # color palette
    $bw.Write([byte]0)   # reserved
    $bw.Write([UInt16]1) # color planes
    $bw.Write([UInt16]32) # bits per pixel
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($data in $imageDataList) { $bw.Write($data) }
[System.IO.File]::WriteAllBytes($icoPath, $ms.ToArray())
$bw.Dispose(); $ms.Dispose()
Write-Host "wrote $icoPath (sizes: $($icoSizes -join ', '))"

# --- MSIX manifest logos (minimum required set) ---
Save-Png 44 (Join-Path $msixAssets "Square44x44Logo.png")
Save-Png 150 (Join-Path $msixAssets "Square150x150Logo.png")
Save-Png 50 (Join-Path $msixAssets "StoreLogo.png")

Write-Host "Icon generation complete."
