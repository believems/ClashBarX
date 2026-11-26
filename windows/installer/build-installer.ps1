# ClashBarX 安装器构建（M4-4）
# 用法：powershell -File build-installer.ps1 [-Version 1.0.0] [-IsccPath <ISCC.exe 路径>]
# 产出：windows/dist/ClashBarX-<版本>-setup.exe（Inno Setup；自包含发布，用户无需 .NET 运行时）
param(
    [string]$Version = "1.0.0",
    [string]$IsccPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)  # windows/installer -> 仓库根
$windowsRoot = Join-Path $repoRoot "windows"
$projectPath = Join-Path $windowsRoot "src\ClashBarX.App\ClashBarX.App.csproj"
$publishDir = Join-Path $windowsRoot "src\ClashBarX.App\bin\Release\net8.0-windows\publish\win-x64"
$distDir = Join-Path $windowsRoot "dist"
$issPath = Join-Path $PSScriptRoot "clashbarx.iss"

Write-Host "[installer] 发布自包含构建（win-x64 self-contained）…"
dotnet publish $projectPath -c Release -r win-x64 --self-contained true `
    /p:Version=$Version /p:PublishDir=$publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败" }

# 解析 ISCC：优先参数，其次 PATH，再尝试常见安装位置
if (-not $IsccPath) {
    $IsccPath = (Get-Command "ISCC.exe" -ErrorAction SilentlyContinue)?.Source
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

Write-Host "[installer] Inno Setup 编译（版本 $Version）…"
$setupDir = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Force -Path $setupDir | Out-Null
# iss 内的 #define 可被命令行 /D 覆盖（Version 与 PublishDir 绝对路径）
& $IsccPath "/DVersion=$Version" "/DPublishDir=$publishDir" "/O$setupDir" $issPath
if ($LASTEXITCODE -ne 0) { throw "ISCC 编译失败" }

New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$setup = Join-Path $setupDir "ClashBarX-$Version-setup.exe"
if (-not (Test-Path $setup)) { throw "安装器未产出：$setup" }
Copy-Item $setup $distDir -Force
Write-Host "[installer] 完成：$distDir\ClashBarX-$Version-setup.exe"
