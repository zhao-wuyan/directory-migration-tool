#!/usr/bin/env pwsh
# Publish script for MoveWithSymlink WPF Application
# 支持两种发布模式：
#   1. 自包含版本 (SelfContained): 包含完整运行时，体积大，无需安装 .NET
#   2. 框架依赖版本 (Framework-Dependent): 轻量级，需要系统安装 .NET 8.0 Desktop Runtime

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet("all", "selfcontained", "lite", "both")]
    [string]$Mode = "selfcontained",
    
    [Parameter(Mandatory=$false)]
    [switch]$SkipVersionIncrement
)

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

$versionData = Get-Content $versionFile -Encoding UTF8 -Raw | ConvertFrom-Json
$currentVersion = "$($versionData.major).$($versionData.minor).$($versionData.patch)"
Write-Host "Current version: $currentVersion" -ForegroundColor Green

# Increment patch version unless skipped
if (-not $SkipVersionIncrement) {
    $versionData.patch++
    $newVersion = "$($versionData.major).$($versionData.minor).$($versionData.patch)"
    Write-Host "New version: $newVersion" -ForegroundColor Cyan
    
    # Save updated version
    $versionData | ConvertTo-Json | Set-Content $versionFile -Encoding UTF8
    Write-Host "Version file updated" -ForegroundColor Green
} else {
    $newVersion = $currentVersion
    Write-Host "Skipping version increment, using current version: $newVersion" -ForegroundColor Yellow
}

# Always update .csproj file to sync with version.json
Write-Host "Updating project file with version $newVersion..." -ForegroundColor Yellow
$csprojFile = "MoveWithSymlinkWPF\MoveWithSymlinkWPF.csproj"
$csprojContent = Get-Content $csprojFile -Encoding UTF8 -Raw

$csprojContent = $csprojContent -replace '<Version>[\d.]+</Version>', "<Version>$newVersion</Version>"
$csprojContent = $csprojContent -replace '<AssemblyVersion>[\d.]+</AssemblyVersion>', "<AssemblyVersion>$newVersion.0</AssemblyVersion>"
$csprojContent = $csprojContent -replace '<FileVersion>[\d.]+</FileVersion>', "<FileVersion>$newVersion.0</FileVersion>"

$csprojContent | Set-Content $csprojFile -Encoding UTF8 -NoNewline
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

# Determine what to publish
$publishSelfContained = $false
$publishFrameworkDependent = $false

switch ($Mode.ToLower()) {
    "selfcontained" { $publishSelfContained = $true }
    "lite" { $publishFrameworkDependent = $true }
    "both" { $publishSelfContained = $true; $publishFrameworkDependent = $true }
    "all" { $publishSelfContained = $true; $publishFrameworkDependent = $true }
}

$results = @()

# Publish Self-Contained version
if ($publishSelfContained) {
    Write-Host "========================================" -ForegroundColor Magenta
    Write-Host "  Publishing Self-Contained Version" -ForegroundColor Magenta
    Write-Host "  自包含版本（完整运行时）" -ForegroundColor Magenta
    Write-Host "========================================" -ForegroundColor Magenta
    Write-Host ""
    
    # Clean previous publish
    Write-Host "Cleaning previous publish..." -ForegroundColor Yellow
    Remove-Item -Path "MoveWithSymlinkWPF\bin\publish\win-x64" -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host ""
    
    # Publish the WPF application
    Write-Host "Publishing as single-file executable with embedded runtime..." -ForegroundColor Yellow
    dotnet publish MoveWithSymlinkWPF\MoveWithSymlinkWPF.csproj `
        -p:PublishProfile=win-x64 `
        -c Release
    
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to publish Self-Contained version"
    } else {
        Write-Host "Self-Contained version published successfully" -ForegroundColor Green
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
        
        $results += [PSCustomObject]@{
            Type = "Self-Contained"
            Name = "目录迁移工具-v$newVersion.exe"
            Size = "$exeSize MB"
            Path = $publishDir
            Runtime = "包含完整 .NET 8.0 运行时"
        }
    }
}

# Publish Framework-Dependent version
if ($publishFrameworkDependent) {
    Write-Host "========================================" -ForegroundColor Magenta
    Write-Host "  Publishing Framework-Dependent Version" -ForegroundColor Magenta
    Write-Host "  框架依赖版本（轻量级）" -ForegroundColor Magenta
    Write-Host "========================================" -ForegroundColor Magenta
    Write-Host ""
    
    # Clean previous publish
    Write-Host "Cleaning previous publish..." -ForegroundColor Yellow
    Remove-Item -Path "MoveWithSymlinkWPF\bin\publish\win-x64-framework-dependent" -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host ""
    
    # Publish the WPF application
    Write-Host "Publishing Framework-Dependent version..." -ForegroundColor Yellow
    Write-Host "Note: This version requires .NET 8.0 Desktop Runtime on target system" -ForegroundColor Yellow
    dotnet publish MoveWithSymlinkWPF\MoveWithSymlinkWPF.csproj `
        -p:PublishProfile=win-x64-framework-dependent `
        -c Release
    
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to publish Framework-Dependent version"
    } else {
        Write-Host "Framework-Dependent version published successfully" -ForegroundColor Green
        Write-Host ""
        
        # Get file info and rename with version
        $publishDir = "MoveWithSymlinkWPF\bin\publish\win-x64-framework-dependent"
        $originalExe = "$publishDir\目录迁移工具.exe"
        $versionedExe = "$publishDir\目录迁移工具-v$newVersion-lite.exe"
        
        # Rename exe with version
        if (Test-Path $originalExe) {
            Move-Item -Path $originalExe -Destination $versionedExe -Force
            Write-Host "Renamed executable with version number" -ForegroundColor Green
        }
        
        $exeFile = Get-Item $versionedExe
        $exeSize = [math]::Round($exeFile.Length/1MB, 2)
        
        $results += [PSCustomObject]@{
            Type = "Framework-Dependent"
            Name = "目录迁移工具-v$newVersion-lite.exe"
            Size = "$exeSize MB"
            Path = $publishDir
            Runtime = "需要系统安装 .NET 8.0 Desktop Runtime"
        }
    }
}

# Display summary
Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  发布完成！" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "版本号: " -NoNewline
Write-Host "v$newVersion" -ForegroundColor Magenta
Write-Host "作者: " -NoNewline
Write-Host "诏无言" -ForegroundColor Yellow
Write-Host ""

# Display results table
if ($results.Count -gt 0) {
    Write-Host "发布结果:" -ForegroundColor Cyan
    Write-Host ""
    foreach ($result in $results) {
        Write-Host "  📦 $($result.Type)" -ForegroundColor Yellow
        Write-Host "     文件名: $($result.Name)" -ForegroundColor White
        Write-Host "     大小: $($result.Size)" -ForegroundColor White
        Write-Host "     位置: $($result.Path)\" -ForegroundColor White
        Write-Host "     运行时: $($result.Runtime)" -ForegroundColor $(if ($result.Type -eq "Self-Contained") { "Green" } else { "Yellow" })
        Write-Host ""
    }
}

Write-Host "特性说明:" -ForegroundColor Cyan
Write-Host "  ✓ 单个 EXE 文件" -ForegroundColor Green
Write-Host "  ✓ 支持 Windows 10/11 (x64)" -ForegroundColor Green
Write-Host "  ✓ 启动时自动申请管理员权限" -ForegroundColor Green
Write-Host ""

if ($publishFrameworkDependent) {
    Write-Host "框架依赖版本系统要求:" -ForegroundColor Yellow
    Write-Host "  目标系统需要安装 .NET 8.0 Desktop Runtime" -ForegroundColor White
    Write-Host "  下载: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor White
    Write-Host "  选择: .NET Desktop Runtime 8.0.x (x64)" -ForegroundColor White
    Write-Host ""
    
    # Check if .NET 8.0 runtime is installed on current system
    Write-Host "Checking local .NET runtime..." -ForegroundColor Yellow
    $runtimes = dotnet --list-runtimes 2>$null | Select-String "Microsoft.WindowsDesktop.App 8\."
    if ($runtimes) {
        Write-Host "  ✓ .NET 8.0 Desktop Runtime is installed on this system" -ForegroundColor Green
    } else {
        Write-Host "  ✗ .NET 8.0 Desktop Runtime NOT found on this system" -ForegroundColor Red
    }
    Write-Host ""
}

Write-Host "运行应用程序:" -ForegroundColor Cyan
Write-Host "  方法1（推荐）: .\run.ps1" -ForegroundColor White
Write-Host "  方法2: 双击对应的 EXE 文件" -ForegroundColor White
Write-Host ""

Write-Host "使用说明:" -ForegroundColor Cyan
Write-Host "  # 发布自包含版本（默认）:" -ForegroundColor White
Write-Host "  .\publish.ps1" -ForegroundColor Gray
Write-Host ""
Write-Host "  # 发布轻量级版本:" -ForegroundColor White
Write-Host "  .\publish.ps1 -Mode lite" -ForegroundColor Gray
Write-Host ""
Write-Host "  # 同时发布两个版本:" -ForegroundColor White
Write-Host "  .\publish.ps1 -Mode both" -ForegroundColor Gray
Write-Host ""
Write-Host "  # 不增加版本号:" -ForegroundColor White
Write-Host "  .\publish.ps1 -SkipVersionIncrement" -ForegroundColor Gray
Write-Host ""
