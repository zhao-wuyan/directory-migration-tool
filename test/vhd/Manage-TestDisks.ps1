[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateSet('Check', 'Create', 'Mount', 'Dismount')][string]$Action = 'Check',
    [guid]$RunId
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'TestDiskGuard.ps1')

function New-LabWorkRoot([string]$WorkRoot) {
    $runs = [IO.Path]::GetDirectoryName($WorkRoot)
    $base = [IO.Path]::GetDirectoryName($runs)
    foreach ($dir in $base, $runs) {
        $null = [DiskLabPath]::AssertOrdinaryPath($dir, $true)
        $null = [IO.Directory]::CreateDirectory($dir)
        $null = [DiskLabPath]::AssertOrdinaryPath($dir, $false)
    }
    if ($null -ne [DiskLabPath]::AssertOrdinaryPath($WorkRoot, $true)) {
        throw "RunId already used; existing directory and files will not be reused: $WorkRoot"
    }
    # Directory.Move refuses an existing destination, including a concurrent Create.
    $staging = [IO.Path]::Combine($runs, '.create-' + [guid]::NewGuid().ToString('D'))
    if ($null -ne [DiskLabPath]::AssertOrdinaryPath($staging, $true)) { throw 'Unexpected staging directory.' }
    $null = [IO.Directory]::CreateDirectory($staging)
    $null = [DiskLabPath]::AssertOrdinaryPath($staging, $false)
    [IO.Directory]::Move($staging, $WorkRoot)
    $null = [DiskLabPath]::AssertOrdinaryPath($WorkRoot, $false)
}

function Write-LabRecord([string]$WorkRoot, $Lab, [switch]$Replace) {
    Assert-LabMounted $Lab
    Assert-LabHostSpace -MinimumBytes 1GB
    $path = [IO.Path]::Combine($WorkRoot, 'lab.json')
    $prior = Get-LabOrdinaryFile -Path $path -AllowMissing:(-not $Replace)
    if (-not $Replace -and $null -ne $prior) { throw 'Refusing to overwrite existing registration.' }
    $temp = [IO.Path]::Combine($WorkRoot, '.lab-' + [guid]::NewGuid().ToString('D') + '.tmp')
    $null = Get-LabOrdinaryFile -Path $temp -AllowMissing
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Lab -Depth 4))
    $stream = [IO.File]::Open($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    } finally { $stream.Dispose() }
    $null = Get-LabOrdinaryFile -Path $temp
    if ($Replace) {
        $current = Get-LabOrdinaryFile -Path $path
        if ($current.FileId -ne $prior.FileId -or $current.Serial -ne $prior.Serial -or $current.FinalPath -cne $prior.FinalPath) {
            throw 'Registration changed before commit.'
        }
        [IO.File]::Replace($temp, $path, [NullString]::Value)
    } else {
        [IO.File]::Move($temp, $path)
    }
}

function Mount-LabImage([string]$Path, [string]$Identifier, [string]$FileId, [switch]$RequireDetached) {
    $image = Get-LabVhd $Path $Identifier $FileId
    if ($RequireDetached -and $image.Vhd.Attached) { throw "A new image was unexpectedly already attached: $Path" }
    if (-not $image.Vhd.Attached) {
        $null = Hyper-V\Mount-VHD -Path $Path -NoDriveLetter -Passthru -ErrorAction Stop
    }
}

function Initialize-LabImage([string]$Path, [string]$Label) {
    Assert-LabHostSpace -MinimumBytes 1GB
    if ($null -ne (Get-LabOrdinaryFile -Path $Path -AllowMissing)) { throw "Image already exists: $Path" }
    $created = @(Hyper-V\New-VHD -Path $Path -Dynamic -SizeBytes 524288000 -ErrorAction Stop)
    if ($created.Count -ne 1 -or $created[0].Path -ine $Path -or -not $created[0].DiskIdentifier -or
        [guid]$created[0].DiskIdentifier -eq [guid]::Empty) { throw 'Cannot establish the identity of the new VHD.' }
    $identifier = ([guid]$created[0].DiskIdentifier).ToString('D')
    $image = Get-LabVhd $Path $identifier
    if ($image.Vhd.Attached) { throw "A new image was unexpectedly already attached: $Path" }
    Mount-LabImage $Path $image.Identifier $image.FileId -RequireDetached

    Assert-LabHostSpace -MinimumBytes 1GB
    $disk = Get-LabAttachedDisk $Path $image.Identifier $image.FileId
    if ($disk.PartitionStyle -ne 'RAW' -or $disk.NumberOfPartitions -ne 0) { throw 'New image is not a blank RAW disk.' }
    $devicePath = $disk.Path
    Storage\Initialize-Disk -InputObject $disk -PartitionStyle GPT -ErrorAction Stop

    Assert-LabHostSpace -MinimumBytes 1GB
    $disk = Get-LabAttachedDisk $Path $image.Identifier $image.FileId
    if ($disk.Path -cne $devicePath -or $disk.PartitionStyle -ne 'GPT' -or
        [string]::IsNullOrWhiteSpace($disk.UniqueId)) { throw 'Unexpected disk identity after GPT initialization.' }
    $diskId = $disk.UniqueId
    if ($disk.NumberOfPartitions -gt 1) { throw 'Unexpected partition layout on newly initialized disk.' }
    if ($disk.NumberOfPartitions -eq 1) {
        $existing = @(Storage\Get-Partition -Disk $disk -ErrorAction Stop)
        if ($existing.Count -ne 1 -or [guid]$existing[0].GptType -ne [guid]'e3c9e316-0b5c-4db8-817d-f92df00215ae') {
            throw 'Newly initialized disk contains an unexpected partition.'
        }
    }
    $newPartition = @(Storage\New-Partition -InputObject $disk -UseMaximumSize `
        -GptType '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}' -ErrorAction Stop)
    if ($newPartition.Count -ne 1 -or -not $newPartition[0].Guid -or [guid]$newPartition[0].Guid -eq [guid]::Empty) {
        throw 'Cannot establish the identity of the new data partition.'
    }
    $partitionGuid = ([guid]$newPartition[0].Guid).ToString('D')
    Assert-LabHostSpace -MinimumBytes 1GB
    $disk = Get-LabAttachedDisk $Path $image.Identifier $image.FileId $diskId
    $partition = Get-LabDataPartition $disk
    if ($disk.Path -cne $devicePath -or ([guid]$partition.Guid).ToString('D') -cne $partitionGuid) {
        throw 'The new partition changed before format.'
    }
    $unformatted = @(Storage\Get-Volume -Partition $partition -ErrorAction Stop)
    if ($unformatted.Count -ne 1 -or $unformatted[0] -isnot [Microsoft.Management.Infrastructure.CimInstance] -or
        [string]$unformatted[0].FileSystem -notin @('', 'Unknown', 'RAW')) {
        throw 'The new partition did not resolve to exactly one unformatted volume; refusing format.'
    }
    $null = Storage\Format-Volume -InputObject $unformatted[0] -FileSystem NTFS -NewFileSystemLabel $Label -ErrorAction Stop

    $disk = Get-LabAttachedDisk $Path $image.Identifier $image.FileId $diskId
    $partition = Get-LabDataPartition $disk
    if ($disk.Path -cne $devicePath -or ([guid]$partition.Guid).ToString('D') -cne $partitionGuid) {
        throw 'The new partition changed after format.'
    }
    $state = Get-LabVolumeState $partition
    return [pscustomobject]@{
        Image = $image; DiskId = $diskId; PartitionGuid = $partitionGuid; State = $state
    }
}

function New-LabFixtureDirectory($Lab, [int]$Index, [string]$RelativePath) {
    Assert-LabHostSpace -MinimumBytes 1GB
    $state = Get-LabRegisteredVolume $Lab $Index
    if (-not $state.Letter -or $state.Letter -cne $Lab.driveLetters[$Index]) { throw 'Lab drive letter changed before write.' }
    $root = $state.Letter + ':\'
    $path = [IO.Path]::Combine($root, $RelativePath)
    if ($null -ne [DiskLabPath]::Check($path, $root, $state.GuidPath, $state.Serial, $true, $false)) {
        throw "Fixture path already exists: $path"
    }
    $null = [IO.Directory]::CreateDirectory($path)
    $null = [DiskLabPath]::Check($path, $root, $state.GuidPath, $state.Serial, $false, $false)
}

function Set-LabDriveLetters($Lab) {
    # Authenticate both volumes before changing either, and the specific partition again before assigning.
    $null = Get-LabRegisteredVolume $Lab 0
    $null = Get-LabRegisteredVolume $Lab 1
    for ($i = 0; $i -lt 2; $i++) {
        Assert-LabHostSpace -MinimumBytes 1GB
        $state = Get-LabRegisteredVolume $Lab $i
        if (-not $state.Letter) {
            $null = Storage\Add-PartitionAccessPath -InputObject $state.Partition -AssignDriveLetter -ErrorAction Stop
            $state = Get-LabRegisteredVolume $Lab $i
        }
        if (-not $state.Letter) { throw 'Windows did not assign a drive letter to the lab volume.' }
        $Lab.driveLetters[$i] = $state.Letter
    }
    if ($Lab.driveLetters[0] -eq $Lab.driveLetters[1]) { throw 'Lab volumes did not receive distinct drive letters.' }
}

function Dismount-LabImages($Lab) {
    $attached = @($false, $false)
    $volumeLocks = @($null, $null)
    try {
        # Preflight all attached volumes before detaching any, using GUID names, never drive letters.
        for ($i = 0; $i -lt 2; $i++) {
            $image = Get-LabVhd $Lab.imagePaths[$i] $Lab.vhdIdentifiers[$i] $Lab.imageFileIds[$i]
            $attached[$i] = $image.Vhd.Attached
            if ($attached[$i]) {
                $state = Get-LabRegisteredVolume $Lab $i
                $volumeLocks[$i] = [DiskLabPath]::LockVolume($state.GuidPath)
            }
        }
        for ($i = 0; $i -lt 2; $i++) {
            if (-not $attached[$i]) { continue }
            # Re-resolve while locked without trying to open the volume filesystem.
            $null = Get-LabRegisteredPartition -Lab $Lab -Index $i
            # FSCTL_LOCK_VOLUME is a preflight, not a force-dismount. Release only this
            # volume's lock immediately before calling Hyper-V, which may need access itself.
            $volumeLocks[$i].Dispose()
            $volumeLocks[$i] = $null
            $null = Hyper-V\Dismount-VHD -Path $Lab.imagePaths[$i] -ErrorAction Stop
            $image = Get-LabVhd $Lab.imagePaths[$i] $Lab.vhdIdentifiers[$i] $Lab.imageFileIds[$i]
            if ($image.Vhd.Attached) { throw "VHD remains attached: $($Lab.imagePaths[$i])" }
        }
    } finally {
        foreach ($handle in $volumeLocks) { if ($null -ne $handle) { $handle.Dispose() } }
    }
}

Assert-LabPrerequisites
if ($Action -eq 'Check') {
    Assert-LabHostSpace -MinimumBytes 2GB
    if ($PSBoundParameters.ContainsKey('RunId')) {
        $null = Read-LabRecord -RunId $RunId -ReadOnlyPreflight
    }
    Write-Output 'Read-only prerequisites passed; VHD creation and attachment were not attempted.'
    return
}
if (-not $PSBoundParameters.ContainsKey('RunId') -or $RunId -eq [guid]::Empty) {
    throw "$Action requires an explicit, nonempty RunId."
}

Initialize-LabGuard
$workRoot = Get-LabWorkRoot -RunId $RunId
$lock = $null
try {
    if ($Action -eq 'Create') {
        Assert-LabHostSpace -MinimumBytes 2GB
        New-LabWorkRoot $workRoot
        $lock = Enter-LabLock -WorkRoot $workRoot -Create
        $paths = @((Get-LabImagePath $workRoot 0), (Get-LabImagePath $workRoot 1))
        $source = Initialize-LabImage $paths[0] 'Lab Source'
        $target = Initialize-LabImage $paths[1] 'Lab Target'
        $lab = [pscustomobject][ordered]@{
            schemaVersion = 1
            runId = $RunId.ToString('D')
            createdAtUtc = [datetime]::UtcNow.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
            imagePaths = $paths
            vhdIdentifiers = @($source.Image.Identifier, $target.Image.Identifier)
            imageFileIds = @($source.Image.FileId, $target.Image.FileId)
            diskUniqueIds = @($source.DiskId, $target.DiskId)
            partitionGuids = @($source.PartitionGuid, $target.PartitionGuid)
            volumeUniqueIds = @($source.State.Volume.UniqueId, $target.State.Volume.UniqueId)
            volumeGuidPaths = @($source.State.GuidPath, $target.State.GuidPath)
            volumeSerials = @($source.State.Serial, $target.State.Serial)
            driveLetters = @('', '')
        }
        foreach ($name in 'vhdIdentifiers', 'imageFileIds', 'diskUniqueIds', 'partitionGuids', 'volumeUniqueIds', 'volumeGuidPaths') {
            if ($lab.$name[0] -eq $lab.$name[1]) { throw "New lab identities are not distinct: $name" }
        }
        Set-LabDriveLetters $lab
        foreach ($relative in 'Data', 'Data\Sources', 'Data\Sources\Sample', 'Data\Protected', 'LabState') {
            New-LabFixtureDirectory $lab 0 $relative
        }
        foreach ($relative in 'Data', 'Data\Targets') { New-LabFixtureDirectory $lab 1 $relative }
        Write-LabRecord $workRoot $lab
    } else {
        $lock = Enter-LabLock -WorkRoot $workRoot
        $lab = Read-LabRecord -RunId $RunId
        if ($Action -eq 'Mount') {
            Assert-LabHostSpace -MinimumBytes 1GB
            # Preflight both images (and any existing attachments) before attaching either.
            for ($i = 0; $i -lt 2; $i++) {
                $image = Get-LabVhd $lab.imagePaths[$i] $lab.vhdIdentifiers[$i] $lab.imageFileIds[$i]
                if ($image.Vhd.Attached) { $null = Get-LabRegisteredVolume $lab $i }
            }
            for ($i = 0; $i -lt 2; $i++) {
                Mount-LabImage $lab.imagePaths[$i] $lab.vhdIdentifiers[$i] $lab.imageFileIds[$i]
                $null = Get-LabRegisteredVolume $lab $i
            }
            Set-LabDriveLetters $lab
            Write-LabRecord $workRoot $lab -Replace
        } else {
            Dismount-LabImages $lab
        }
    }
    Write-Output "$Action completed for $RunId ($workRoot)."
} catch {
    throw "$Action stopped for $RunId. No images or test data were deleted; attachments may remain. Run directory: $workRoot. $($_.Exception.Message)"
} finally {
    if ($null -ne $lock) { $lock.Dispose() }
}
