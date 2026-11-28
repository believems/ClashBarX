# ClashBarX 绿色便携版构建（M4-4）
# 用法：powershell -File build-portable.ps1 [-Version 1.0.0] [-Lite]
# 产出：windows/dist/ClashBarX-<版本>-portable-win-x64.zip（自包含 .NET，解压即用、免安装免管理员）
#       加 -Lite：windows/dist/ClashBarX-<版本>-portable-win-x64-lite.zip（框架依赖发布，体积小；
#       用户需自备 .NET 8 Desktop Runtime——对齐 build-installer.ps1 -Lite 的口径）
param(
    [string]$Version = "1.0.0",
    [string]$DotNet = "",
    [switch]$Lite
)

$ErrorActionPreference = "Stop"

# 开发机 SDK 为用户级便携安装（不在 PATH）——脚本内定位便携 SDK，CI 上 PATH 的 dotnet 直接可用
if (-not $DotNet) {
    $portable = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
    $DotNet = if (Test-Path $portable) { $portable } else { "dotnet" }
}

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)  # windows/installer -> 仓库根
$windowsRoot = Join-Path $repoRoot "windows"
$projectPath = Join-Path $windowsRoot "src\ClashBarX.App\ClashBarX.App.csproj"
$publishFlavor = if ($Lite) { "win-x64-lite" } else { "win-x64" }
$publishDir = Join-Path $windowsRoot "src\ClashBarX.App\bin\Release\net8.0-windows\publish\$publishFlavor"
$distDir = Join-Path $windowsRoot "dist"
$flavorLabel = if ($Lite) { "-lite" } else { "" }
$zipPath = Join-Path $distDir "ClashBarX-$Version-portable-win-x64$flavorLabel.zip"

if ($Lite) {
    Write-Host "[portable] 发布框架依赖构建（win-x64 lite，需 .NET 8 Desktop Runtime）…"
    & $DotNet publish $projectPath -c Release -r win-x64 --self-contained false `
        /p:Version=$Version /p:PublishDir=$publishDir
} else {
    Write-Host "[portable] 发布自包含构建（win-x64 self-contained）…"
    & $DotNet publish $projectPath -c Release -r win-x64 --self-contained true `
        /p:Version=$Version /p:PublishDir=$publishDir
}
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host "[portable] 打包 zip（解压到任意目录即可运行；数据在 %APPDATA%\ClashBarX）…"
Compress-Archive -Path (Join-Path $publishDir "*") -DestinationPath $zipPath -CompressionLevel Optimal

$hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
$hashPath = "$zipPath.sha256"
"$hash  $(Split-Path $zipPath -Leaf)" | Set-Content -Path $hashPath -NoNewline

$size = "{0:N1} MB" -f ((Get-Item $zipPath).Length / 1MB)
Write-Host "[portable] 完成：$zipPath（$size）"
Write-Host "[portable] SHA256：$hash"
