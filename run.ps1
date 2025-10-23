#!/usr/bin/env pwsh

# Check for published single-file executable first (fastest to run)
$publishFiles = Get-ChildItem -Path "MoveWithSymlinkWPF\bin\publish\win-x64\目录迁移工具-v*.exe" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending

if ($publishFiles -and $publishFiles.Count -gt 0) {
    $exePath = $publishFiles[0].FullName
    Write-Host "Found published version: $($publishFiles[0].Name)" -ForegroundColor Green
    Write-Host "Starting application (requires admin privileges)..." -ForegroundColor Cyan
    Write-Host "Please click 'Yes' in the UAC dialog to grant admin privileges" -ForegroundColor Yellow
    
    try {
        Start-Process -FilePath $exePath -Verb RunAs
        Write-Host "Application started (admin mode)" -ForegroundColor Green
    } catch {
        Write-Error "Failed to start application: $_"
        Read-Host "Press Enter to exit"
    }
} else {
    # No published version, run with dotnet run
    Write-Host "No published version found, running with 'dotnet run'..." -ForegroundColor Yellow
    Write-Host "Note: This requires .NET SDK and will start without admin privileges" -ForegroundColor Yellow
    Write-Host "For admin mode and standalone exe, run: .\publish.ps1" -ForegroundColor Cyan
    Write-Host ""
    
    Set-Location "MoveWithSymlinkWPF"
    dotnet run -c Release
    Set-Location ..
}
