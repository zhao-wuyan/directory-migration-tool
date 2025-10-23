#!/usr/bin/env pwsh
# 测试发布脚本 - 演示两种发布模式的区别

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  发布版本对比测试" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

Write-Host "此脚本将演示两种发布模式的区别：" -ForegroundColor Yellow
Write-Host "  1. 自包含版本 (Self-Contained)" -ForegroundColor White
Write-Host "  2. 框架依赖版本 (Framework-Dependent)" -ForegroundColor White
Write-Host ""

$continue = Read-Host "是否继续？这将执行完整发布（不增加版本号） [Y/n]"
if ($continue -eq "n" -or $continue -eq "N") {
    Write-Host "已取消" -ForegroundColor Yellow
    exit 0
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Magenta
Write-Host "  步骤 1/2: 发布自包含版本" -ForegroundColor Magenta
Write-Host "========================================" -ForegroundColor Magenta
Write-Host ""

$startTime1 = Get-Date
.\publish.ps1 -Mode selfcontained -SkipVersionIncrement
$endTime1 = Get-Date
$duration1 = ($endTime1 - $startTime1).TotalSeconds

Write-Host ""
Write-Host "========================================" -ForegroundColor Magenta
Write-Host "  步骤 2/2: 发布框架依赖版本" -ForegroundColor Magenta
Write-Host "========================================" -ForegroundColor Magenta
Write-Host ""

$startTime2 = Get-Date
.\publish.ps1 -Mode lite -SkipVersionIncrement
$endTime2 = Get-Date
$duration2 = ($endTime2 - $startTime2).TotalSeconds

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  对比结果" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Get file sizes
$selfContainedPath = Get-ChildItem "MoveWithSymlinkWPF\bin\publish\win-x64\目录迁移工具-v*.exe" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
$frameworkDependentPath = Get-ChildItem "MoveWithSymlinkWPF\bin\publish\win-x64-framework-dependent\目录迁移工具-v*-lite.exe" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($selfContainedPath -and $frameworkDependentPath) {
    $size1 = [math]::Round($selfContainedPath.Length / 1MB, 2)
    $size2 = [math]::Round($frameworkDependentPath.Length / 1MB, 2)
    $sizeRatio = [math]::Round($size1 / $size2, 1)
    
    Write-Host "📊 文件大小对比:" -ForegroundColor Yellow
    Write-Host ""
    
    # Create comparison table
    $comparison = @(
        [PSCustomObject]@{
            版本类型 = "自包含版本"
            文件名 = $selfContainedPath.Name
            大小 = "$size1 MB"
            构建时间 = "$([math]::Round($duration1, 1)) 秒"
            运行时依赖 = "无"
            推荐场景 = "最终用户分发"
        },
        [PSCustomObject]@{
            版本类型 = "框架依赖版本"
            文件名 = $frameworkDependentPath.Name
            大小 = "$size2 MB"
            构建时间 = "$([math]::Round($duration2, 1)) 秒"
            运行时依赖 = ".NET 8.0"
            推荐场景 = "内部使用"
        }
    )
    
    $comparison | Format-Table -AutoSize
    
    Write-Host "💡 关键数据:" -ForegroundColor Yellow
    Write-Host "  • 体积差异: 自包含版本是框架依赖版本的 " -NoNewline
    Write-Host "$sizeRatio" -NoNewline -ForegroundColor Magenta
    Write-Host " 倍"
    Write-Host "  • 体积减少: " -NoNewline
    Write-Host "$([math]::Round(($size1 - $size2) / $size1 * 100, 1))%" -ForegroundColor Green
    Write-Host "  • 构建时间差: " -NoNewline
    Write-Host "$([math]::Round($duration1 - $duration2, 1)) 秒" -ForegroundColor Cyan
    Write-Host ""
    
    Write-Host "📁 输出位置:" -ForegroundColor Yellow
    Write-Host "  自包含版本:" -ForegroundColor White
    Write-Host "    $($selfContainedPath.DirectoryName)\" -ForegroundColor Gray
    Write-Host "  框架依赖版本:" -ForegroundColor White
    Write-Host "    $($frameworkDependentPath.DirectoryName)\" -ForegroundColor Gray
    Write-Host ""
    
    Write-Host "🎯 使用建议:" -ForegroundColor Yellow
    Write-Host "  • 分发给最终用户 → 使用自包含版本" -ForegroundColor White
    Write-Host "  • 内部测试/开发 → 使用框架依赖版本" -ForegroundColor White
    Write-Host "  • 网络带宽有限 → 使用框架依赖版本" -ForegroundColor White
    Write-Host "  • 不确定运行时环境 → 使用自包含版本" -ForegroundColor White
    Write-Host ""
    
    # Check runtime
    $hasRuntime = dotnet --list-runtimes 2>$null | Select-String "Microsoft.WindowsDesktop.App 8\."
    Write-Host "💻 本机运行时检查:" -ForegroundColor Yellow
    if ($hasRuntime) {
        Write-Host "  ✓ 已安装 .NET 8.0 Desktop Runtime" -ForegroundColor Green
        Write-Host "  → 两个版本都可以在本机运行" -ForegroundColor Green
    } else {
        Write-Host "  ✗ 未安装 .NET 8.0 Desktop Runtime" -ForegroundColor Red
        Write-Host "  → 仅自包含版本可以在本机运行" -ForegroundColor Yellow
        Write-Host "  → 框架依赖版本需要先安装运行时" -ForegroundColor Yellow
    }
    
} else {
    Write-Host "未找到发布文件" -ForegroundColor Red
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  测试完成" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

