#!/usr/bin/env pwsh
# Publish script for MoveWithSymlink WPF Application
# 支持多种发布模式：
#   1. 自包含版本 (SelfContained): 包含完整运行时，体积大，无需安装 .NET
#   2. 框架依赖版本 (Framework-Dependent): 轻量级，需要系统安装 .NET 8.0 Desktop Runtime
#   3. 调试版本 (Debug): 显示控制台窗口，便于排查问题

param(
    [Parameter(Mandatory=$false)]
    [Alias("m")]
    [ValidateSet("all", "selfcontained", "lite", "both", "debug")]
    [string]$Mode = "selfcontained",
    
    [Parameter(Mandatory=$false)]
    [Alias("s")]
    [switch]$SkipVersionIncrement,

    [Parameter(Mandatory=$false)]
    [Alias("h")]
    [switch]$Help
)

function Show-PublishHelp {
    Write-Host "Usage: .\publish.ps1 [-Mode|-m <all|selfcontained|lite|both|debug>] [-s|-SkipVersionIncrement] [-h|-Help]" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Options:" -ForegroundColor Yellow
    Write-Host "  -Mode, -m <value>         选择发布模式 (all/selfcontained/lite/both/debug)" -ForegroundColor White
    Write-Host "  -s, -SkipVersionIncrement  跳过版本号自增 (调试场景推荐启用)" -ForegroundColor White
    Write-Host "  -h, -Help                 显示此帮助信息并退出" -ForegroundColor White
    Write-Host ""
    Write-Host "示例:" -ForegroundColor Yellow
    Write-Host "  .\publish.ps1                          # 默认发布自包含版本" -ForegroundColor Gray
    Write-Host "  .\publish.ps1 -Mode lite               # 发布轻量框架依赖版本" -ForegroundColor Gray
    Write-Host "  .\publish.ps1 -Mode both               # 同时发布两个版本" -ForegroundColor Gray
    Write-Host "  .\publish.ps1 -Mode debug -s           # 发布带控制台的调试版本" -ForegroundColor Gray
}

if ($Help) {
    Show-PublishHelp
    return
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Publishing MoveWithSymlink WPF" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$modeLower = $Mode.ToLower()

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
$newVersion = $currentVersion
$shouldIncrementVersion = (-not $SkipVersionIncrement) -and ($modeLower -ne "debug")

if ($shouldIncrementVersion) {
    $versionData.patch++
    $newVersion = "$($versionData.major).$($versionData.minor).$($versionData.patch)"
    Write-Host "New version: $newVersion" -ForegroundColor Cyan
    
    # Save updated version
    $versionData | ConvertTo-Json | Set-Content $versionFile -Encoding UTF8
    Write-Host "Version file updated" -ForegroundColor Green
} else {
    if ($SkipVersionIncrement) {
        Write-Host "Skipping version increment, using current version: $newVersion" -ForegroundColor Yellow
    }
    if ($modeLower -eq "debug") {
        Write-Host "Debug mode selected, version number not incremented" -ForegroundColor Yellow
    }
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
$publishDebug = $false

switch ($modeLower) {
    "selfcontained" { $publishSelfContained = $true }
    "lite" { $publishFrameworkDependent = $true }
    "both" { $publishSelfContained = $true; $publishFrameworkDependent = $true }
    "all" { $publishSelfContained = $true; $publishFrameworkDependent = $true }
    "debug" { $publishDebug = $true }
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

# Publish Debug Console version
if ($publishDebug) {
    Write-Host "========================================" -ForegroundColor Magenta
    Write-Host "  Publishing Debug Console Version" -ForegroundColor Magenta
    Write-Host "  调试版本（控制台可见）" -ForegroundColor Magenta
    Write-Host "========================================" -ForegroundColor Magenta
    Write-Host ""
    
    # Clean previous publish
    Write-Host "Cleaning previous debug publish..." -ForegroundColor Yellow
    Remove-Item -Path "MoveWithSymlinkWPF\bin\publish\win-x64-debug" -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host ""
    
    # Publish the WPF application with Debug configuration
    Write-Host "Publishing Debug configuration with console window enabled..." -ForegroundColor Yellow
    dotnet publish MoveWithSymlinkWPF\MoveWithSymlinkWPF.csproj `
        -p:PublishProfile=win-x64-debug `
        -c Debug
    
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to publish Debug Console version"
    } else {
        Write-Host "Debug Console version published successfully" -ForegroundColor Green
        Write-Host ""
        
        $publishDir = "MoveWithSymlinkWPF\bin\publish\win-x64-debug"
        $originalExe = "$publishDir\目录迁移工具.exe"
        $versionedExe = "$publishDir\目录迁移工具-v$newVersion-debug.exe"
        
        if (Test-Path $originalExe) {
            # 先删除目标文件（如果存在）
            if (Test-Path $versionedExe) {
                Remove-Item -Path $versionedExe -Force
            }
            Move-Item -Path $originalExe -Destination $versionedExe -Force
            Write-Host "Renamed debug executable with version suffix" -ForegroundColor Green
        }

        $originalPdb = "$publishDir\目录迁移工具.pdb"
        if (Test-Path $originalPdb) {
            $versionedPdb = "$publishDir\目录迁移工具-v$newVersion-debug.pdb"
            # 先删除目标文件（如果存在）
            if (Test-Path $versionedPdb) {
                Remove-Item -Path $versionedPdb -Force
            }
            Move-Item -Path $originalPdb -Destination $versionedPdb -Force
        }
        
        $exeFile = Get-Item $versionedExe
        $exeSize = [math]::Round($exeFile.Length/1MB, 2)
        
        $results += [PSCustomObject]@{
            Type = "Debug (Console)"
            Name = "目录迁移工具-v$newVersion-debug.exe"
            Size = "$exeSize MB"
            Path = $publishDir
            Runtime = "需要系统安装 .NET 8.0 Desktop Runtime | 控制台输出可见"
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
Write-Host "  # 发布调试版（显示控制台日志）:" -ForegroundColor White
Write-Host "  .\publish.ps1 -Mode debug -s" -ForegroundColor Gray
Write-Host ""
Write-Host "  # 不增加版本号:" -ForegroundColor White
Write-Host "  .\publish.ps1 -s" -ForegroundColor Gray
Write-Host ""
Write-Host "  # 查看脚本帮助:" -ForegroundColor White
Write-Host "  .\publish.ps1 -h" -ForegroundColor Gray
Write-Host ""
