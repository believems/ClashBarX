# 从 macOS 线的品牌图标源（Assets.xcassets 的 512px PNG）生成 Windows 侧全部图标资产：
#   - app.ico      （exe 嵌入图标，256/48/32/16 多尺寸）
#   - tray.ico     （托盘默认图标，32）
#   - tray-run.ico / tray-sleep.ico（托盘状态变体，32；源：BrandRun/BrandSleep）
# 输出均落在 src/ClashBarX.App/ 下（app.ico 与 Assets/）。
# 用法：powershell -NoProfile -File generate-app-icon.ps1

param(
    [string]$AppRoot = "$PSScriptRoot\..\src\ClashBarX.App"
)

Add-Type -AssemblyName System.Drawing

$macAssets = Join-Path $PSScriptRoot "..\..\Sources\ClashBar\Resources\Assets.xcassets"

function Convert-PngToIco {
    param(
        [string]$SourcePng,
        [string]$OutputIco,
        [int[]]$Sizes
    )

    $source = [System.Drawing.Image]::FromFile($SourcePng)
    $payloads = @()
    foreach ($size in $Sizes) {
        $bitmap = New-Object System.Drawing.Bitmap($size, $size)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($source, 0, 0, $size, $size)
        $graphics.Dispose()

        $stream = New-Object System.IO.MemoryStream
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        # 逗号包裹防止 PowerShell 输出流把 byte[] 逐元素展开
        $payloads += , ($stream.ToArray())
    }
    $source.Dispose()

    $out = New-Object System.IO.MemoryStream
    $writer = New-Object System.IO.BinaryWriter($out)
    $writer.Write([uint16]0)                 # Reserved
    $writer.Write([uint16]1)                 # Type = icon
    $writer.Write([uint16]$Sizes.Count)      # Count

    $offset = 6 + 16 * $Sizes.Count
    for ($i = 0; $i -lt $Sizes.Count; $i++) {
        $size = $Sizes[$i]
        $payload = $payloads[$i]
        $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))   # Width（256 记为 0）
        $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))   # Height
        $writer.Write([byte]0)               # ColorCount
        $writer.Write([byte]0)               # Reserved
        $writer.Write([uint16]1)             # Planes
        $writer.Write([uint16]32)            # BitCount
        $writer.Write([uint32]$payload.Length)
        $writer.Write([uint32]$offset)
        $offset += $payload.Length
    }
    foreach ($payload in $payloads) {
        $writer.Write($payload)
    }
    $writer.Flush()

    $target = [System.IO.Path]::GetFullPath($OutputIco)
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
    [System.IO.File]::WriteAllBytes($target, $out.ToArray())
    $writer.Dispose()
    Write-Output "已生成 $target（$((Get-Item $target).Length) 字节，尺寸 $($Sizes -join '/')）"
}

$appRoot = [System.IO.Path]::GetFullPath($AppRoot)
$assets = Join-Path $appRoot "Assets"

# exe 嵌入图标：主 logo 多尺寸
Convert-PngToIco -SourcePng (Join-Path $macAssets "BrandLogo.imageset\logo.png") `
    -OutputIco (Join-Path $appRoot "app.ico") -Sizes @(256, 48, 32, 16)

# 托盘默认与状态变体（源与 macOS 菜单栏同源：BrandLogo/BrandRun/BrandSleep/BrandTun）
Convert-PngToIco -SourcePng (Join-Path $macAssets "BrandLogo.imageset\logo.png") `
    -OutputIco (Join-Path $assets "tray.ico") -Sizes @(32)
Convert-PngToIco -SourcePng (Join-Path $macAssets "BrandRun.imageset\icon-run.png") `
    -OutputIco (Join-Path $assets "tray-run.ico") -Sizes @(32)
Convert-PngToIco -SourcePng (Join-Path $macAssets "BrandSleep.imageset\icon-sleep.png") `
    -OutputIco (Join-Path $assets "tray-sleep.ico") -Sizes @(32)
Convert-PngToIco -SourcePng (Join-Path $macAssets "BrandTun.imageset\icon-tun.png") `
    -OutputIco (Join-Path $assets "tray-tun.ico") -Sizes @(32)
