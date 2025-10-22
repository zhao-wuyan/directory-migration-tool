#!/usr/bin/env pwsh
# 验证应用程序 manifest 配置

Write-Host "=== 验证 WPF 应用程序管理员权限配置 ===" -ForegroundColor Cyan
Write-Host ""

# 检查 manifest 文件是否存在
$manifestPath = "MoveWithSymlinkWPF\app.manifest"
if (Test-Path $manifestPath) {
    Write-Host "✓ Manifest 文件存在: $manifestPath" -ForegroundColor Green
    
    # 检查 manifest 内容
    $content = Get-Content $manifestPath -Raw
    if ($content -match 'level="requireAdministrator"') {
        Write-Host "✓ Manifest 配置正确: requireAdministrator" -ForegroundColor Green
    } else {
        Write-Host "✗ Manifest 配置错误: 未找到 requireAdministrator" -ForegroundColor Red
    }
} else {
    Write-Host "✗ Manifest 文件不存在" -ForegroundColor Red
}

Write-Host ""

# 检查 csproj 是否引用 manifest
$csprojPath = "MoveWithSymlinkWPF\MoveWithSymlinkWPF.csproj"
if (Test-Path $csprojPath) {
    $content = Get-Content $csprojPath -Raw
    if ($content -match '<ApplicationManifest>app\.manifest</ApplicationManifest>') {
        Write-Host "✓ .csproj 文件已引用 manifest" -ForegroundColor Green
    } else {
        Write-Host "✗ .csproj 文件未引用 manifest" -ForegroundColor Red
    }
} else {
    Write-Host "✗ .csproj 文件不存在" -ForegroundColor Red
}

Write-Host ""

# 检查已发布的 exe 是否存在
$exePath = "MoveWithSymlinkWPF\bin\publish\win-x64\目录迁移工具.exe"
if (Test-Path $exePath) {
    $fileInfo = Get-Item $exePath
    Write-Host "✓ 已发布的 exe 存在" -ForegroundColor Green
    Write-Host "  路径: $exePath" -ForegroundColor Gray
    Write-Host "  大小: $([math]::Round($fileInfo.Length / 1MB, 2)) MB" -ForegroundColor Gray
    Write-Host "  修改时间: $($fileInfo.LastWriteTime)" -ForegroundColor Gray
    
    # 比较 manifest 和 exe 的修改时间
    if (Test-Path $manifestPath) {
        $manifestInfo = Get-Item $manifestPath
        if ($manifestInfo.LastWriteTime -gt $fileInfo.LastWriteTime) {
            Write-Host ""
            Write-Host "⚠ 警告: manifest 文件比 exe 新，需要重新发布！" -ForegroundColor Yellow
            Write-Host "  运行命令: .\publish.ps1" -ForegroundColor Yellow
        } else {
            Write-Host "  ✓ exe 版本是最新的" -ForegroundColor Green
        }
    }
} else {
    Write-Host "✗ 已发布的 exe 不存在" -ForegroundColor Red
    Write-Host "  请运行: .\publish.ps1" -ForegroundColor Yellow
}

Write-Host ""
Write-Host "=== 验证完成 ===" -ForegroundColor Cyan
Write-Host ""

# 提供下一步建议
if (Test-Path $exePath) {
    $manifestInfo = Get-Item $manifestPath
    $fileInfo = Get-Item $exePath
    
    if ($manifestInfo.LastWriteTime -gt $fileInfo.LastWriteTime) {
        Write-Host "下一步: 重新发布应用程序" -ForegroundColor Yellow
        Write-Host "  .\publish.ps1" -ForegroundColor White
    } else {
        Write-Host "✓ 配置完成！您可以测试应用程序了" -ForegroundColor Green
        Write-Host ""
        Write-Host "测试方法:" -ForegroundColor Cyan
        Write-Host "1. 双击 exe 文件，应该会弹出 UAC 对话框" -ForegroundColor White
        Write-Host "2. 或运行: .\run.ps1" -ForegroundColor White
    }
} else {
    Write-Host "下一步: 发布应用程序" -ForegroundColor Yellow
    Write-Host "  .\publish.ps1" -ForegroundColor White
}

Write-Host ""

