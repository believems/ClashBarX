# ClashBarX 安装器构建（M4-4）
# 用法：powershell -File build-installer.ps1 [-Version 1.0.0] [-IsccPath <ISCC.exe 路径>] [-Lite]
# 产出：windows/dist/ClashBarX-<版本>-setup.exe（Inno Setup；自包含发布，用户无需 .NET 运行时）
#       加 -Lite：windows/dist/ClashBarX-<版本>-setup-lite.exe（框架依赖发布，体积小；用户需自备 .NET 8 Desktop Runtime）
param(
    [string]$Version = "1.0.0",
    [string]$IsccPath = "",
    [switch]$Lite
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)  # windows/installer -> 仓库根
$windowsRoot = Join-Path $repoRoot "windows"
$projectPath = Join-Path $windowsRoot "src\ClashBarX.App\ClashBarX.App.csproj"
# 两种发布各用独立目录：publish 不会清理旧文件，混用目录会把上一种发布的运行时文件/残留打进安装包
$publishFlavor = if ($Lite) { "win-x64-lite" } else { "win-x64" }
$publishDir = Join-Path $windowsRoot "src\ClashBarX.App\bin\Release\net8.0-windows\publish\$publishFlavor"
$distDir = Join-Path $windowsRoot "dist"
$issPath = Join-Path $PSScriptRoot "clashbarx.iss"

if ($Lite) {
    Write-Host "[installer] 发布框架依赖构建（win-x64 framework-dependent，需 .NET 8 Desktop Runtime）…"
    dotnet publish $projectPath -c Release -r win-x64 --self-contained false `
        /p:Version=$Version /p:PublishDir=$publishDir
} else {
    Write-Host "[installer] 发布自包含构建（win-x64 self-contained）…"
    dotnet publish $projectPath -c Release -r win-x64 --self-contained true `
        /p:Version=$Version /p:PublishDir=$publishDir
}
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

# 解析 ISCC：优先参数，其次 PATH，再尝试常见安装位置
if (-not $IsccPath) {
    $command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($command) { $IsccPath = $command.Source }
}
if (-not $IsccPath) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    )
    $IsccPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $IsccPath -or -not (Test-Path $IsccPath)) {
    throw "未找到 Inno Setup 编译器（ISCC.exe）。安装 Inno Setup 6 或经 -IsccPath 指定；CI 上用 choco install innosetup。"
}

$flavorLabel = if ($Lite) { " / lite" } else { "" }
Write-Host "[installer] Inno Setup 编译（版本 $Version$flavorLabel）…"
$setupDir = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Force -Path $setupDir | Out-Null
# iss 内的 #define 可被命令行 /D 覆盖（Version 与 PublishDir 绝对路径）；
# lite 变体经 /DLite 开启（iss 内 #ifdef Lite 切换输出文件名后缀）
$setupName = if ($Lite) { "ClashBarX-$Version-setup-lite.exe" } else { "ClashBarX-$Version-setup.exe" }
$innoArgs = @("/DVersion=$Version", "/DPublishDir=$publishDir")
if ($Lite) { $innoArgs += "/DLite" }
$innoArgs += "/O$setupDir"
$innoArgs += $issPath
& $IsccPath @innoArgs
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败" }

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$setup = Join-Path $setupDir $setupName
if (-not (Test-Path $setup)) { throw "安装器未产出：$setup" }
Copy-Item $setup $distDir -Force
Write-Host "[installer] 完成：$distDir\$setupName"
