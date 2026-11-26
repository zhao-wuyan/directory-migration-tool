using System.Diagnostics;
using System.IO.Enumeration;
using MigrationCore.Models;
using MigrationCore.Models.Cleanup;
using MigrationCore.Models.Scanning;
using MigrationCore.Services.Safety;
using MigrationCore.Services.Scanning;

namespace MigrationCore.Services.Cleanup;

/// <summary>
/// 通道 A 文件级只读扫描器（支持 MFT 内存快照零 I/O 复用、零句柄高速枚举、单次全系统进程快照与零分配 FileSystemName 匹配）。
/// </summary>
internal sealed class CleanupRuleScanner
{
    private readonly PathIdentityService _identity;
    private readonly VolumeSnapshotCache? _snapshotCache;
    private readonly SafetyPolicyEnvironment? _policyEnvironment;
    private readonly CleanupRootPathResolver _rootResolver;
    private readonly Func<string, bool>? _processRunningOverride;

    public CleanupRuleScanner(
        PathIdentityService identity,
        VolumeSnapshotCache? snapshotCache,
        SafetyPolicyEnvironment? policyEnvironment,
        CleanupRootPathResolver rootResolver,
        Func<string, bool>? processRunningOverride = null)
    {
        _identity = identity;
        _snapshotCache = snapshotCache;
        _policyEnvironment = policyEnvironment;
        _rootResolver = rootResolver;
        _processRunningOverride = processRunningOverride;
    }

    /// <summary>
    /// 在单轮扫描开始时拍一次运行中进程名快照（避免 80 条规则循环内 50+ 次内核态全进程枚举）。
    /// </summary>
    public HashSet<string>? CaptureRunningProcessSnapshot()
    {
        if (_processRunningOverride is not null)
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            Process[] all = Process.GetProcesses();
            foreach (Process p in all)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(p.ProcessName))
                    {
                        names.Add(p.ProcessName);
                    }
                }
                catch
                {
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        catch
        {
            return null;
        }

        return names;
    }

    public string[] DetectRunningBlockingProcesses(string[] processNames, IReadOnlySet<string>? preCapturedProcessNames = null)
    {
        if (processNames.Length == 0) return [];

        var running = new List<string>();
        foreach (string procName in processNames)
        {
            if (_processRunningOverride is not null)
            {
                if (_processRunningOverride(procName)) running.Add(procName);
                continue;
            }

            if (preCapturedProcessNames is not null)
            {
                if (preCapturedProcessNames.Contains(procName)) running.Add(procName);
                continue;
            }

            try
            {
                Process[] procs = Process.GetProcessesByName(procName);
                bool any = procs.Length > 0;
                foreach (Process p in procs) p.Dispose();
                if (any) running.Add(procName);
            }
            catch
            {
            }
        }
        return running.ToArray();
    }

    public CleanupRuleScanResult ScanChannelAFiles(
        CleanupRuleDefinition rule,
        IReadOnlyList<string> protectedTxPaths,
        IReadOnlyList<(string SourcePath, string TargetPath)> completedMigrations,
        string[] blockingProcesses,
        DateTimeOffset scannedAt,
        int stepIndex,
        int totalRules,
        CleanupRootResolutionCache? rootCache,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var matchedFiles = new List<CleanupPreviewItem>();
        var expectations = new List<CleanupDirectoryExpectation>();
        long totalBytes = 0;
        int matchedCount = 0;
        int skippedRecentOrLocked = 0;
        int excludedProtected = 0;
        int visitedEntries = 0;
        int mftCacheHitSpecs = 0;
        long lastProgressTick = Environment.TickCount64;
        bool isBrowserRule = rule.IsBrowserCacheRule || rule.RuleId.Equals("browser-cache-only", StringComparison.OrdinalIgnoreCase);
        var seenTargetDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CleanupPathRuleSpec spec in rule.TargetSpecs)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<string> targetDirs = _rootResolver.ResolveRuleTargetDirectories(spec, protectedTxPaths, completedMigrations, rootCache);
            if (targetDirs.Count == 0)
            {
                continue;
            }

            int effectiveMinAgeHours = spec.MinAgeHoursOverride ?? rule.MinAgeHours;
            DateTimeOffset minCutoffUtc = effectiveMinAgeHours > 0
                ? scannedAt.AddHours(-effectiveMinAgeHours)
                : DateTimeOffset.MaxValue;

            foreach (string targetDir in targetDirs)
            {
                ct.ThrowIfCancellationRequested();
                if (!seenTargetDirs.Add(targetDir))
                {
                    continue;
                }

                if (TryLocateInFreshMftSnapshot(targetDir, out ScanSnapshot? mftSnapshot, out int mftRootNodeId))
                {
                    DirectoryIdentity? rootId = TryReadIdentitySafe(targetDir);
                    if (rootId is null || !rootId.IsDirectory || rootId.IsReparsePoint)
                    {
                        excludedProtected++;
                        continue;
                    }

                    mftCacheHitSpecs++;
                    var dirExpectations = new List<CleanupFileExpectation>();
                    EnumerateFromMftSnapshot(
                        mftSnapshot!,
                        mftRootNodeId,
                        spec,
                        rule,
                        effectiveMinAgeHours,
                        minCutoffUtc,
                        isBrowserRule,
                        protectedTxPaths,
                        matchedFiles,
                        dirExpectations,
                        ref totalBytes,
                        ref matchedCount,
                        ref skippedRecentOrLocked,
                        ref excludedProtected,
                        ct);

                    if (dirExpectations.Count > 0)
                    {
                        expectations.Add(new CleanupDirectoryExpectation(
                            RootDirectoryPath: targetDir,
                            ExpectedRootIdentity: rootId,
                            SnapshotCompletedAtUtc: mftSnapshot!.CompletedAtUtc,
                            ExpectedFiles: dirExpectations.ToArray()));
                    }
                    continue;
                }

                if (!Directory.Exists(QuarantineStore.ToExtendedPath(targetDir)))
                {
                    continue;
                }

                DirectoryIdentity? liveRootId = TryReadIdentitySafe(targetDir);
                if (liveRootId is null || !liveRootId.IsDirectory || liveRootId.IsReparsePoint)
                {
                    excludedProtected++;
                    continue;
                }

                var liveDirExpectations = new List<CleanupFileExpectation>();
                EnumerateSafeFiles(
                    targetDir,
                    spec,
                    rule,
                    effectiveMinAgeHours,
                    minCutoffUtc,
                    isBrowserRule,
                    protectedTxPaths,
                    matchedFiles,
                    liveDirExpectations,
                    ref totalBytes,
                    ref matchedCount,
                    ref skippedRecentOrLocked,
                    ref excludedProtected,
                    ref visitedEntries,
                    ref lastProgressTick,
                    stepIndex,
                    totalRules,
                    progress,
                    ct);

                if (liveDirExpectations.Count > 0)
                {
                    expectations.Add(new CleanupDirectoryExpectation(
                        RootDirectoryPath: targetDir,
                        ExpectedRootIdentity: liveRootId,
                        SnapshotCompletedAtUtc: scannedAt,
                        ExpectedFiles: liveDirExpectations.ToArray()));
                }
            }
        }

        string mftTag = mftCacheHitSpecs > 0 ? "（已复用 MFT 缓存 · 执行前仍实时复核 24h 与 FileId）" : string.Empty;
        string statusNote = blockingProcesses.Length > 0
            ? $"检测到 {string.Join("、", blockingProcesses)} 正在运行；请先关闭后再清理。"
            : matchedCount == 0
                ? (skippedRecentOrLocked > 0 ? $"无可清理过期文件（已跳过 {skippedRecentOrLocked:N0} 个近期/活跃文件）{mftTag}。" : $"未发现可清理缓存文件{mftTag}。")
                : $"共识别 {matchedCount:N0} 个可安全隔离文件" + (skippedRecentOrLocked > 0 ? $"（已自动跳过 {skippedRecentOrLocked:N0} 个 24h 内或活跃文件）" : string.Empty) + $"{mftTag}。";

        return new CleanupRuleScanResult(
            Rule: rule,
            PreviewItems: matchedFiles.OrderByDescending(i => i.SizeBytes).Take(CleanupService.MaxPreviewItemsPerRule).ToArray(),
            VerificationExpectations: expectations.ToArray(),
            TotalBytes: totalBytes,
            MatchedCount: matchedCount,
            SkippedRecentOrLockedCount: skippedRecentOrLocked,
            ExcludedProtectedCount: excludedProtected,
            RunningBlockingProcesses: blockingProcesses,
            IsAvailable: matchedCount > 0,
            StatusNote: statusNote,
            ScannedAtUtc: scannedAt);
    }

    internal static bool MatchesAnyPattern(string fileName, string[] patterns)
    {
        if (patterns.Length == 0) return true;
        foreach (string pat in patterns)
        {
            if (pat == "*" || pat == "*.*") return true;
            if (FileSystemName.MatchesSimpleExpression(pat, fileName, ignoreCase: true)) return true;
        }
        return false;
    }

    internal static CleanupRuleScanResult EmptyRuleResult(
        CleanupRuleDefinition rule,
        string[] blockingProcesses,
        DateTimeOffset scannedAt,
        string note) =>
        new(
            Rule: rule,
            PreviewItems: [],
            VerificationExpectations: [],
            TotalBytes: 0,
            MatchedCount: 0,
            SkippedRecentOrLockedCount: 0,
            ExcludedProtectedCount: 0,
            RunningBlockingProcesses: blockingProcesses,
            IsAvailable: false,
            StatusNote: note,
            ScannedAtUtc: scannedAt);

    private readonly record struct FastFsEntry(
        string FullPath,
        string Name,
        FileAttributes Attributes,
        long Length,
        long LastWriteUtcTicks,
        bool IsDirectory);

    private bool TryLocateInFreshMftSnapshot(string targetDir, out ScanSnapshot? snapshot, out int rootNodeId)
    {
        snapshot = null;
        rootNodeId = -1;
        if (_snapshotCache is null || string.IsNullOrWhiteSpace(targetDir) || targetDir.Length < 3 || targetDir[1] != ':')
        {
            return false;
        }

        string normalized = PathIdentityService.Normalize(targetDir);
        string driveRoot = VolumeSnapshotCache.NormalizeDriveRoot(normalized[..3]);
        VolumeSnapshotLookup? lookup = _snapshotCache.TryGet(driveRoot, runLiveProbe: true, touchAccessTime: false);
        if (lookup is null || !lookup.IsFresh)
        {
            return false;
        }

        ScanSnapshot snap = lookup.Entry.Snapshot;
        if (snap.Backend != ScanBackendKind.RawNtfsMft
            || snap.Coverage != ScanCoverageStatus.Complete
            || snap.Nodes.Length == 0)
        {
            return false;
        }

        string scanRoot = string.IsNullOrWhiteSpace(snap.ScanRootPath)
            ? snap.Volume.DriveRoot
            : snap.ScanRootPath;
        string normalizedScanRoot = PathIdentityService.Normalize(scanRoot);
        if (!PathIdentityService.IsWithin(normalized, normalizedScanRoot))
        {
            return false;
        }

        int current = snap.RootNodeId;
        if (!normalized.Equals(normalizedScanRoot, StringComparison.OrdinalIgnoreCase))
        {
            string relative = normalized[normalizedScanRoot.TrimEnd('\\').Length..].TrimStart('\\');
            string[] segments = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            foreach (string seg in segments)
            {
                if (snap.Nodes[current].IsReparsePoint)
                {
                    return false;
                }

                ReadOnlySpan<int> children = snap.GetChildrenSpan(current);
                int matchedChild = -1;
                for (int i = 0; i < children.Length; i++)
                {
                    int cid = children[i];
                    ref readonly ScanNodeRecord child = ref snap.Nodes[cid];
                    if (child.IsDirectory && !child.IsOrphaned && snap.GetNodeName(cid).Equals(seg, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedChild = cid;
                        break;
                    }
                }

                if (matchedChild < 0)
                {
                    return false;
                }

                current = matchedChild;
            }
        }

        ref readonly ScanNodeRecord targetNode = ref snap.Nodes[current];
        if (!targetNode.IsDirectory || targetNode.IsReparsePoint || targetNode.IsOrphaned)
        {
            return false;
        }

        snapshot = snap;
        rootNodeId = current;
        return true;
    }

    private void EnumerateFromMftSnapshot(
        ScanSnapshot snapshot,
        int rootNodeId,
        CleanupPathRuleSpec spec,
        CleanupRuleDefinition rule,
        int effectiveMinAgeHours,
        DateTimeOffset minCutoffUtc,
        bool isBrowserRule,
        IReadOnlyList<string> protectedTxPaths,
        List<CleanupPreviewItem> previewAccumulator,
        List<CleanupFileExpectation> expectations,
        ref long totalBytes,
        ref int matchedCount,
        ref int skippedRecentOrLocked,
        ref int excludedProtected,
        CancellationToken ct)
    {
        long minCutoffTicks = effectiveMinAgeHours > 0 ? minCutoffUtc.UtcTicks : long.MaxValue;
        var stack = new Stack<(int NodeId, int Depth)>();
        stack.Push((rootNodeId, 0));

        while (stack.Count > 0 && matchedCount < CleanupService.MaxFilesPerRuleScan)
        {
            ct.ThrowIfCancellationRequested();
            var (currId, depth) = stack.Pop();
            ReadOnlySpan<int> children = snapshot.GetChildrenSpan(currId);

            for (int i = 0; i < children.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (matchedCount >= CleanupService.MaxFilesPerRuleScan) break;

                int cid = children[i];
                ref readonly ScanNodeRecord child = ref snapshot.Nodes[cid];
                if (child.IsOrphaned || child.IsIncomplete)
                {
                    skippedRecentOrLocked++;
                    continue;
                }

                if (child.IsReparsePoint || child.IsCloudPlaceholder)
                {
                    excludedProtected++;
                    continue;
                }

                string name = snapshot.GetNodeName(cid);
                string fullPath = snapshot.GetFullPath(cid);

                if (child.IsDirectory)
                {
                    if (!spec.Recursive || depth >= 16) continue;
                    if (spec.ExcludePathSegments is { Length: > 0 } && spec.ExcludePathSegments.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        excludedProtected++;
                        continue;
                    }

                    CleanupExclusionVerdict dirExcl = CleanupExclusionPolicy.EvaluateFileForRuleCleanup(
                        fullPath, protectedTxPaths, _policyEnvironment, isBrowserRule);
                    if (dirExcl.IsExcluded)
                    {
                        excludedProtected++;
                        continue;
                    }

                    stack.Push((cid, depth + 1));
                    continue;
                }

                if (!MatchesAnyPattern(name, spec.IncludeFilePatterns)) continue;
                if (spec.ExcludeFileNames is { Length: > 0 } && spec.ExcludeFileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    excludedProtected++;
                    continue;
                }

                CleanupExclusionVerdict excl = CleanupExclusionPolicy.EvaluateFileForRuleCleanup(
                    fullPath, protectedTxPaths, _policyEnvironment, isBrowserRule);
                if (excl.IsExcluded)
                {
                    excludedProtected++;
                    continue;
                }

                if (effectiveMinAgeHours > 0 && (child.LastWriteUtcTicks <= 0 || child.LastWriteUtcTicks > minCutoffTicks))
                {
                    skippedRecentOrLocked++;
                    continue;
                }

                ulong fileId = child.MftRecordNumber;
                long len = child.LogicalBytes;
                long writeTicks = child.LastWriteUtcTicks;

                totalBytes += len;
                matchedCount++;

                expectations.Add(new CleanupFileExpectation(
                    FullPath: fullPath,
                    ExpectedFileId: fileId,
                    ExpectedLogicalBytes: len,
                    ExpectedLastWriteUtcTicks: writeTicks));

                if (previewAccumulator.Count < CleanupService.MaxPreviewItemsPerRule * 2)
                {
                    previewAccumulator.Add(new CleanupPreviewItem(
                        ItemId: $"{rule.RuleId}:{matchedCount}",
                        DisplayName: name,
                        FullPath: fullPath,
                        SizeBytes: len,
                        LastWriteUtcTicks: writeTicks,
                        FileId: fileId,
                        IsDirectory: false,
                        KindTag: "隔离区"));
                }
            }
        }
    }

    private void EnumerateSafeFiles(
        string rootDir,
        CleanupPathRuleSpec spec,
        CleanupRuleDefinition rule,
        int effectiveMinAgeHours,
        DateTimeOffset minCutoffUtc,
        bool isBrowserRule,
        IReadOnlyList<string> protectedTxPaths,
        List<CleanupPreviewItem> previewAccumulator,
        List<CleanupFileExpectation> expectations,
        ref long totalBytes,
        ref int matchedCount,
        ref int skippedRecentOrLocked,
        ref int excludedProtected,
        ref int visitedEntries,
        ref long lastProgressTick,
        int stepIndex,
        int totalRules,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        long minCutoffTicks = effectiveMinAgeHours > 0 ? minCutoffUtc.UtcTicks : long.MaxValue;
        var enumOptions = new EnumerationOptions
        {
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false
        };

        var stack = new Stack<(string Path, int Depth)>();
        stack.Push((rootDir, 0));

        while (stack.Count > 0 && matchedCount < CleanupService.MaxFilesPerRuleScan)
        {
            ct.ThrowIfCancellationRequested();
            var (currentDir, depth) = stack.Pop();

            FileSystemEnumerable<FastFsEntry> entries;
            try
            {
                entries = new FileSystemEnumerable<FastFsEntry>(
                    QuarantineStore.ToExtendedPath(currentDir),
                    (ref FileSystemEntry e) =>
                    {
                        string rawFull = e.ToFullPath();
                        string cleanPath = rawFull.StartsWith(@"\\?\", StringComparison.Ordinal) ? rawFull[4..] : rawFull;
                        return new FastFsEntry(
                            FullPath: cleanPath,
                            Name: e.FileName.ToString(),
                            Attributes: e.Attributes,
                            Length: e.IsDirectory ? 0L : e.Length,
                            LastWriteUtcTicks: e.LastWriteTimeUtc.UtcTicks,
                            IsDirectory: e.IsDirectory);
                    },
                    enumOptions);
            }
            catch
            {
                skippedRecentOrLocked++;
                continue;
            }

            try
            {
                foreach (FastFsEntry entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (matchedCount >= CleanupService.MaxFilesPerRuleScan) break;

                    visitedEntries++;
                    if (progress is not null && (visitedEntries & 0xFF) == 0)
                    {
                        long nowTick = Environment.TickCount64;
                        if (nowTick - lastProgressTick >= 150)
                        {
                            lastProgressTick = nowTick;
                            progress.Report($"正在只读扫描 ({stepIndex}/{totalRules})：{rule.DisplayName} · 已遍历 {visitedEntries:N0} 项（已发现 {FileStatsService.FormatBytes(totalBytes)}）…");
                        }
                    }

                    if ((entry.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                    {
                        excludedProtected++;
                        continue;
                    }

                    if (entry.IsDirectory)
                    {
                        if (!spec.Recursive || depth >= 16) continue;
                        if (spec.ExcludePathSegments is { Length: > 0 } && spec.ExcludePathSegments.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                        {
                            excludedProtected++;
                            continue;
                        }

                        CleanupExclusionVerdict dirExcl = CleanupExclusionPolicy.EvaluateFileForRuleCleanup(
                            entry.FullPath, protectedTxPaths, _policyEnvironment, isBrowserRule);
                        if (dirExcl.IsExcluded)
                        {
                            excludedProtected++;
                            continue;
                        }

                        stack.Push((entry.FullPath, depth + 1));
                        continue;
                    }

                    if (!MatchesAnyPattern(entry.Name, spec.IncludeFilePatterns)) continue;
                    if (spec.ExcludeFileNames is { Length: > 0 } && spec.ExcludeFileNames.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                    {
                        excludedProtected++;
                        continue;
                    }

                    CleanupExclusionVerdict excl = CleanupExclusionPolicy.EvaluateFileForRuleCleanup(
                        entry.FullPath, protectedTxPaths, _policyEnvironment, isBrowserRule);
                    if (excl.IsExcluded)
                    {
                        excludedProtected++;
                        continue;
                    }

                    if (effectiveMinAgeHours > 0 && entry.LastWriteUtcTicks > minCutoffTicks)
                    {
                        skippedRecentOrLocked++;
                        continue;
                    }

                    totalBytes += entry.Length;
                    matchedCount++;

                    expectations.Add(new CleanupFileExpectation(
                        FullPath: entry.FullPath,
                        ExpectedFileId: 0,
                        ExpectedLogicalBytes: entry.Length,
                        ExpectedLastWriteUtcTicks: entry.LastWriteUtcTicks));

                    if (previewAccumulator.Count < CleanupService.MaxPreviewItemsPerRule * 2)
                    {
                        previewAccumulator.Add(new CleanupPreviewItem(
                            ItemId: $"{rule.RuleId}:{matchedCount}",
                            DisplayName: entry.Name,
                            FullPath: entry.FullPath,
                            SizeBytes: entry.Length,
                            LastWriteUtcTicks: entry.LastWriteUtcTicks,
                            FileId: 0,
                            IsDirectory: false,
                            KindTag: "隔离区"));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                skippedRecentOrLocked++;
            }
        }
    }

    private DirectoryIdentity? TryReadIdentitySafe(string path)
    {
        try
        {
            return _identity.ReadIfExists(path);
        }
        catch
        {
            return null;
        }
    }
}
