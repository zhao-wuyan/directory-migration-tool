#!/usr/bin/env pwsh
# 清理构建中间产物脚本

param(
    [Parameter(Mandatory=$false)]
    [switch]$KeepPublish,
    
    [Parameter(Mandatory=$false)]
    [switch]$Force
)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  构建清理工具" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Define directories to clean
$itemsToClean = @()

# Always clean obj directories
$objDirs = Get-ChildItem -Path . -Recurse -Directory -Filter "obj" -ErrorAction SilentlyContinue
if ($objDirs) {
    $itemsToClean += $objDirs
}

# Always clean win-x64 intermediate directories (but not publish directories)
$winX64Dirs = Get-ChildItem -Path "MoveWithSymlinkWPF\bin\Release\net8.0-windows10.0.19041.0\win-x64" -ErrorAction SilentlyContinue
if ($winX64Dirs) {
    $itemsToClean += $winX64Dirs
}

$migrationWinX64Dirs = Get-ChildItem -Path "MigrationCore\bin\Release\net8.0-windows10.0.19041.0\win-x64" -ErrorAction SilentlyContinue
if ($migrationWinX64Dirs) {
    $itemsToClean += $migrationWinX64Dirs
}

# Clean bin directories if not keeping publish
if (-not $KeepPublish) {
    $binDirs = Get-ChildItem -Path . -Recurse -Directory -Filter "bin" -ErrorAction SilentlyContinue
    if ($binDirs) {
        $itemsToClean += $binDirs
    }
}

if ($itemsToClean.Count -eq 0) {
    Write-Host "✓ No intermediate files to clean" -ForegroundColor Green
    exit 0
}

# Calculate total size
$totalSize = 0
foreach ($item in $itemsToClean) {
    if (Test-Path $item) {
        $size = (Get-ChildItem -Path $item -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
        $totalSize += $size
    }
}

$totalSizeMB = [math]::Round($totalSize / 1MB, 2)

Write-Host "将要清理的项目:" -ForegroundColor Yellow
Write-Host ""

foreach ($item in $itemsToClean) {
    if (Test-Path $item) {
        $size = (Get-ChildItem -Path $item -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
        $sizeMB = [math]::Round($size / 1MB, 2)
        $relativePath = Resolve-Path -Relative $item.FullName
        Write-Host "  • $relativePath" -NoNewline
        Write-Host " ($sizeMB MB)" -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "总计: " -NoNewline
Write-Host "$totalSizeMB MB" -ForegroundColor Magenta
Write-Host ""

if ($KeepPublish) {
    Write-Host "注意: 发布文件 (bin/publish) 将被保留" -ForegroundColor Yellow
    Write-Host ""
}

if (-not $Force) {
    $confirm = Read-Host "确认删除这些文件？ [y/N]"
    if ($confirm -ne "y" -and $confirm -ne "Y") {
        Write-Host "已取消" -ForegroundColor Yellow
        exit 0
    }
}

Write-Host ""
Write-Host "正在清理..." -ForegroundColor Cyan

$deletedCount = 0
$deletedSize = 0
$errors = @()

foreach ($item in $itemsToClean) {
    if (Test-Path $item) {
        try {
            $size = (Get-ChildItem -Path $item -Recurse -File -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum).Sum
            Remove-Item -Path $item -Recurse -Force -ErrorAction Stop
            $deletedCount++
            $deletedSize += $size
            Write-Host "  ✓ Deleted: $($item.FullName)" -ForegroundColor Green
        } catch {
            $errors += [PSCustomObject]@{
                Path = $item.FullName
                Error = $_.Exception.Message
            }
            Write-Host "  ✗ Failed: $($item.FullName)" -ForegroundColor Red
        }
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  清理完成" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "已删除: " -NoNewline
Write-Host "$deletedCount" -ForegroundColor Green -NoNewline
Write-Host " 个目录"
Write-Host "释放空间: " -NoNewline
Write-Host "$([math]::Round($deletedSize / 1MB, 2)) MB" -ForegroundColor Magenta
Write-Host ""

if ($errors.Count -gt 0) {
    Write-Host "失败项目:" -ForegroundColor Red
    foreach ($error in $errors) {
        Write-Host "  • $($error.Path)" -ForegroundColor Red
        Write-Host "    原因: $($error.Error)" -ForegroundColor Gray
    }
    Write-Host ""
}

if ($KeepPublish) {
    Write-Host "提示: 发布文件已保留在 bin/publish 目录中" -ForegroundColor Yellow
    Write-Host ""
    
    # Show preserved publish files
    $publishFiles = Get-ChildItem -Path "MoveWithSymlinkWPF\bin\publish" -Recurse -File -Filter "*.exe" -ErrorAction SilentlyContinue
    if ($publishFiles) {
        Write-Host "已保留的发布文件:" -ForegroundColor Green
        foreach ($file in $publishFiles) {
            $size = [math]::Round($file.Length / 1MB, 2)
            Write-Host "  • $($file.Name) ($size MB)" -ForegroundColor White
        }
        Write-Host ""
    }
}

Write-Host "使用说明:" -ForegroundColor Cyan
Write-Host "  .\clean-build.ps1              # 清理所有构建文件" -ForegroundColor White
Write-Host "  .\clean-build.ps1 -KeepPublish # 保留发布文件" -ForegroundColor White
Write-Host "  .\clean-build.ps1 -Force       # 不询问直接清理" -ForegroundColor White
Write-Host ""

