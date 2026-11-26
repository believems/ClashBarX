# 生成 ClashBarX 应用图标（src/ClashBarX.App/app.ico）
# 图形与 TrayIconService.RenderIconAsIco 保持一致：深底圆角方块 + 白色上箭头。
# 输出 256 与 32 两个尺寸（PNG 载荷 ICO，Vista+ 原生支持）。
# 用法：powershell -NoProfile -File generate-app-icon.ps1 [输出路径]

param(
    [string]$OutputPath = "$PSScriptRoot\..\src\ClashBarX.App\app.ico"
)

Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$Size) {
    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $scale = $Size / 32.0

    # 圆角背景（与 WPF 版 DrawRoundedRectangle 同参数：1,1,30,30 r7）
    $radius = 7 * $scale
    $pad = 1 * $scale
    $width = 30 * $scale
    $background = New-Object System.Drawing.Drawing2D.GraphicsPath
    $background.AddArc($pad, $pad, 2 * $radius, 2 * $radius, 180, 90)
    $background.AddArc($pad + $width - 2 * $radius, $pad, 2 * $radius, 2 * $radius, 270, 90)
    $background.AddArc($pad + $width - 2 * $radius, $pad + $width - 2 * $radius, 2 * $radius, 2 * $radius, 0, 90)
    $background.AddArc($pad, $pad + $width - 2 * $radius, 2 * $radius, 2 * $radius, 90, 90)
    $background.CloseFigure()
    $brush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(0x2D, 0x33, 0x3B))
    $graphics.FillPath($brush, $background)

    # 白色上箭头（与 WPF 版同一组顶点）
    $points = @(
        @(16, 6), @(26, 17), @(21, 17),
        @(21, 26), @(11, 26), @(11, 17), @(6, 17)
    ) | ForEach-Object {
        New-Object System.Drawing.PointF ([float]($_[0] * $scale), [float]($_[1] * $scale))
    }
    $arrow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $arrow.AddPolygon($points)
    $graphics.FillPath([System.Drawing.Brushes]::White, $arrow)

    $graphics.Dispose()
    $stream = New-Object System.IO.MemoryStream
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    # 逗号包裹防止 PowerShell 输出流把 byte[] 逐元素展开
    return , ($stream.ToArray())
}

$png256 = New-IconPng 256
$png32 = New-IconPng 32

# ICO 容器：ICONDIR(6) + N x ICONDIRENTRY(16) + PNG 载荷
$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)
$writer.Write([uint16]0)   # Reserved
$writer.Write([uint16]1)   # Type = icon
$writer.Write([uint16]2)   # Count

# 目录项 1：256（宽度 256 在字节字段中记为 0）
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([uint16]1)
$writer.Write([uint16]32)
$writer.Write([uint32]$png256.Length)
$writer.Write([uint32](6 + 2 * 16))

# 目录项 2：32
$writer.Write([byte]32)
$writer.Write([byte]32)
$writer.Write([byte]0)
$writer.Write([byte]0)
$writer.Write([uint16]1)
$writer.Write([uint16]32)
$writer.Write([uint32]$png32.Length)
$writer.Write([uint32](6 + 2 * 16 + $png256.Length))

$writer.Write($png256)
$writer.Write($png32)
$writer.Flush()

$target = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
$writer.Dispose()
Write-Output "已生成 $target（$((Get-Item $target).Length) 字节：256 + 32）"
