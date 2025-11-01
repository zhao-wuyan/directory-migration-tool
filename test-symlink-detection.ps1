# 符号链接检测测试脚本
param(
    [string]$Path
)

Write-Host "=== 符号链接检测测试 ===" -ForegroundColor Cyan
Write-Host ""

if ([string]::IsNullOrWhiteSpace($Path)) {
    Write-Host "使用方法: .\test-symlink-detection.ps1 '路径'" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "测试已知的路径：" -ForegroundColor Green
    
    $testPaths = @(
        "C:\testMove\01",
        "C:\testMove\02",
        "C:\Users\xinghe_zwy\Downloads\test1\02"
    )
    
    foreach ($testPath in $testPaths) {
        if (Test-Path $testPath) {
            Write-Host "`n检查路径: $testPath" -ForegroundColor White
            
            $item = Get-Item $testPath -Force
            $isSymlink = ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq [System.IO.FileAttributes]::ReparsePoint
            
            Write-Host "  属性: $($item.Attributes)" -ForegroundColor Gray
            Write-Host "  是符号链接: $isSymlink" -ForegroundColor $(if ($isSymlink) { "Green" } else { "Red" })
            
            if ($isSymlink -and $item.LinkTarget) {
                Write-Host "  链接目标: $($item.LinkTarget)" -ForegroundColor Yellow
            }
        } else {
            Write-Host "`n路径不存在: $testPath" -ForegroundColor Red
        }
    }
} else {
    if (Test-Path $Path) {
        Write-Host "检查路径: $Path" -ForegroundColor White
        Write-Host ""
        
        $item = Get-Item $Path -Force
        $isSymlink = ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq [System.IO.FileAttributes]::ReparsePoint
        
        Write-Host "属性: $($item.Attributes)" -ForegroundColor Gray
        Write-Host "是符号链接: $isSymlink" -ForegroundColor $(if ($isSymlink) { "Green" } else { "Red" })
        
        if ($isSymlink) {
            Write-Host ""
            Write-Host "✅ 这是一个符号链接！" -ForegroundColor Green
            if ($item.LinkTarget) {
                Write-Host "链接目标: $($item.LinkTarget)" -ForegroundColor Yellow
            }
        } else {
            Write-Host ""
            Write-Host "❌ 这不是符号链接，是普通目录/文件" -ForegroundColor Red
        }
    } else {
        Write-Host "❌ 路径不存在: $Path" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "=== 说明 ===" -ForegroundColor Cyan
Write-Host "符号链接: C:\testMove\02 -> C:\Users\xinghe_zwy\Downloads\test1\02"
Write-Host "  ↑ 这是符号链接        ↑ 这是真实目录（目标）"
Write-Host ""
Write-Host "请在应用中选择: C:\testMove\02 (符号链接)" -ForegroundColor Green
Write-Host "不要选择: C:\Users\xinghe_zwy\Downloads\test1\02 (目标)" -ForegroundColor Red

