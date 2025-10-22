param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$Target,
    [int]$LargeFileThresholdMB = 1024,
    [int]$RobocopyThreads = 8,
    [int]$SampleMilliseconds = 1000
)

#region 自动申请管理员权限
function Test-Administrator {
    $currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# 如果不是管理员，重新以管理员身份启动脚本
if (-not (Test-Administrator)) {
    Write-Host '检测到当前不是管理员权限，正在请求提升权限...' -ForegroundColor Yellow
    
    # 构建参数列表
    $argList = @()
    $argList += '-NoExit'
    $argList += '-File'
    $argList += "`"$($MyInvocation.MyCommand.Path)`""
    
    # 传递所有原始参数
    if ($PSBoundParameters.ContainsKey('Source')) {
        $argList += '-Source'
        $argList += "`"$Source`""
    }
    if ($PSBoundParameters.ContainsKey('Target')) {
        $argList += '-Target'
        $argList += "`"$Target`""
    }
    if ($PSBoundParameters.ContainsKey('LargeFileThresholdMB')) {
        $argList += '-LargeFileThresholdMB'
        $argList += $LargeFileThresholdMB
    }
    if ($PSBoundParameters.ContainsKey('RobocopyThreads')) {
        $argList += '-RobocopyThreads'
        $argList += $RobocopyThreads
    }
    if ($PSBoundParameters.ContainsKey('SampleMilliseconds')) {
        $argList += '-SampleMilliseconds'
        $argList += $SampleMilliseconds
    }
    
    try {
        $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs -PassThru
        exit
    }
    catch {
        Write-Error "无法以管理员身份启动脚本: $_"
        Write-Host '按任意键退出...'
        $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
        exit 1
    }
}

Write-Host '✓ 已获得管理员权限' -ForegroundColor Green
#endregion

function Get-CanonicalPath([string]$Path) {
    try {
        return (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
    } catch {
        return [System.IO.Path]::GetFullPath($Path)
    }
}

function Get-DirectorySizeBytes([string]$DirectoryPath) {
    if (-not (Test-Path -LiteralPath $DirectoryPath)) { return 0 }
    $total = 0L
    try {
        $enumerator = [System.IO.Directory]::EnumerateFiles($DirectoryPath, '*', [System.IO.SearchOption]::AllDirectories)
        foreach ($file in $enumerator) {
            try { $total += ([System.IO.FileInfo]$file).Length } catch { }
        }
    } catch { }
    return $total
}

function Get-FileStats([string]$DirectoryPath, [long]$LargeThresholdBytes) {
    $totalBytes = 0L
    $totalFiles = 0
    $largeFiles = 0
    if (-not (Test-Path -LiteralPath $DirectoryPath)) {
        return [pscustomobject]@{ TotalBytes = 0L; TotalFiles = 0; LargeFiles = 0 }
    }
    $enumerator = $null
    try {
        $enumerator = [System.IO.Directory]::EnumerateFiles($DirectoryPath, '*', [System.IO.SearchOption]::AllDirectories)
        foreach ($file in $enumerator) {
            try {
                $fi = [System.IO.FileInfo]$file
                $totalBytes += $fi.Length
                $totalFiles += 1
                if ($fi.Length -ge $LargeThresholdBytes) { $largeFiles += 1 }
            } catch { }
        }
    } catch { }
    return [pscustomobject]@{ TotalBytes = $totalBytes; TotalFiles = $totalFiles; LargeFiles = $largeFiles }
}

function New-DirectoryIfMissing([string]$DirectoryPath) {
    if (-not (Test-Path -LiteralPath $DirectoryPath)) {
        [void](New-Item -ItemType Directory -Path $DirectoryPath -Force)
    }
}

function Start-RobocopyWithProgress([string]$SourceDir, [string]$TargetDir, [long]$ExpectedBytes, [int]$Threads, [int]$SampleMs) {
    $robocopyArgs = @(
        '"' + $SourceDir + '"',
        '"' + $TargetDir + '"',
        '/MIR',
        '/COPYALL',
        '/DCOPY:DAT',
        '/R:0',
        '/W:0',
        '/XJ',
        '/NFL',
        '/NDL',
        '/NP',
        '/MT:' + $Threads
    )

    $robocopy = Start-Process -FilePath 'robocopy.exe' -ArgumentList ($robocopyArgs -join ' ') -PassThru

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $prevBytes = 0L
    $prevTime = [TimeSpan]::Zero
    do {
        Start-Sleep -Milliseconds $SampleMs
        $copied = Get-DirectorySizeBytes -DirectoryPath $TargetDir

        $elapsed = $stopwatch.Elapsed
        $deltaBytes = [math]::Max(0, $copied - $prevBytes)
        $deltaTime = ($elapsed - $prevTime).TotalSeconds
        $speed = if ($deltaTime -gt 0) { [double]$deltaBytes / $deltaTime } else { 0 }

        # 复制阶段占10%-90%，即80%的总进度
        $copyPercent = if ($ExpectedBytes -gt 0) { [math]::Min(100, [math]::Floor(($copied * 100.0) / $ExpectedBytes)) } else { 0 }
        $percent = 10 + ($copyPercent * 0.8)  # 映射到10-90%

        $eta = ''
        if ($speed -gt 0 -and $ExpectedBytes -gt 0) {
            $remainingBytes = [math]::Max(0, $ExpectedBytes - $copied)
            $etaSeconds = [int]([math]::Ceiling($remainingBytes / $speed))
            $eta = (New-TimeSpan -Seconds $etaSeconds).ToString()
        }

        # 去掉千位分隔符、单位、多余字符，只保留数字（允许小数）
        $numericSpeed = [double]($speed -replace '[^\d\.]','')
        $status = ('{0:N2}% | {1} / {2} | {3}/s | ETA {4}' -f $percent,
                (Format-Bytes $copied),
                (Format-Bytes $ExpectedBytes),
                (Format-Bytes $numericSpeed),
                $eta)        
        $intPercent = [int]$percent
        Write-Progress -Activity '[3/6] Copying files (robocopy)' -Status $status -PercentComplete $intPercent

        $prevBytes = $copied
        $prevTime = $elapsed
    } while (-not $robocopy.HasExited)

    # 复制完成显示90%
    $finalBytes = Get-DirectorySizeBytes -DirectoryPath $TargetDir
    $finalStatus = ('90% | {0} / {1}' -f (Format-Bytes $finalBytes), (Format-Bytes $ExpectedBytes))
    Write-Progress -Activity '[3/6] Copying files (robocopy)' -Status $finalStatus -PercentComplete 90

    return $robocopy.ExitCode
}

function Format-Bytes([long]$Bytes) {
    $sizes = 'B','KB','MB','GB','TB','PB'
    $len = [double]$Bytes
    $order = 0
    while ($len -ge 1024 -and $order -lt $sizes.Length - 1) {
        $order += 1
        $len = $len / 1024
    }
    return ('{0:N2} {1}' -f $len, $sizes[$order])
}

try {
    Write-Host '[1/6] 解析源/目标路径...' -ForegroundColor Yellow
    Write-Progress -Activity '[1/6] 解析源/目标路径' -Status '验证路径...' -PercentComplete 0
    $sourcePath = Get-CanonicalPath -Path $Source
    if (-not (Test-Path -LiteralPath $sourcePath)) { throw "源目录不存在: $sourcePath" }
    if (-not (Get-Item -LiteralPath $sourcePath).PsIsContainer) { throw "源路径不是目录: $sourcePath" }

    $targetPath = Get-CanonicalPath -Path $Target

    # 若用户提供的目标路径是一个已存在的非空文件夹，且不以源目录名结尾，则自动拼接源目录名
    $sourceLeafForTarget = [System.IO.Path]::GetFileName($sourcePath)
    if (Test-Path -LiteralPath $targetPath) {
        $tItem = Get-Item -LiteralPath $targetPath -ErrorAction Stop
        if ($tItem.PsIsContainer) {
            $targetLeafName = [System.IO.Path]::GetFileName($targetPath.TrimEnd('\'))
            if ([string]::IsNullOrEmpty($targetLeafName)) { $targetLeafName = $tItem.Name }
            $nonEmpty = $false
            try { $nonEmpty = @(Get-ChildItem -LiteralPath $targetPath -Force -ErrorAction SilentlyContinue | Select-Object -First 1).Count -gt 0 } catch { }
            if ($nonEmpty -and -not ($targetLeafName -ieq $sourceLeafForTarget)) {
                $oldPath = $targetPath
                $targetPath = Join-Path -Path $targetPath -ChildPath $sourceLeafForTarget
                Write-Host "⚠️  目标目录非空且不以源目录名结尾" -ForegroundColor Yellow
                Write-Host "   自动调整目标路径: $oldPath -> $targetPath" -ForegroundColor Yellow
            }
        } else {
            throw "目标路径已存在且不是目录: $targetPath"
        }
    }

    $targetParent = [System.IO.Path]::GetDirectoryName($targetPath)
    if ([string]::IsNullOrEmpty($targetParent)) { throw '无法解析目标目录的上级路径。' }
    New-DirectoryIfMissing -DirectoryPath $targetParent
    New-DirectoryIfMissing -DirectoryPath $targetPath

    # 检查最终目标目录是否为空（在路径调整之后）
    if (Test-Path -LiteralPath $targetPath) {
        $targetHasContent = $false
        try { $targetHasContent = @(Get-ChildItem -LiteralPath $targetPath -Force -ErrorAction SilentlyContinue | Select-Object -First 1).Count -gt 0 } catch { }
        if ($targetHasContent) {
            throw "目标目录已存在且不为空，禁止迁移: $targetPath"
        }
    }

    # 防止互为子目录或相同路径
    $srcRooted = [System.IO.Path]::GetFullPath($sourcePath)
    $dstRooted = [System.IO.Path]::GetFullPath($targetPath)
    if ($srcRooted.TrimEnd('\') -ieq $dstRooted.TrimEnd('\')) { throw '源与目标路径不能相同。' }
    if ($dstRooted.StartsWith($srcRooted, [System.StringComparison]::OrdinalIgnoreCase)) { throw '目标不能位于源目录内部。' }

    $thresholdBytes = [int64]$LargeFileThresholdMB * 1MB
    Write-Host '[2/6] 扫描源目录以计算大小与大文件数量...' -ForegroundColor Yellow
    Write-Progress -Activity '[2/6] 扫描源目录' -Status '计算大小与文件数量...' -PercentComplete 5
    $stats = Get-FileStats -DirectoryPath $sourcePath -LargeThresholdBytes $thresholdBytes
    $totalBytes = [int64]$stats.TotalBytes
    $totalFiles = [int]$stats.TotalFiles
    $largeFiles = [int]$stats.LargeFiles
    Write-Host ("源目录: {0}" -f $sourcePath)
    Write-Host ("目标目录: {0}" -f $targetPath)
    Write-Host ("总文件: {0}, 总大小: {1}, 大文件(≥{2}MB): {3}" -f $totalFiles, (Format-Bytes $totalBytes), $LargeFileThresholdMB, $largeFiles)

    Write-Host '[3/6] 开始复制（robocopy）...' -ForegroundColor Yellow
    $copyExit = Start-RobocopyWithProgress -SourceDir $sourcePath -TargetDir $targetPath -ExpectedBytes $totalBytes -Threads $RobocopyThreads -SampleMs $SampleMilliseconds
    # Robocopy 退出码：0-7 视为成功/部分成功
    if ($copyExit -ge 8) { throw ("robocopy 失败，退出码: {0}" -f $copyExit) }

    # 复制完成后做一次大小校验（近似）
    $copiedBytes = Get-DirectorySizeBytes -DirectoryPath $targetPath
    if ($totalBytes -gt 0) {
        $ratio = [double]$copiedBytes / [double]$totalBytes
        if ($ratio -lt 0.98) { Write-Warning ("目标大小仅为源的 {0:P1}，请确认复制是否完整。" -f $ratio) }
    }

    # 切换：将源目录重命名为 .bak 并创建符号链接
    $parent = [System.IO.Path]::GetDirectoryName($sourcePath)
    $name = [System.IO.Path]::GetFileName($sourcePath)
    $timestamp = (Get-Date).ToString('yyyyMMdd_HHmmss')
    $backupPath = Join-Path -Path $parent -ChildPath ($name + '.bak_' + $timestamp)

    Write-Host ("[4/6] 创建符号链接..." -f $backupPath) -ForegroundColor Yellow
    Write-Progress -Activity '[4/6] 创建符号链接' -Status '备份源目录...' -PercentComplete 90
    Move-Item -LiteralPath $sourcePath -Destination $backupPath -Force

    $mklinkCmd = 'mklink /D ' + '"' + $sourcePath + '" ' + '"' + $targetPath + '"'
    Write-Progress -Activity '[4/6] 创建符号链接' -Status '创建符号链接...' -PercentComplete 91
    $mklink = Start-Process -FilePath 'cmd.exe' -ArgumentList "/c $mklinkCmd" -PassThru -Wait
    if ($mklink.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $sourcePath)) {
        Write-Warning '创建符号链接失败，开始回滚...'
        try { if (Test-Path -LiteralPath $sourcePath) { Remove-Item -LiteralPath $sourcePath -Force } } catch { }
        Move-Item -LiteralPath $backupPath -Destination $sourcePath -Force
        throw '已回滚至迁移前状态。'
    }

    # 健康检查：链接存在且可访问
    Write-Host '[5/6] 健康检查...' -ForegroundColor Yellow
    Write-Progress -Activity '[5/6] 健康检查' -Status '验证符号链接...' -PercentComplete 93
    $linkItem = Get-Item -LiteralPath $sourcePath -ErrorAction SilentlyContinue
    if (-not $linkItem -or -not $linkItem.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) {
        Write-Warning '创建的对象不是重解析点（符号链接），开始回滚...'
        try { if (Test-Path -LiteralPath $sourcePath) { Remove-Item -LiteralPath $sourcePath -Force } } catch { }
        Move-Item -LiteralPath $backupPath -Destination $sourcePath -Force
        throw '已回滚至迁移前状态。'
    }

    # 清理备份
    Write-Host '[6/6] 清理备份目录...' -ForegroundColor Yellow
    Write-Progress -Activity '[6/6] 清理备份' -Status '删除备份目录...' -PercentComplete 96
    try { Remove-Item -LiteralPath $backupPath -Recurse -Force } catch { }
    Write-Progress -Activity '[6/6] 清理备份' -Status '完成' -PercentComplete 100 -Completed

    Write-Host ''
    Write-Host '迁移完成 ✅' -ForegroundColor Green
    Write-Host ("源路径(现为链接): {0}" -f $sourcePath)
    Write-Host ("目标路径: {0}" -f $targetPath)
    Write-Host ("总文件: {0}, 总大小: {1}, 大文件(≥{2}MB): {3}" -f $totalFiles, (Format-Bytes $totalBytes), $LargeFileThresholdMB, $largeFiles)
}
catch {
    $msg = $_.Exception.Message
    $inv = $_.InvocationInfo
    $where = if ($inv) { " 行: $($inv.ScriptLineNumber), 调用: $($inv.Line.Trim())" } else { '' }
    Write-Error ("发生错误: {0}{1}" -f $msg, $where)
    if ($_.ScriptStackTrace) { Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray }
    exit 1
}

