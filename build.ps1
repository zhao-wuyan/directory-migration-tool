# Build script for MoveWithSymlink solution

param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64"
)

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Building MoveWithSymlink Solution" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check if .NET 8 SDK is installed
Write-Host "Checking .NET SDK version..." -ForegroundColor Yellow
$dotnetVersion = dotnet --version
Write-Host "Found .NET SDK: $dotnetVersion" -ForegroundColor Green
Write-Host ""

# Restore NuGet packages
Write-Host "Restoring NuGet packages..." -ForegroundColor Yellow
dotnet restore MoveWithSymlink.sln
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to restore NuGet packages"
    exit 1
}
Write-Host "NuGet packages restored successfully" -ForegroundColor Green
Write-Host ""

# Build MigrationCore
Write-Host "Building MigrationCore..." -ForegroundColor Yellow
dotnet build MigrationCore/MigrationCore.csproj -c $Configuration
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to build MigrationCore"
    exit 1
}
Write-Host "MigrationCore built successfully" -ForegroundColor Green
Write-Host ""

# Build MoveWithSymlinkGUI
Write-Host "Building MoveWithSymlinkGUI..." -ForegroundColor Yellow
dotnet build MoveWithSymlinkGUI/MoveWithSymlinkGUI.csproj -c $Configuration /p:Platform=$Platform
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to build MoveWithSymlinkGUI"
    exit 1
}
Write-Host "MoveWithSymlinkGUI built successfully" -ForegroundColor Green
Write-Host ""

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Build completed successfully!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Output directory: MoveWithSymlinkGUI\bin\$Platform\$Configuration\net8.0-windows10.0.19041.0" -ForegroundColor Yellow
Write-Host ""
Write-Host "To run the GUI application:" -ForegroundColor Yellow
Write-Host "  cd MoveWithSymlinkGUI\bin\$Platform\$Configuration\net8.0-windows10.0.19041.0" -ForegroundColor White
Write-Host "  .\MoveWithSymlinkGUI.exe" -ForegroundColor White
Write-Host ""

