# Shared by the disk manager and runner. Sourcing this file does not touch disks.

function Assert-LabPrerequisites {
    if ($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion -lt [version]'5.1' -or
        -not [Environment]::Is64BitProcess) {
        throw 'Blocked: use 64-bit Windows PowerShell 5.1 (native Storage CIM objects are required).'
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'Blocked: an elevated administrator session is required.'
        }
    } finally { $identity.Dispose() }

    $required = @{
        'Hyper-V\New-VHD' = @('Path', 'Dynamic', 'SizeBytes')
        'Hyper-V\Get-VHD' = @('Path', 'DiskNumber')
        'Hyper-V\Mount-VHD' = @('Path', 'NoDriveLetter', 'Passthru')
        'Hyper-V\Dismount-VHD' = @('Path')
        'Storage\Get-Disk' = @('Number')
        'Storage\Get-Partition' = @('Disk')
        'Storage\Get-Volume' = @('Partition')
        'Storage\Initialize-Disk' = @('InputObject', 'PartitionStyle')
        'Storage\New-Partition' = @('InputObject', 'UseMaximumSize', 'GptType')
        'Storage\Format-Volume' = @('InputObject', 'FileSystem', 'NewFileSystemLabel')
        'Storage\Add-PartitionAccessPath' = @('InputObject', 'AssignDriveLetter')
    }
    foreach ($name in $required.Keys) {
        $command = Get-Command -Name $name -CommandType Cmdlet,Function -ErrorAction SilentlyContinue
        if (-not $command) { throw "Blocked: missing $name. Enable prerequisites separately; no changes were made." }
        foreach ($parameter in $required[$name]) {
            if (-not $command.Parameters.ContainsKey($parameter)) {
                throw "Blocked: $name does not support -$parameter."
            }
        }
    }
    $dotnet = Get-Command dotnet.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $dotnet) { throw 'Blocked: .NET SDK 8 or later is required.' }
    $sdks = @(& $dotnet.Source --list-sdks)
    if ($LASTEXITCODE -ne 0 -or -not ($sdks | Where-Object { $_ -match '^(?:[89]|[1-9][0-9]+)\.[0-9]+\.[0-9]+\s+\[' })) {
        throw 'Blocked: .NET SDK 8 or later is required.'
    }
}

function Initialize-LabGuard {
    if (-not ('DiskLabPath' -as [type])) {
        $code = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'TestDiskPath.cs'), [Text.Encoding]::UTF8)
        Add-Type -TypeDefinition $code -ErrorAction Stop
    }
}

function Get-LabLocalAppData {
    $path = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData,
        [Environment+SpecialFolderOption]::DoNotVerify)
    if ($path -notmatch '^[A-Za-z]:\\.+[^\\]$') { throw 'Blocked: LocalAppData must be a local DOS path.' }
    return $path
}

function Get-LabWorkRoot([guid]$RunId) {
    if ($RunId -eq [guid]::Empty) { throw 'A nonempty RunId is required.' }
    return [IO.Path]::Combine((Get-LabLocalAppData), 'MoveWithSymlinkWPF-TestLab', 'runs', $RunId.ToString('D'))
}

function Assert-LabReadOnlyDirectories([string]$Path) {
    # Check does not compile the native helper or open writable handles.
    $cursor = $Path.Substring(0, 3)
    foreach ($part in @('') + $Path.Substring(3).Split('\')) {
        if ($part) { $cursor = [IO.Path]::Combine($cursor, $part) }
        $attrs = [IO.File]::GetAttributes($cursor)
        if (($attrs -band ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint)) -ne
            [IO.FileAttributes]::Directory) { throw "Blocked: not an ordinary host directory: $cursor" }
    }
}

function Assert-LabHostSpace([long]$MinimumBytes) {
    $local = Get-LabLocalAppData
    if ('DiskLabPath' -as [type]) {
        $null = [DiskLabPath]::AssertOrdinaryPath($local, $false)
    } else {
        Assert-LabReadOnlyDirectories $local
    }
    $drive = [IO.DriveInfo]::new($local.Substring(0, 3))
    if ($drive.DriveType -ne [IO.DriveType]::Fixed -or $drive.AvailableFreeSpace -lt $MinimumBytes) {
        throw "Blocked: the lab host must be a fixed drive with at least $MinimumBytes available bytes."
    }
}

function Get-LabOrdinaryFile([string]$Path, [switch]$AllowMissing) {
    $file = [DiskLabPath]::AssertOrdinaryPath($Path, $AllowMissing.IsPresent)
    if ($null -ne $file -and (($file.Attributes -band [IO.FileAttributes]::Directory) -or
        $file.LinkCount -ne 1 -or $file.FileId -eq 0)) {
        throw "Not an ordinary single-link file: $Path"
    }
    return $file
}

function Enter-LabLock([string]$WorkRoot, [switch]$Create) {
    $lockPath = [IO.Path]::Combine($WorkRoot, 'run.lock')
    $identity = Get-LabOrdinaryFile -Path $lockPath -AllowMissing:$Create
    if ($Create -and $null -ne $identity) { throw "Unexpected existing lock: $lockPath" }
    $mode = if ($Create) { [IO.FileMode]::CreateNew } else { [IO.FileMode]::Open }
    try {
        return [IO.File]::Open($lockPath, $mode, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    } catch {
        throw "Cannot take exclusive lab run lock (busy or inaccessible): $lockPath. $_"
    }
}

function Get-LabImagePath([string]$WorkRoot, [int]$Index) {
    return [IO.Path]::Combine($WorkRoot, @('source.vhdx', 'target.vhdx')[$Index])
}

function Read-LabRecord([guid]$RunId, [switch]$ReadOnlyPreflight) {
    $root = Get-LabWorkRoot -RunId $RunId
    $path = [IO.Path]::Combine($root, 'lab.json')
    if ($ReadOnlyPreflight) {
        Assert-LabReadOnlyDirectories $root
        if ([IO.File]::GetAttributes($path) -band ([IO.FileAttributes]::Directory -bor [IO.FileAttributes]::ReparsePoint)) {
            throw "Blocked: not an ordinary registration file: $path"
        }
    } else {
        $null = Get-LabOrdinaryFile -Path $path
    }
    $lab = [IO.File]::ReadAllText($path) | ConvertFrom-Json -ErrorAction Stop
    if ($lab -isnot [pscustomobject] -or ($lab.schemaVersion -isnot [int] -and $lab.schemaVersion -isnot [long]) -or
        $lab.schemaVersion -ne 1 -or $lab.runId -isnot [string] -or $lab.runId -cne $RunId.ToString('D')) {
        throw 'Invalid lab record schema or RunId.'
    }
    if ($lab.createdAtUtc -is [datetime]) {
        if ($lab.createdAtUtc.Kind -ne [DateTimeKind]::Utc) { throw 'Invalid lab registration timestamp.' }
        $lab.createdAtUtc = $lab.createdAtUtc.ToString('o', [Globalization.CultureInfo]::InvariantCulture)
    } elseif ($lab.createdAtUtc -isnot [string] -or $lab.createdAtUtc -cnotmatch 'Z$') {
        throw 'Invalid lab registration timestamp.'
    }
    $null = [datetime]::ParseExact($lab.createdAtUtc, 'o', [Globalization.CultureInfo]::InvariantCulture,
        [Globalization.DateTimeStyles]::RoundtripKind)
    foreach ($name in 'imagePaths', 'diskUniqueIds', 'partitionGuids', 'volumeUniqueIds', 'volumeSerials',
        'driveLetters', 'volumeGuidPaths', 'vhdIdentifiers', 'imageFileIds') {
        $values = $lab.$name
        if ($values -isnot [array] -or $values.Count -ne 2) { throw "Invalid lab identity pair: $name" }
        if ($name -ne 'volumeSerials') {
            foreach ($value in $values) {
                if ($value -isnot [string] -or [string]::IsNullOrWhiteSpace($value)) {
                    throw "Invalid lab identity value: $name"
                }
            }
            if ($values[0] -eq $values[1]) { throw "Duplicate lab identity: $name" }
        }
    }
    for ($i = 0; $i -lt 2; $i++) {
        $serial = $lab.volumeSerials[$i]
        if ($lab.imagePaths[$i] -cne (Get-LabImagePath $root $i) -or
            $lab.driveLetters[$i] -cnotmatch '^[A-Z]$' -or $lab.imageFileIds[$i] -cnotmatch '^[0-9A-F]{16}$' -or
            ($serial -isnot [int] -and $serial -isnot [long]) -or $serial -lt 0 -or $serial -gt [uint32]::MaxValue -or
            [guid]::ParseExact($lab.partitionGuids[$i], 'D') -eq [guid]::Empty -or
            [guid]::ParseExact($lab.vhdIdentifiers[$i], 'D') -eq [guid]::Empty) {
            throw "Invalid lab disk identity at index $i."
        }
        if ($ReadOnlyPreflight) {
            $guidPath = $lab.volumeGuidPaths[$i]
            if ($guidPath -notmatch '^\\\\\?\\Volume\{[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}\}\\\z' -or
                [guid]::ParseExact($guidPath.Substring(11, 36), 'D') -eq [guid]::Empty) {
                throw 'Invalid registered volume GUID path.'
            }
        } else {
            [DiskLabPath]::RequireVolumeGuidRoot($lab.volumeGuidPaths[$i])
        }
    }
    return $lab
}

function Get-LabVhd([string]$Path, [string]$ExpectedIdentifier, [string]$ExpectedFileId) {
    $before = Get-LabOrdinaryFile -Path $Path
    $fileId = $before.FileId.ToString('X16')
    if ($ExpectedFileId -and $fileId -cne $ExpectedFileId) { throw "VHD file identity changed: $Path" }
    $matches = @(Hyper-V\Get-VHD -Path $Path -ErrorAction Stop)
    if ($matches.Count -ne 1) { throw "VHD path did not resolve uniquely: $Path" }
    $vhd = $matches[0]
    if ($vhd.Path -ine $Path -or $vhd.VhdFormat -ne 'VHDX' -or $vhd.VhdType -ne 'Dynamic' -or
        $vhd.Size -ne 524288000 -or $vhd.ParentPath -or $null -eq $vhd.DiskIdentifier -or
        [guid]$vhd.DiskIdentifier -eq [guid]::Empty -or
        ($ExpectedIdentifier -and ([guid]$vhd.DiskIdentifier).ToString('D') -cne $ExpectedIdentifier)) {
        throw "VHD path, format, type, capacity or identifier does not match: $Path"
    }
    $after = Get-LabOrdinaryFile -Path $Path
    if ($after.FileId -ne $before.FileId -or $after.Serial -ne $before.Serial -or $after.FinalPath -cne $before.FinalPath) {
        throw "VHD file changed during resolution: $Path"
    }
    return [pscustomobject]@{ Vhd = $vhd; FileId = $fileId; Identifier = ([guid]$vhd.DiskIdentifier).ToString('D') }
}

function Get-LabAttachedDisk([string]$Path, [string]$Identifier, [string]$FileId, [string]$DiskUniqueId) {
    $image = Get-LabVhd $Path $Identifier $FileId
    if (-not $image.Vhd.Attached -or $null -eq $image.Vhd.DiskNumber) { throw "VHD is not locally attached: $Path" }
    $number = $image.Vhd.DiskNumber
    $back = @(Hyper-V\Get-VHD -DiskNumber $number -ErrorAction Stop)
    if ($back.Count -ne 1 -or -not $back[0].Attached -or $back[0].DiskNumber -ne $number -or
        $back[0].Path -ine $Path -or ([guid]$back[0].DiskIdentifier).ToString('D') -cne $Identifier) {
        throw "Disk is not associated with the exact lab image: $Path"
    }
    $disks = @(Storage\Get-Disk -Number $number -ErrorAction Stop)
    if ($disks.Count -ne 1) { throw "Attached disk did not resolve uniquely: $Path" }
    $disk = $disks[0]
    if ($disk -isnot [Microsoft.Management.Infrastructure.CimInstance] -or $disk.Number -ne $number -or
        $disk.CimInstanceProperties['BusType'].Value -ne 15 -or $disk.Size -ne 524288000 -or
        $disk.IsBoot -ne $false -or $disk.IsSystem -ne $false -or $disk.IsOffline -ne $false -or
        $disk.IsReadOnly -ne $false -or [string]::IsNullOrWhiteSpace($disk.Path) -or
        ($DiskUniqueId -and $disk.UniqueId -cne $DiskUniqueId)) {
        throw "Disk is not the expected writable, nonboot, nonsystem file-backed virtual disk: $Path"
    }
    $confirmed = Get-LabVhd $Path $Identifier $FileId
    if (-not $confirmed.Vhd.Attached -or $confirmed.Vhd.DiskNumber -ne $disk.Number) {
        throw "VHD disk association changed during resolution: $Path"
    }
    return $disk
}

function Get-LabDataPartition($Disk) {
    $data = $null
    $reserved = 0
    if ($Disk.PartitionStyle -ne 'GPT') { throw 'Expected a lab GPT disk.' }
    foreach ($partition in @(Storage\Get-Partition -Disk $Disk -ErrorAction Stop)) {
        if ($partition -isnot [Microsoft.Management.Infrastructure.CimInstance] -or
            $partition.DiskNumber -ne $Disk.Number -or $partition.IsBoot -ne $false -or
            $partition.IsSystem -ne $false -or $partition.IsOffline -ne $false) {
            throw 'Unexpected lab partition state.'
        }
        switch ([guid]$partition.GptType) {
            ([guid]'e3c9e316-0b5c-4db8-817d-f92df00215ae') { $reserved++ }
            ([guid]'ebd0a0a2-b9e5-4433-87c0-68b6b72699c7') {
                if ($partition.IsReadOnly -ne $false) { throw 'Lab data partition must be writable.' }
                if ($null -ne $data) { throw 'Multiple lab data partitions.' }
                $data = $partition
            }
            default { throw 'Unexpected partition type on lab disk.' }
        }
    }
    if ($reserved -gt 1 -or $null -eq $data -or -not $data.Guid -or [guid]$data.Guid -eq [guid]::Empty) {
        throw 'Missing unique lab data partition.'
    }
    return $data
}

function Get-LabVolumeState($Partition) {
    $volumes = @(Storage\Get-Volume -Partition $Partition -ErrorAction Stop)
    if ($volumes.Count -ne 1) { throw 'Lab volume association is not unique.' }
    $volume = $volumes[0]
    if ($volume -isnot [Microsoft.Management.Infrastructure.CimInstance] -or $volume.FileSystem -ne 'NTFS' -or
        [string]::IsNullOrWhiteSpace($volume.UniqueId)) { throw 'Expected a uniquely identified NTFS lab volume.' }
    $guidPath = [string]$volume.Path
    [DiskLabPath]::RequireVolumeGuidRoot($guidPath)
    $letter = [string]$volume.DriveLetter
    if ($letter -eq [string][char]0) { $letter = '' }
    if ($letter -notmatch '^(?:[A-Z])?$' -or ($letter -and [string]$Partition.DriveLetter -ine $letter)) {
        throw 'Unexpected or inconsistent lab drive letter.'
    }
    $letter = $letter.ToUpperInvariant()
    foreach ($accessPath in $Partition.AccessPaths) {
        if ($accessPath -and $accessPath -ine $guidPath -and (-not $letter -or $accessPath -ine "${letter}:\")) {
            throw "Unexpected lab partition access path: $accessPath"
        }
    }
    $serial = ([DiskLabPath]::ReadVolumeRoot($guidPath)).Serial
    if ($letter) { $null = [DiskLabPath]::Check("${letter}:\", "${letter}:\", $guidPath, $serial, $false, $false) }
    return [pscustomobject]@{ Partition = $Partition; Volume = $volume; GuidPath = $guidPath; Serial = $serial; Letter = $letter }
}

function Get-LabRegisteredPartition($Lab, [int]$Index) {
    $disk = Get-LabAttachedDisk $Lab.imagePaths[$Index] $Lab.vhdIdentifiers[$Index] $Lab.imageFileIds[$Index] $Lab.diskUniqueIds[$Index]
    $partition = Get-LabDataPartition $disk
    if (([guid]$partition.Guid).ToString('D') -cne $Lab.partitionGuids[$Index]) { throw 'Lab partition GUID mismatch.' }
    return $partition
}

function Get-LabRegisteredVolume($Lab, [int]$Index) {
    $partition = Get-LabRegisteredPartition $Lab $Index
    $state = Get-LabVolumeState $partition
    if ($state.Volume.UniqueId -cne $Lab.volumeUniqueIds[$Index] -or $state.GuidPath -ine $Lab.volumeGuidPaths[$Index] -or
        $state.Serial -ne $Lab.volumeSerials[$Index]) { throw 'Lab volume identity mismatch.' }
    return $state
}

function Assert-LabMounted($Lab) {
    for ($i = 0; $i -lt 2; $i++) {
        $state = Get-LabRegisteredVolume $Lab $i
        if ($state.Letter -cne $Lab.driveLetters[$i]) { throw 'Registered drive letter changed; run Mount to refresh it.' }
    }
}
