#!/usr/bin/env pwsh
# Publish script for MoveWithSymlink WPF Application (Single-File EXE)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Publishing MoveWithSymlink WPF" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Read and update version
Write-Host "Reading version information..." -ForegroundColor Yellow
$versionFile = "version.json"
if (-not (Test-Path $versionFile)) {
    Write-Error "Version file not found: $versionFile"
    exit 1
}

$versionData = Get-Content $versionFile | ConvertFrom-Json
$currentVersion = "$($versionData.major).$($versionData.minor).$($versionData.patch)"
Write-Host "Current version: $currentVersion" -ForegroundColor Green

# Increment patch version
$versionData.patch++
$newVersion = "$($versionData.major).$($versionData.minor).$($versionData.patch)"
Write-Host "New version: $newVersion" -ForegroundColor Cyan

# Save updated version
$versionData | ConvertTo-Json | Set-Content $versionFile
Write-Host "Version file updated" -ForegroundColor Green

# Update .csproj file
Write-Host "Updating project file with new version..." -ForegroundColor Yellow
$csprojFile = "MoveWithSymlinkWPF\MoveWithSymlinkWPF.csproj"
$csprojContent = Get-Content $csprojFile -Raw

$csprojContent = $csprojContent -replace '<Version>[\d.]+</Version>', "<Version>$newVersion</Version>"
$csprojContent = $csprojContent -replace '<AssemblyVersion>[\d.]+</AssemblyVersion>', "<AssemblyVersion>$newVersion.0</AssemblyVersion>"
$csprojContent = $csprojContent -replace '<FileVersion>[\d.]+</FileVersion>', "<FileVersion>$newVersion.0</FileVersion>"

$csprojContent | Set-Content $csprojFile -NoNewline
Write-Host "Project file updated with version $newVersion" -ForegroundColor Green
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
Remove-Item -Path "MoveWithSymlinkWPF\bin\publish" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""

# Publish the WPF application
Write-Host "Publishing MoveWithSymlinkWPF as single-file executable..." -ForegroundColor Yellow
dotnet publish MoveWithSymlinkWPF\MoveWithSymlinkWPF.csproj `
    -p:PublishProfile=win-x64 `
    -c Release

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to publish MoveWithSymlinkWPF"
    exit 1
}

Write-Host "MoveWithSymlinkWPF published successfully" -ForegroundColor Green
Write-Host ""

# Get file info and rename with version
$publishDir = "MoveWithSymlinkWPF\bin\publish\win-x64"
$originalExe = "$publishDir\目录迁移工具.exe"
$versionedExe = "$publishDir\目录迁移工具-v$newVersion.exe"

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
Write-Host "v$newVersion" -ForegroundColor Magenta
Write-Host "单文件可执行程序: " -NoNewline
Write-Host "目录迁移工具-v$newVersion.exe" -ForegroundColor Yellow
Write-Host "文件大小: " -NoNewline
Write-Host "$exeSize MB" -ForegroundColor Yellow
Write-Host "位置: " -NoNewline
Write-Host "$publishDir\" -ForegroundColor Yellow
Write-Host "作者: " -NoNewline
Write-Host "诏无言" -ForegroundColor Yellow
Write-Host ""
Write-Host "这是一个完全独立的 WPF 可执行程序：" -ForegroundColor Cyan
Write-Host "  ✓ 包含 .NET 8.0 运行时" -ForegroundColor Green
Write-Host "  ✓ 包含所有依赖项" -ForegroundColor Green
Write-Host "  ✓ 无需安装 .NET" -ForegroundColor Green
Write-Host "  ✓ 单个 EXE 文件" -ForegroundColor Green
Write-Host "  ✓ 支持 Windows 10/11 (x64)" -ForegroundColor Green
Write-Host "  ✓ 启动时自动申请管理员权限" -ForegroundColor Green
Write-Host ""
Write-Host "运行应用程序:" -ForegroundColor Cyan
Write-Host "  方法1（推荐）: .\run.ps1" -ForegroundColor White
Write-Host "  方法2: 双击 目录迁移工具.exe" -ForegroundColor White
Write-Host ""
Write-Host "复制 EXE 到当前目录:" -ForegroundColor Cyan
Write-Host "  Copy-Item 'MoveWithSymlinkWPF\bin\publish\win-x64\目录迁移工具-v$newVersion.exe' -Destination '.' -Force" -ForegroundColor White

