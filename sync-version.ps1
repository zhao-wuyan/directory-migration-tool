#!/usr/bin/env pwsh
# Sync version from version.json to .csproj file
# 从 version.json 同步版本号到 .csproj 文件

$versionFile = "version.json"
$csprojFile = "MoveWithSymlinkWPF\MoveWithSymlinkWPF.csproj"

# Check if files exist
if (-not (Test-Path $versionFile)) {
    Write-Error "Version file not found: $versionFile"
    exit 1
}

if (-not (Test-Path $csprojFile)) {
    Write-Error "Project file not found: $csprojFile"
    exit 1
}

# Read version from version.json
Write-Host "Reading version from $versionFile..." -ForegroundColor Yellow
$versionData = Get-Content $versionFile -Encoding UTF8 -Raw | ConvertFrom-Json
$version = "$($versionData.major).$($versionData.minor).$($versionData.patch)"
Write-Host "Version: $version" -ForegroundColor Cyan

# Update .csproj file
Write-Host "Updating $csprojFile..." -ForegroundColor Yellow
$csprojContent = Get-Content $csprojFile -Encoding UTF8 -Raw

$csprojContent = $csprojContent -replace '<Version>[\d.]+</Version>', "<Version>$version</Version>"
$csprojContent = $csprojContent -replace '<AssemblyVersion>[\d.]+</AssemblyVersion>', "<AssemblyVersion>$version.0</AssemblyVersion>"
$csprojContent = $csprojContent -replace '<FileVersion>[\d.]+</FileVersion>', "<FileVersion>$version.0</FileVersion>"

$csprojContent | Set-Content $csprojFile -Encoding UTF8 -NoNewline

Write-Host "Project file updated successfully!" -ForegroundColor Green
Write-Host "Version synced: v$version" -ForegroundColor Cyan

