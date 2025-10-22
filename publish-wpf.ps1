#!/usr/bin/env pwsh
# Publish script for MoveWithSymlink WPF Application (Single-File EXE)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Publishing MoveWithSymlink WPF" -ForegroundColor Cyan
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
Remove-Item -Path "MoveWithSymlinkWPF\bin\publish" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ""

# Publish the WPF application
Write-Host "Publishing MoveWithSymlinkWPF as single-file executable..." -ForegroundColor Yellow
dotnet publish MoveWithSymlinkWPF\MoveWithSymlinkWPF.csproj `
    -p:PublishProfile=win-x64 `
    -c Release

if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to publish MoveWithSymlinkWPF"
    exit 1
}

Write-Host "MoveWithSymlinkWPF published successfully" -ForegroundColor Green
Write-Host ""

# Get file info
$exeFile = Get-Item "MoveWithSymlinkWPF\bin\publish\win-x64\MoveWithSymlinkWPF.exe"
$exeSize = [math]::Round($exeFile.Length/1MB, 2)

# Display output information
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Publish completed successfully!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Single-file executable: " -NoNewline
Write-Host "MoveWithSymlinkWPF.exe" -ForegroundColor Yellow
Write-Host "File size: " -NoNewline
Write-Host "$exeSize MB" -ForegroundColor Yellow
Write-Host "Location: " -NoNewline
Write-Host "MoveWithSymlinkWPF\bin\publish\win-x64\" -ForegroundColor Yellow
Write-Host ""
Write-Host "This is a fully self-contained WPF executable that:" -ForegroundColor Cyan
Write-Host "  ✓ Includes .NET 8.0 runtime" -ForegroundColor Green
Write-Host "  ✓ Includes all dependencies" -ForegroundColor Green
Write-Host "  ✓ Runs without installing .NET" -ForegroundColor Green
Write-Host "  ✓ Single EXE file" -ForegroundColor Green
Write-Host "  ✓ Works on Windows 10/11 (x64)" -ForegroundColor Green
Write-Host ""
Write-Host "To run the application:" -ForegroundColor Cyan
Write-Host "  .\MoveWithSymlinkWPF\bin\publish\win-x64\MoveWithSymlinkWPF.exe" -ForegroundColor White
Write-Host ""
Write-Host "Note: Administrator privileges or Developer Mode is required for creating symbolic links." -ForegroundColor Yellow
Write-Host ""
Write-Host "To copy the EXE to the current directory:" -ForegroundColor Cyan
Write-Host "  Copy-Item 'MoveWithSymlinkWPF\bin\publish\win-x64\MoveWithSymlinkWPF.exe' -Destination '.' -Force" -ForegroundColor White

