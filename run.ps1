#!/usr/bin/env pwsh
# 运行脚本 - 智能选择最佳可用版本

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  目录迁移工具 - 启动器" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check for published versions
$selfContainedFiles = Get-ChildItem -Path "MoveWithSymlinkWPF\bin\publish\win-x64\目录迁移工具-v*.exe" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
$liteFiles = Get-ChildItem -Path "MoveWithSymlinkWPF\bin\publish\win-x64-framework-dependent\目录迁移工具-v*-lite.exe" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending

$exePath = $null
$exeType = $null

# Priority: Self-Contained > Framework-Dependent > dotnet run
if ($selfContainedFiles -and $selfContainedFiles.Count -gt 0) {
    $exePath = $selfContainedFiles[0].FullName
    $exeType = "Self-Contained"
    $exeSize = [math]::Round($selfContainedFiles[0].Length/1MB, 2)
    Write-Host "✓ Found Self-Contained version: $($selfContainedFiles[0].Name)" -ForegroundColor Green
    Write-Host "  Size: $exeSize MB | Runtime: Embedded .NET 8.0" -ForegroundColor Gray
} elseif ($liteFiles -and $liteFiles.Count -gt 0) {
    # Check if .NET 8.0 Desktop Runtime is available
    $hasRuntime = dotnet --list-runtimes 2>$null | Select-String "Microsoft.WindowsDesktop.App 8\."
    
    if ($hasRuntime) {
        $exePath = $liteFiles[0].FullName
        $exeType = "Framework-Dependent"
        $exeSize = [math]::Round($liteFiles[0].Length/1MB, 2)
        Write-Host "✓ Found Framework-Dependent version: $($liteFiles[0].Name)" -ForegroundColor Green
        Write-Host "  Size: $exeSize MB | Runtime: System .NET 8.0" -ForegroundColor Gray
    } else {
        Write-Host "⚠ Framework-Dependent version found, but .NET 8.0 Desktop Runtime is not installed" -ForegroundColor Yellow
        Write-Host "  Install from: https://dotnet.microsoft.com/download/dotnet/8.0" -ForegroundColor Yellow
        Write-Host "  Falling back to 'dotnet run'..." -ForegroundColor Yellow
        $exeType = "DotnetRun"
    }
}

Write-Host ""

if ($exePath) {
    Write-Host "Starting application (requires admin privileges)..." -ForegroundColor Cyan
    Write-Host "Please click 'Yes' in the UAC dialog to grant admin privileges" -ForegroundColor Yellow
    Write-Host ""
    
    try {
        Start-Process -FilePath $exePath -Verb RunAs
        Write-Host "✓ Application started successfully ($exeType mode)" -ForegroundColor Green
    } catch {
        Write-Error "Failed to start application: $_"
        Read-Host "Press Enter to exit"
    }
} else {
    # No published version, run with dotnet run
    Write-Host "No published version found, running with 'dotnet run'..." -ForegroundColor Yellow
    Write-Host "Note: This requires .NET SDK and will start without admin privileges" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "To build optimized versions, run:" -ForegroundColor Cyan
    Write-Host "  .\publish.ps1              # Self-Contained (~70-100 MB, no runtime needed)" -ForegroundColor White
    Write-Host "  .\publish.ps1 -Mode lite   # Framework-Dependent (~2-5 MB, runtime needed)" -ForegroundColor White
    Write-Host "  .\publish.ps1 -Mode both   # Build both versions" -ForegroundColor White
    Write-Host ""
    
    try {
        Set-Location "MoveWithSymlinkWPF"
        dotnet run -c Release
        Set-Location ..
    } catch {
        Write-Error "Failed to run with dotnet: $_"
        Set-Location ..
        Read-Host "Press Enter to exit"
    }
}
