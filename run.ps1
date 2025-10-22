#!/usr/bin/env pwsh
# Quick launch script for MoveWithSymlink WPF
# 直接以管理员身份启动 WPF 应用

$exePath = "MoveWithSymlinkWPF\bin\publish\win-x64\目录迁移工具.exe"

if (Test-Path $exePath) {
    Write-Host "正在启动目录迁移工具（需要管理员权限）..." -ForegroundColor Cyan
    Write-Host "如果弹出 UAC 对话框，请点击'是'以授予管理员权限" -ForegroundColor Yellow
    
    try {
        # 以管理员身份启动 WPF 应用
        Start-Process -FilePath $exePath -Verb RunAs
        Write-Host "✓ 应用程序已启动（管理员模式）" -ForegroundColor Green
    }
    catch {
        Write-Error "无法以管理员身份启动应用: $_"
        Write-Host "请检查是否点击了 UAC 对话框中的'是'" -ForegroundColor Yellow
        Read-Host "按 Enter 键退出"
    }
} else {
    Write-Host "错误: 未找到可执行文件" -ForegroundColor Red
    Write-Host "路径: $exePath" -ForegroundColor Yellow
    Write-Host "请先运行发布脚本: .\publish.ps1" -ForegroundColor Yellow
    Write-Host ""
    Read-Host "按 Enter 键退出"
}

