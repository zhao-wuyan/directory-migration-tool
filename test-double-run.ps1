#!/usr/bin/env pwsh
# Test double increment

Write-Host "=== Simulating Two Consecutive Runs ===" -ForegroundColor Cyan
Write-Host ""

# First run
Write-Host "【第一次运行】" -ForegroundColor Green
$v1 = Get-Content version.json | ConvertFrom-Json
$current1 = "$($v1.major).$($v1.minor).$($v1.patch)"
Write-Host "Current: $current1" -ForegroundColor Yellow
$v1.patch++
$new1 = "$($v1.major).$($v1.minor).$($v1.patch)"
Write-Host "New: $new1" -ForegroundColor Cyan
$v1 | ConvertTo-Json | Set-Content version.json
Write-Host ""

# Second run
Write-Host "【第二次运行】" -ForegroundColor Green
$v2 = Get-Content version.json | ConvertFrom-Json
$current2 = "$($v2.major).$($v2.minor).$($v2.patch)"
Write-Host "Current: $current2" -ForegroundColor Yellow
$v2.patch++
$new2 = "$($v2.major).$($v2.minor).$($v2.patch)"
Write-Host "New: $new2" -ForegroundColor Cyan
$v2 | ConvertTo-Json | Set-Content version.json
Write-Host ""

# Show result
Write-Host "=== Result ===" -ForegroundColor Magenta
Write-Host "Started with: $current1" -ForegroundColor White
Write-Host "First run: $current1 → $new1" -ForegroundColor White
Write-Host "Second run: $current2 → $new2" -ForegroundColor White
Write-Host ""
Write-Host "Final version.json:" -ForegroundColor Yellow
Get-Content version.json

# Restore to 1.0.0
Write-Host ""
Write-Host "Restoring version.json to 1.0.0..." -ForegroundColor Gray
@{major=1; minor=0; patch=0; description="版本号管理文件 - 每次打包时自动递增 patch 版本号"} | ConvertTo-Json | Set-Content version.json

