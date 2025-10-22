#!/usr/bin/env pwsh
# Publish script for MoveWithSymlink GUI Application

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Publishing MoveWithSymlink GUI" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check .NET SDK
Write-Host "Checking .NET SDK version..." -ForegroundColor Yellow
$dotnetVersion = dotnet --version
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to detect .NET SDK. Please install .NET 8.0 SDK or later."
    exit 1
}
Write-Host "Found .NET SDK: $dotnetVersion" -ForegroundColor Green
Write-Host ""

# Clean previous publish
Write-Host "Cleaning previous publish..." -ForegroundColor Yellow
Remove-Item -Path "MoveWithSymlinkGUI\bin\publish" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""

# Publish the GUI application
Write-Host "Publishing MoveWithSymlinkGUI as single-file executable..." -ForegroundColor Yellow
dotnet publish MoveWithSymlinkGUI\MoveWithSymlinkGUI.csproj `
    -p:PublishProfile=win-x64 `
    -c Release

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to publish MoveWithSymlinkGUI"
    exit 1
}

Write-Host "MoveWithSymlinkGUI published successfully" -ForegroundColor Green
Write-Host ""

# Get file info
$exeFile = Get-Item "MoveWithSymlinkGUI\bin\publish\win-x64\MoveWithSymlinkGUI.exe"
$exeSize = [math]::Round($exeFile.Length/1MB, 2)

# Display output information
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Publish completed successfully!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Single-file executable: " -NoNewline
Write-Host "MoveWithSymlinkGUI.exe" -ForegroundColor Yellow
Write-Host "File size: " -NoNewline
Write-Host "$exeSize MB" -ForegroundColor Yellow
Write-Host "Location: " -NoNewline
Write-Host "MoveWithSymlinkGUI\bin\publish\win-x64\" -ForegroundColor Yellow
Write-Host ""
Write-Host "This is a fully self-contained executable that:" -ForegroundColor Cyan
Write-Host "  ✓ Includes .NET 8.0 runtime" -ForegroundColor Green
Write-Host "  ✓ Includes all dependencies" -ForegroundColor Green
Write-Host "  ✓ Runs without installing .NET" -ForegroundColor Green
Write-Host "  ✓ Single EXE file (no additional DLLs needed)" -ForegroundColor Green
Write-Host ""
Write-Host "To run the application:" -ForegroundColor Cyan
Write-Host "  .\MoveWithSymlinkGUI\bin\publish\win-x64\MoveWithSymlinkGUI.exe" -ForegroundColor White
Write-Host ""
Write-Host "Note: Administrator privileges or Developer Mode is required for creating symbolic links." -ForegroundColor Yellow

