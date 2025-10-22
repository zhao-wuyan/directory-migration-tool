#!/usr/bin/env pwsh
# Advanced Publish script with multiple build profiles

param(
    [Parameter(Mandatory=$false)]
    [ValidateSet('standard', 'fast', 'all')]
    [string]$Profile = 'standard'
)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Advanced Publish - MoveWithSymlink WPF" -ForegroundColor Cyan
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

# Function to publish with a specific profile
function Publish-Profile {
    param(
        [string]$ProfileName,
        [string]$Description,
        [string]$ExpectedSize
    )
    
    Write-Host "----------------------------------------" -ForegroundColor Yellow
    Write-Host "Publishing: $Description" -ForegroundColor Yellow
    Write-Host "Profile: $ProfileName" -ForegroundColor Gray
    Write-Host "Expected Size: $ExpectedSize" -ForegroundColor Gray
    Write-Host "----------------------------------------" -ForegroundColor Yellow
    
    dotnet publish MoveWithSymlinkWPF\MoveWithSymlinkWPF.csproj `
        -p:PublishProfile=$ProfileName `
        -c Release `
        --nologo
    
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to publish with profile: $ProfileName"
        return $false
    }
    
    # Get file info and rename with version
    $publishDir = "MoveWithSymlinkWPF\bin\publish\$ProfileName"
    $originalExe = "$publishDir\目录迁移工具.exe"
    $versionedExe = "$publishDir\目录迁移工具-v$newVersion.exe"
    
    # Rename exe with version
    if (Test-Path $originalExe) {
        Move-Item -Path $originalExe -Destination $versionedExe -Force
    }
    
    $exeFile = Get-Item $versionedExe -ErrorAction SilentlyContinue
    
    if ($exeFile) {
        $exeSize = [math]::Round($exeFile.Length/1MB, 2)
        Write-Host "✓ Success! " -ForegroundColor Green -NoNewline
        Write-Host "File: 目录迁移工具-v$newVersion.exe" -ForegroundColor Yellow
        Write-Host "  Size: $exeSize MB" -ForegroundColor Gray
        Write-Host "  Location: $publishDir\" -ForegroundColor Gray
    }
    
    Write-Host ""
    return $true
}

# Clean previous publish
if ($Profile -eq 'all') {
    Write-Host "Cleaning all previous publish directories..." -ForegroundColor Yellow
    Remove-Item -Path "MoveWithSymlinkWPF\bin\publish" -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "Cleaning previous publish for profile: $Profile..." -ForegroundColor Yellow
    Remove-Item -Path "MoveWithSymlinkWPF\bin\publish\$Profile" -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host ""

# Publish based on profile selection
$success = $true

switch ($Profile) {
    'standard' {
        $success = Publish-Profile 'win-x64' '标准版本（推荐）' '约 74 MB'
    }
    'fast' {
        $success = Publish-Profile 'win-x64-fast' '快速启动版本（ReadyToRun）' '约 95 MB'
    }
    'all' {
        Write-Host "Building all profiles..." -ForegroundColor Cyan
        Write-Host ""
        
        $success = Publish-Profile 'win-x64' '标准版本' '约 74 MB'
        if ($success) {
            $success = Publish-Profile 'win-x64-fast' '快速启动版本' '约 95 MB'
        }
    }
}

if (-not $success) {
    Write-Error "Publishing failed!"
    exit 1
}

# Summary
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  发布完成！" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "版本号: " -NoNewline
Write-Host "v$newVersion" -ForegroundColor Magenta
Write-Host ""

Write-Host "配置说明:" -ForegroundColor Cyan
Write-Host ""

Write-Host "1. 标准版本 (win-x64) - 推荐" -ForegroundColor Yellow
Write-Host "   大小: ~74 MB" -ForegroundColor Gray
Write-Host "   特点: 平衡体积和功能，包含完整运行时，无调试符号" -ForegroundColor Gray
Write-Host "   使用: .\publish-advanced.ps1 standard" -ForegroundColor White
Write-Host ""

Write-Host "2. 快速启动版本 (win-x64-fast)" -ForegroundColor Yellow
Write-Host "   大小: ~95 MB" -ForegroundColor Gray
Write-Host "   特点: ReadyToRun 预编译，启动速度更快" -ForegroundColor Gray
Write-Host "   使用: .\publish-advanced.ps1 fast" -ForegroundColor White
Write-Host ""

Write-Host "3. 编译所有版本" -ForegroundColor Yellow
Write-Host "   使用: .\publish-advanced.ps1 all" -ForegroundColor White
Write-Host ""

Write-Host "为什么是 74 MB？" -ForegroundColor Cyan
Write-Host "  • .NET 8 运行时: ~50 MB" -ForegroundColor Gray
Write-Host "  • WPF 框架: ~15 MB" -ForegroundColor Gray
Write-Host "  • 应用程序代码: ~5 MB" -ForegroundColor Gray
Write-Host "  • 其他依赖: ~4 MB" -ForegroundColor Gray
Write-Host ""

Write-Host "大小差异原因:" -ForegroundColor Cyan
Write-Host "  • 标准版 (~74MB): 包含完整运行时，不包含调试符号" -ForegroundColor Gray
Write-Host "  • 快速版 (~95MB): 启用 ReadyToRun 预编译 (+20-30 MB)" -ForegroundColor Gray
Write-Host "  • 调试版 (~76MB): 包含 .pdb 调试符号文件 (+2 MB)" -ForegroundColor Gray
Write-Host ""

Write-Host "注意: WPF 应用不适合使用代码裁剪（会导致运行时错误）" -ForegroundColor Yellow
Write-Host ""

Write-Host "作者: " -NoNewline
Write-Host "诏无言" -ForegroundColor Yellow

