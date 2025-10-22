#!/usr/bin/env pwsh
# Quick launch script for MoveWithSymlink WPF

$exePath = "MoveWithSymlinkWPF\bin\publish\win-x64\MoveWithSymlinkWPF.exe"

if (Test-Path $exePath) {
    Write-Host "启动目录迁移工具 (WPF 单文件版本)..." -ForegroundColor Cyan
    Start-Process $exePath
} else {
    Write-Host "错误: 未找到可执行文件" -ForegroundColor Red
    Write-Host "请先运行发布脚本: .\publish-wpf.ps1" -ForegroundColor Yellow
    Write-Host ""
    Read-Host "按 Enter 键退出"
}

