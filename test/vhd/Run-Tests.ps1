[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true)][guid]$RunId,
    [Parameter(Mandatory = $true)]
    [ValidateSet('Boundary', 'Identity', 'Probe', 'Copy', 'Switch', 'Backup', 'Recycle', 'Verification', 'Policy', 'MftScan', 'Batch', 'All')]
    [string]$Suite,
    [switch]$LaunchUi
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-LabDirectory([string]$Path) {
    $null = [DiskLabPath]::AssertOrdinaryPath($Path, $true)
    $null = [IO.Directory]::CreateDirectory($Path)
    $null = [DiskLabPath]::AssertOrdinaryPath($Path, $false)
    return $Path
}

function Copy-LabBuildTree([string]$Source, [string]$Destination, [switch]$ProjectRoot) {
    $null = [DiskLabPath]::AssertOrdinaryPath($Source, $false)
    $null = Get-LabDirectory $Destination
    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($Source)) {
        $name = [IO.Path]::GetFileName($entry)
        if ($name -eq '.git' -or ($ProjectRoot -and $name -in @('bin', 'obj'))) { continue }
        $identity = [DiskLabPath]::AssertOrdinaryPath($entry, $false)
        $destinationEntry = Join-Path $Destination $name
        if ($identity.Attributes -band [IO.FileAttributes]::Directory) {
            Copy-LabBuildTree $entry $destinationEntry
        } else {
            $null = [DiskLabPath]::AssertOrdinaryPath($destinationEntry, $true)
            [IO.File]::Copy($entry, $destinationEntry, $false)
        }
    }
}

function Write-LabBuildFile([string]$Path, [string]$Text) {
    if ($null -ne [DiskLabPath]::AssertOrdinaryPath($Path, $true)) {
        throw "Build file already exists: $Path"
    }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Text)
        $stream.Write($bytes, 0, $bytes.Length)
    } finally { $stream.Dispose() }
}

function Publish-LabProject([string]$Project, [string]$Output, [string]$Log) {
    Assert-LabHostSpace -MinimumBytes 1GB
    $null = [DiskLabPath]::AssertOrdinaryPath($Log, $true)
    & $dotnet publish $Project -c Release -r win-x64 --self-contained true `
        --artifacts-path $artifacts --output $Output `
        '-p:PublishSingleFile=false' '-p:PublishTrimmed=false' '-p:PublishReadyToRun=false' `
        '-p:ImportDirectoryBuildProps=false' '-p:ImportDirectoryBuildTargets=false' `
        '-p:ImportDirectoryPackagesProps=false' '-p:RuntimeIdentifiers=win-x64' `
        "-p:RestoreConfigFile=$nugetConfig" '-p:NuGetAudit=false' | Tee-Object -FilePath $Log
    if ($LASTEXITCODE -ne 0) {
        throw "Offline publish failed for $Project (exit $LASTEXITCODE). See $Log. Missing packages must be supplied separately."
    }
}


. (Join-Path $PSScriptRoot 'TestDiskGuard.ps1')
$null = Assert-LabPrerequisites
Initialize-LabGuard
$workRoot = Get-LabWorkRoot -RunId $RunId
$lock = Enter-LabLock -WorkRoot $workRoot
$oldEnvironment = @{}
try {
    $lab = Read-LabRecord -RunId $RunId
    $null = Assert-LabMounted -Lab $lab
    Assert-LabHostSpace -MinimumBytes 4GB
    $dotnet = (Get-Command dotnet.exe -CommandType Application).Source
    $installedSdks = @(& $dotnet --list-sdks)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot list installed .NET SDKs.' }
    $sdkVersions = @(foreach ($line in $installedSdks) {
        if ($line -match '^(\d+\.\d+\.\d+)\s+\[' -and ([version]$Matches[1]).Major -ge 8) {
            [version]$Matches[1]
        }
    })
    $sdk = $sdkVersions | Where-Object Major -eq 8 | Sort-Object -Descending | Select-Object -First 1
    if (-not $sdk) { $sdk = $sdkVersions | Sort-Object -Descending | Select-Object -First 1 }
    if (-not $sdk) { throw 'A stable .NET SDK 8 or later is required.' }
    $cacheOutput = @(& $dotnet nuget locals global-packages --list)
    if ($LASTEXITCODE -ne 0 -or $cacheOutput.Count -ne 1 -or $cacheOutput[0] -notmatch '^global-packages:\s*(.+)$') {
        throw 'Cannot locate the existing NuGet global package cache.'
    }
    $packageSource = $Matches[1].Trim().TrimEnd('\')
    $null = [DiskLabPath]::AssertOrdinaryPath($packageSource, $false)
    if (-not [IO.Directory]::Exists($packageSource)) { throw "Missing offline package source: $packageSource" }

    $attempt = [guid]::NewGuid().ToString('D')
    $results = Get-LabDirectory (Join-Path $workRoot 'Results')
    $buildSource = Get-LabDirectory (Join-Path (Get-LabDirectory (Join-Path $workRoot 'BuildSource')) $attempt)
    $sdkConfig = @{ sdk = @{ version = $sdk.ToString(); rollForward = 'disable' } } | ConvertTo-Json -Depth 3
    Write-LabBuildFile (Join-Path $buildSource 'global.json') $sdkConfig
    Write-Host "Offline build SDK: $sdk"
    $artifacts = Get-LabDirectory (Join-Path (Get-LabDirectory (Join-Path $workRoot 'BuildArtifacts')) $attempt)
    $app = Get-LabDirectory (Join-Path (Get-LabDirectory (Join-Path $workRoot 'App')) $attempt)
    $harness = Get-LabDirectory (Join-Path (Get-LabDirectory (Join-Path $workRoot 'Harness')) $attempt)
    $cache = Get-LabDirectory (Join-Path $artifacts 'Cache')
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
    foreach ($project in 'MigrationCore', 'MoveWithSymlinkWPF') {
        Copy-LabBuildTree (Join-Path $repo $project) (Join-Path $buildSource $project) -ProjectRoot
    }
    $testSource = Get-LabDirectory (Join-Path $buildSource 'test')
    Copy-LabBuildTree (Join-Path $repo 'test\MigrationSafetyHarness') (Join-Path $testSource 'MigrationSafetyHarness') -ProjectRoot
    $vhdSource = Get-LabDirectory (Join-Path $testSource 'vhd')
    foreach ($file in 'version.json', 'test\vhd\TestDiskPath.cs') {
        $inputFile = Join-Path $repo $file
        $outputFile = Join-Path $buildSource $file
        $null = [DiskLabPath]::AssertOrdinaryPath($inputFile, $false)
        $null = [DiskLabPath]::AssertOrdinaryPath($outputFile, $true)
        [IO.File]::Copy($inputFile, $outputFile, $false)
    }

    $source = [IO.Path]::Combine(($lab.driveLetters[0] + ':\'), 'Data\Sources\Sample')
    $target = [IO.Path]::Combine(($lab.driveLetters[1] + ':\'), 'Data\Targets')
    $null = [DiskLabPath]::Check($source, $source, $lab.volumeGuidPaths[0], [uint32]$lab.volumeSerials[0], $false, $false)
    $null = [DiskLabPath]::Check($target, $target, $lab.volumeGuidPaths[1], [uint32]$lab.volumeSerials[1], $false, $false)
    $config = [ordered]@{
        defaults = [ordered]@{
            targetStrategy = 'unified'
            unifiedTargetRoot = $target
            robocopyThreads = 2
        }
        profiles = @()
        standaloneSources = @([ordered]@{ displayName = 'Test Lab Sample'; absolutePath = $source; enabled = $true })
    } | ConvertTo-Json -Depth 6
    Write-LabBuildFile (Join-Path $buildSource 'quick-migrate.json') $config
    $nugetConfig = Join-Path $buildSource 'NuGet.Config'
    $escapedCache = [Security.SecurityElement]::Escape($packageSource)
    Write-LabBuildFile $nugetConfig @"
<configuration>
  <packageSources>
    <clear />
    <add key="existing-local-cache" value="$escapedCache" />
  </packageSources>
  <fallbackPackageFolders><clear /></fallbackPackageFolders>
</configuration>
"@
    $processEnvironment = @{
        DOTNET_CLI_HOME = Get-LabDirectory (Join-Path $cache 'DotNet')
        # A fresh DOTNET_CLI_HOME makes the SDK first-run append its tools dir to HKCU PATH (dotnet/sdk#56512).
        DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
        NUGET_PACKAGES = Get-LabDirectory (Join-Path $cache 'Packages')
        NUGET_HTTP_CACHE_PATH = Get-LabDirectory (Join-Path $cache 'Http')
        TEMP = Get-LabDirectory (Join-Path $cache 'Temp')
        TMP = Join-Path $cache 'Temp'
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
        DOTNET_NOLOGO = '1'
        MIGRATION_TEST_LAB_RUN_ID = $RunId.ToString('D')
        MIGRATION_TEST_LAB_APP = $app
    }
    foreach ($key in $processEnvironment.Keys) {
        $oldEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, $processEnvironment[$key], 'Process')
    }

    $appProject = Join-Path $buildSource 'MoveWithSymlinkWPF\MoveWithSymlinkWPF.csproj'
    $harnessProject = Join-Path $testSource 'MigrationSafetyHarness\MigrationSafetyHarness.csproj'
    Push-Location -LiteralPath $buildSource
    try {
        Publish-LabProject $harnessProject $harness (Join-Path $results "$attempt-harness-build.log")
        Publish-LabProject $appProject $app (Join-Path $results "$attempt-app-build.log")
    } finally { Pop-Location }
    $externalConfig = Join-Path $app 'quick-migrate.json'
    Write-LabBuildFile $externalConfig $config
    Write-LabBuildFile (Join-Path $harness 'quick-migrate.json') $config
    if ([IO.File]::ReadAllText($externalConfig) -cne $config) { throw 'External test configuration mismatch.' }
    foreach ($packageFile in 'MigrationCore.dll', 'version.json', 'Resources\bin\Handle\handle64.exe') {
        $null = [DiskLabPath]::AssertOrdinaryPath((Join-Path $app $packageFile), $false)
    }

    $runner = Join-Path $harness 'MigrationSafetyHarness.exe'
    $null = [DiskLabPath]::AssertOrdinaryPath($runner, $false)
    $null = Assert-LabMounted -Lab $lab
    Assert-LabHostSpace -MinimumBytes 1GB
    $evidencePath = Join-Path $results "$attempt-$Suite.json"
    $evidence = & $runner --lab (Join-Path $workRoot 'lab.json') --run-id ($RunId.ToString('D')) --suite $Suite
    $runnerExit = $LASTEXITCODE
    Write-LabBuildFile $evidencePath ($evidence -join [Environment]::NewLine)
    Write-Host "$Suite evidence: $evidencePath"
    if ($runnerExit -ne 0) { throw "$Suite did not pass (exit $runnerExit). Evidence: $evidencePath" }
    if (-not $evidence) { throw "$Suite produced no evidence: $evidencePath" }
    $evidence
    if ($LaunchUi) {
        $runtimeConfig = @([IO.Directory]::EnumerateFiles($app, '*.runtimeconfig.json'))
        if ($runtimeConfig.Count -ne 1) { throw 'Cannot identify the test app executable.' }
        $exe = $runtimeConfig[0] -replace '\.runtimeconfig\.json$', '.exe'
        $null = [DiskLabPath]::AssertOrdinaryPath($exe, $false)
        & $exe --test-lab (Join-Path $workRoot 'lab.json') --run-id ($RunId.ToString('D'))
        if ($LASTEXITCODE -ne 0) { throw "Test UI failed with exit $LASTEXITCODE." }
    }
} finally {
    foreach ($key in $oldEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $oldEnvironment[$key], 'Process')
    }
    $lock.Dispose()
}
