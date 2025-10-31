#!/usr/bin/env pwsh
# 测试 Debug 版本的控制台输出

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Testing Debug Console Output" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# 检查 debug exe 是否存在
$debugExe = "MoveWithSymlinkWPF\bin\publish\win-x64-debug\目录迁移工具-v1.0.3-debug.exe"

if (-not (Test-Path $debugExe)) {
    Write-Host "Debug executable not found. Building..." -ForegroundColor Yellow
    Write-Host ""
    
    # 发布 debug 版本
    .\publish.ps1 -m debug -s
    
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Failed to build debug version"
        exit 1
    }
}

Write-Host ""
Write-Host "Debug executable found: $debugExe" -ForegroundColor Green
Write-Host ""
Write-Host "Launching debug version..." -ForegroundColor Cyan
Write-Host "You should see:" -ForegroundColor Yellow
Write-Host "  1. A console window with debug output" -ForegroundColor White
Write-Host "  2. Application startup messages" -ForegroundColor White
Write-Host "  3. Real-time log messages during operations" -ForegroundColor White
Write-Host ""

# 启动 debug 版本
Start-Process -FilePath $debugExe -Verb RunAs

Write-Host "✓ Debug application launched" -ForegroundColor Green
Write-Host ""
Write-Host "If you don't see console output:" -ForegroundColor Yellow
Write-Host "  1. Make sure you're running the -debug.exe version" -ForegroundColor White
Write-Host "  2. Check that the app was built with -c Debug configuration" -ForegroundColor White
Write-Host "  3. Verify that ConsoleHelper.AllocateConsole() was called" -ForegroundColor White
Write-Host ""

