#!/usr/bin/env pwsh
# Test version increment logic

Write-Host "=== Testing Version Increment ===" -ForegroundColor Cyan
Write-Host ""

# Read current version
$versionData = Get-Content version.json | ConvertFrom-Json

Write-Host "Before increment:" -ForegroundColor Yellow
Write-Host "  major: $($versionData.major)" -ForegroundColor White
Write-Host "  minor: $($versionData.minor)" -ForegroundColor White
Write-Host "  patch: $($versionData.patch)" -ForegroundColor White
Write-Host "  Full: $($versionData.major).$($versionData.minor).$($versionData.patch)" -ForegroundColor Cyan
Write-Host ""

# Increment patch
$versionData.patch++

Write-Host "After increment:" -ForegroundColor Yellow
Write-Host "  major: $($versionData.major)" -ForegroundColor White
Write-Host "  minor: $($versionData.minor)" -ForegroundColor White
Write-Host "  patch: $($versionData.patch)" -ForegroundColor White
Write-Host "  Full: $($versionData.major).$($versionData.minor).$($versionData.patch)" -ForegroundColor Cyan
Write-Host ""

Write-Host "JSON output:" -ForegroundColor Yellow
$versionData | ConvertTo-Json
Write-Host ""

Write-Host "Would save to version.json" -ForegroundColor Green
Write-Host "(Not saving to avoid changing actual version)" -ForegroundColor Gray

