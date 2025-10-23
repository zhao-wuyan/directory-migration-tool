#!/usr/bin/env pwsh
# Publish script for MoveWithSymlink WPF Application (Framework-Dependent Version)
# 框架依赖版本：需要系统安装 .NET 8.0 Desktop Runtime

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Publishing Framework-Dependent Version" -ForegroundColor Cyan
Write-Host "  框架依赖版本（轻量级）" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Read version information
Write-Host "Reading version information..." -ForegroundColor Yellow
$versionFile = "version.json"
if (-not (Test-Path $versionFile)) {
    Write-Error "Version file not found: $versionFile"
    exit 1
}

$versionData = Get-Content $versionFile | ConvertFrom-Json
$currentVersion = "$($versionData.major).$($versionData.minor).$($versionData.patch)"
Write-Host "Current version: $currentVersion" -ForegroundColor Green
Write-Host ""

# Check .NET SDK
Write-Host "Checking .NET SDK version..." -ForegroundColor Yellow
$dotnetVersion = dotnet --version
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to detect .NET SDK. Please install .NET 8.0 SDK or later."
    exit 1
}
Write-Host "Found .NET SDK: $dotnetVersion" -ForegroundColor Green
Write-Host ""

# Clean previous publish
Write-Host "Cleaning previous publish..." -ForegroundColor Yellow
Remove-Item -Path "MoveWithSymlinkWPF\bin\publish\win-x64-framework-dependent" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""

# Publish the WPF application (Framework-Dependent)
Write-Host "Publishing Framework-Dependent version..." -ForegroundColor Yellow
Write-Host "Note: This version requires .NET 8.0 Desktop Runtime to be installed on target system" -ForegroundColor Yellow
dotnet publish MoveWithSymlinkWPF\MoveWithSymlinkWPF.csproj `
    -p:PublishProfile=win-x64-framework-dependent `
    -c Release

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to publish MoveWithSymlinkWPF"
    exit 1
}

Write-Host "Published successfully" -ForegroundColor Green
Write-Host ""

# Get file info and rename with version
$publishDir = "MoveWithSymlinkWPF\bin\publish\win-x64-framework-dependent"
$originalExe = "$publishDir\目录迁移工具.exe"
$versionedExe = "$publishDir\目录迁移工具-v$currentVersion-lite.exe"

# Rename exe with version
if (Test-Path $originalExe) {
    Move-Item -Path $originalExe -Destination $versionedExe -Force
    Write-Host "Renamed executable with version number" -ForegroundColor Green
}

$exeFile = Get-Item $versionedExe
$exeSize = [math]::Round($exeFile.Length/1MB, 2)

# Display output information
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  发布完成！" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "版本号: " -NoNewline
Write-Host "v$currentVersion-lite" -ForegroundColor Magenta
Write-Host "框架依赖可执行程序: " -NoNewline
Write-Host "目录迁移工具-v$currentVersion-lite.exe" -ForegroundColor Yellow
Write-Host "文件大小: " -NoNewline
Write-Host "$exeSize MB" -ForegroundColor Yellow
Write-Host "位置: " -NoNewline
Write-Host "$publishDir\" -ForegroundColor Yellow
Write-Host "作者: " -NoNewline
Write-Host "诏无言" -ForegroundColor Yellow
Write-Host ""
Write-Host "这是一个框架依赖的轻量级可执行程序：" -ForegroundColor Cyan
Write-Host "  ✓ 体积小巧（约 2-5 MB）" -ForegroundColor Green
Write-Host "  ✓ 单个 EXE 文件" -ForegroundColor Green
Write-Host "  ✓ 支持 Windows 10/11 (x64)" -ForegroundColor Green
Write-Host "  ✓ 启动时自动申请管理员权限" -ForegroundColor Green
Write-Host "  ⚠ 需要系统安装 .NET 8.0 Desktop Runtime" -ForegroundColor Yellow
Write-Host ""
Write-Host "系统要求:" -ForegroundColor Cyan
Write-Host "  目标系统需要安装 .NET 8.0 Desktop Runtime" -ForegroundColor Yellow
Write-Host "  下载地址: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor White
Write-Host "  选择: .NET Desktop Runtime 8.0.x (x64)" -ForegroundColor White
Write-Host ""
Write-Host "运行应用程序:" -ForegroundColor Cyan
Write-Host "  双击 目录迁移工具-v$currentVersion-lite.exe" -ForegroundColor White
Write-Host ""
Write-Host "对比说明:" -ForegroundColor Cyan
Write-Host "  📦 标准版 (SelfContained): ~70-100 MB, 无需安装运行时" -ForegroundColor White
Write-Host "  🪶 轻量版 (Framework-Dependent): ~2-5 MB, 需要安装运行时" -ForegroundColor White
Write-Host ""

# Check if .NET 8.0 runtime is installed on current system
Write-Host "Checking local .NET runtime..." -ForegroundColor Yellow
$runtimes = dotnet --list-runtimes 2>$null | Select-String "Microsoft.WindowsDesktop.App 8\."
if ($runtimes) {
    Write-Host "✓ .NET 8.0 Desktop Runtime is installed on this system" -ForegroundColor Green
    Write-Host "  You can run the lite version directly" -ForegroundColor Green
} else {
    Write-Host "✗ .NET 8.0 Desktop Runtime NOT found on this system" -ForegroundColor Red
    Write-Host "  Please install it before running the lite version" -ForegroundColor Yellow
}
Write-Host ""

