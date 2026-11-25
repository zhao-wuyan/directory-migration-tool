using MigrationCore.Models;
using MigrationCore.Models.Scanning;

namespace MigrationCore.Services.Scanning;

/// <summary>
/// 指定路径定向实时复扫与一致性校验器（无需全盘重扫，仅对目标目录或待清理清单执行毫秒~秒级实时核验）。
/// 同时服务于「批量安全迁移执行前复核」与未来「垃圾清理执行前防误删复核」。
/// </summary>
public sealed class TargetedPathVerifier
{
    public delegate bool UsnQueryFunc(string driveRoot, out long journalId, out long nextUsn);

    private readonly DirectoryWalkScanner _walker;
    private readonly PathIdentityService _identity;
    private readonly UsnQueryFunc _usnQuery;

    public TargetedPathVerifier(
        DirectoryWalkScanner? walker = null,
        PathIdentityService? identity = null,
        UsnQueryFunc? usnQuery = null)
    {
        _walker = walker ?? new DirectoryWalkScanner();
        _identity = identity ?? new PathIdentityService();
        _usnQuery = usnQuery ?? NtfsMftScanner.TryQueryVolumeUsn;
    }

    /// <summary>
    /// 在执行目录迁移前，对单个源目录执行定向实时复扫：
    /// 1. 核验根目录身份（VolumeGuid + FileId）与非重解析点；
    /// 2. 若关联快照具备稳定 USN 且卷 NextUsn 未变化，走 &lt;1ms 零 IO 快路径；
    /// 3. 否则仅对 sourcePath 执行定向 DirectoryWalkScanner，核验：
    ///    - 子树无新增重解析点（防穿链）与无访问受限节点；
    ///    - 对新增文件核验是否存在外部硬链接（LinkCount &gt; 1）；
    ///    - 对比实时大小与快照基线，体积异常暴增（&gt;10% 且 &gt;=100MiB）时立即触发 DriftStop。
    /// </summary>
    public async Task<TargetedMigrationVerificationResult> VerifyDirectoryForMigrationAsync(
        string sourcePath,
        DirectoryIdentity? expectedRootIdentity,
        long expectedLogicalBytes,
        long expectedAllocatedBytes,
        int? expectedFileCount = null,
        bool expectedHasReparseDescendants = false,
        ScanSnapshot? cachedSnapshot = null,
        int? cachedNodeId = null,
        TargetedMigrationVerifyOptions? options = null,
        CancellationToken ct = default)
    {
        options ??= new TargetedMigrationVerifyOptions();

        string normalizedSource;
        try
        {
            normalizedSource = PathIdentityService.Normalize(sourcePath);
        }
        catch (Exception ex)
        {
            return HardStop(TargetedVerificationReasonCode.PathMissingOrInaccessible, $"源路径无效：{ex.Message}");
        }

        // 1. 根目录实时身份 + 非符号链接校验（同时校验全部祖先非重解析点）
        DirectoryIdentity liveRootId;
        try
        {
            liveRootId = _identity.ReadEntry(normalizedSource, followLink: false);
        }
        catch (Exception ex)
        {
            return HardStop(TargetedVerificationReasonCode.PathMissingOrInaccessible, $"源目录已不存在或无法读取：{ex.Message}");
        }

        if (!liveRootId.IsDirectory || liveRootId.IsReparsePoint)
        {
            return HardStop(
                TargetedVerificationReasonCode.RootBecameReparsePoint,
                "源目录已变为符号链接、联接点或非普通目录，已停止迁移以防穿链。",
                liveRootId);
        }

        if (expectedRootIdentity is not null && liveRootId != expectedRootIdentity)
        {
            return HardStop(
                TargetedVerificationReasonCode.IdentityChanged,
                "源目录物理身份（VolumeGuid/FileId）较冻结计划时已变化（可能被删除后重建了同名目录），已停止迁移。",
                liveRootId);
        }

        // 2. NTFS USN 零 IO 快路径：仅当快照本身未标记需强制复核、同卷且 NextUsn 完全静止时命中
        string driveRoot = VolumeSnapshotCache.NormalizeDriveRoot(normalizedSource[..3]);
        if (CanTrustUsnFastPath(cachedSnapshot, driveRoot))
        {
            long snapLogical = expectedLogicalBytes;
            long snapAlloc = expectedAllocatedBytes;
            int snapFiles = expectedFileCount ?? 0;
            int snapDirs = 0;

            if (cachedSnapshot is not null && cachedNodeId.HasValue && (uint)cachedNodeId.Value < (uint)cachedSnapshot.Nodes.Length)
            {
                ref readonly ScanNodeRecord snapNode = ref cachedSnapshot.Nodes[cachedNodeId.Value];
                snapLogical = snapNode.LogicalBytes;
                snapAlloc = snapNode.UniqueAllocatedBytes;
                snapFiles = snapNode.FileCount;
                snapDirs = snapNode.DirectoryCount;
            }

            return new TargetedMigrationVerificationResult(
                Outcome: TargetedVerificationOutcome.Passed,
                ReasonCode: TargetedVerificationReasonCode.UsnUnchangedFastPath,
                Message: null,
                UsedUsnFastPath: true,
                LiveLogicalBytes: snapLogical,
                LiveAllocatedBytes: snapAlloc,
                LiveFileCount: snapFiles,
                LiveDirectoryCount: snapDirs,
                HasReparseDescendants: expectedHasReparseDescendants,
                LiveRootIdentity: liveRootId);
        }

        // 3. 指定路径定向复扫（只扫 normalizedSource，不扫整盘）
        ScanVolumeInfo targetVol = BuildMinimalVolumeInfo(driveRoot, liveRootId);
        var walkOptions = new DirectoryWalkOptions(
            MaxDegreeOfParallelism: null,
            BufferSizeBytes: 64 * 1024,
            UseBackgroundIoPriority: false, // 用户已显式点击执行，优先快速完成复核
            DirectoriesPerYield: 64);

        ScanSnapshot liveSubtree = await _walker.ScanAsync(
            targetVol,
            rootDirectoryOverride: normalizedSource,
            fallbackReason: "执行前定向实时复扫",
            options: walkOptions,
            progress: null,
            ct: ct).ConfigureAwait(false);

        if (liveSubtree.Coverage is ScanCoverageStatus.Cancelled)
        {
            throw new OperationCanceledException(ct);
        }

        if (liveSubtree.Coverage is ScanCoverageStatus.AccessDenied || liveSubtree.ErrorCount > 0 || liveSubtree.IncompleteNodeCount > 0)
        {
            return HardStop(
                TargetedVerificationReasonCode.SubtreeIncompleteOrAccessDenied,
                $"定向复扫目标目录时存在 {Math.Max(1, liveSubtree.ErrorCount)} 处访问受限或读取失败，已停止迁移以防漏复制。",
                liveRootId);
        }

        ref readonly ScanNodeRecord liveRootNode = ref liveSubtree.Nodes[liveSubtree.RootNodeId];
        long liveLogical = liveRootNode.LogicalBytes;
        long liveAllocated = liveRootNode.UniqueAllocatedBytes;
        int liveFiles = liveRootNode.FileCount;
        int liveDirs = liveRootNode.DirectoryCount;

        // 4. 检测子树中是否新出现重解析点（符号链接 / Junction / 挂载点）
        bool hasLiveReparse = false;
        string? firstReparsePath = null;
        for (int i = 1; i < liveSubtree.Nodes.Length; i++)
        {
            if (liveSubtree.Nodes[i].IsReparsePoint)
            {
                hasLiveReparse = true;
                firstReparsePath ??= liveSubtree.GetFullPath(i);
                break;
            }
        }

        if (hasLiveReparse && !expectedHasReparseDescendants)
        {
            return new TargetedMigrationVerificationResult(
                Outcome: TargetedVerificationOutcome.HardStop,
                ReasonCode: TargetedVerificationReasonCode.NewReparsePointDetected,
                Message: $"定向复扫发现目录内新出现符号链接/重解析点（{firstReparsePath}），与原快照不一致，已停止迁移。",
                UsedUsnFastPath: false,
                LiveLogicalBytes: liveLogical,
                LiveAllocatedBytes: liveAllocated,
                LiveFileCount: liveFiles,
                LiveDirectoryCount: liveDirs,
                HasReparseDescendants: true,
                LiveRootIdentity: liveRootId);
        }

        // 5. 若有旧快照可比对，检查“自扫描后新增的文件”是否包含跨外部硬链接（仅查增量文件，避免全量打开句柄）
        if (cachedSnapshot is not null && cachedNodeId.HasValue && (uint)cachedNodeId.Value < (uint)cachedSnapshot.Nodes.Length)
        {
            var knownFileIds = CollectSubtreeFileIds(cachedSnapshot, cachedNodeId.Value);
            var liveFileIdCounts = new Dictionary<ulong, int>();
            var newFileNodeIds = new List<int>();

            for (int i = 1; i < liveSubtree.Nodes.Length; i++)
            {
                ref readonly ScanNodeRecord n = ref liveSubtree.Nodes[i];
                if (n.IsDirectory) continue;

                ulong fid = ComposeFileId(n);
                if (fid != 0)
                {
                    liveFileIdCounts[fid] = liveFileIdCounts.TryGetValue(fid, out int c) ? c + 1 : 1;
                    if (!knownFileIds.Contains(fid))
                    {
                        newFileNodeIds.Add(i);
                    }
                }
            }

            if (newFileNodeIds.Count > 0 && newFileNodeIds.Count <= options.MaxNewFilesForHardLinkProbe)
            {
                foreach (int nodeIdx in newFileNodeIds)
                {
                    ct.ThrowIfCancellationRequested();
                    string filePath = liveSubtree.GetFullPath(nodeIdx);
                    try
                    {
                        var (_, linkCount) = _identity.ReadEntryWithLinkCount(filePath, followLink: false);
                        if (linkCount > 1)
                        {
                            ulong fid = ComposeFileId(liveSubtree.Nodes[nodeIdx]);
                            int internalCount = fid != 0 && liveFileIdCounts.TryGetValue(fid, out int cnt) ? cnt : 1;
                            if (internalCount < linkCount)
                            {
                                return new TargetedMigrationVerificationResult(
                                    Outcome: TargetedVerificationOutcome.HardStop,
                                    ReasonCode: TargetedVerificationReasonCode.NewExternalHardLinkDetected,
                                    Message: $"定向复扫发现新增文件包含跨目录外部硬链接（{filePath}），跨卷迁移会破坏共享语义，已停止。",
                                    UsedUsnFastPath: false,
                                    LiveLogicalBytes: liveLogical,
                                    LiveAllocatedBytes: liveAllocated,
                                    LiveFileCount: liveFiles,
                                    LiveDirectoryCount: liveDirs,
                                    HasReparseDescendants: hasLiveReparse,
                                    LiveRootIdentity: liveRootId);
                            }
                        }
                    }
                    catch
                    {
                        // 单文件瞬时不可读由后续 Robocopy 锁/核对阶段显式拦截，此处不吞掉已确认的硬链接违规
                    }
                }
            }
        }

        // 6. 体积异常增长漂移拦截（防止缓存期间后台持续写入大量新数据，偏离用户确认预期）
        long baselineBytes = Math.Max(expectedLogicalBytes, expectedAllocatedBytes);
        long liveBytes = Math.Max(liveLogical, liveAllocated);
        long growth = liveBytes - baselineBytes;

        if (growth >= options.MinSignificantGrowthBytes)
        {
            double growthRatio = baselineBytes > 0 ? (double)growth / baselineBytes : 1.0;
            if (growthRatio >= options.MaxRelativeSizeGrowth)
            {
                return new TargetedMigrationVerificationResult(
                    Outcome: TargetedVerificationOutcome.DriftStop,
                    ReasonCode: TargetedVerificationReasonCode.SizeGrowthExceededThreshold,
                    Message: $"定向复扫发现目录体积较扫描快照显著增长（快照 {FileStatsService.FormatBytes(baselineBytes)} → 最新 {FileStatsService.FormatBytes(liveBytes)}，增加 {FileStatsService.FormatBytes(growth)}），请重新扫描确认后再迁移。",
                    UsedUsnFastPath: false,
                    LiveLogicalBytes: liveLogical,
                    LiveAllocatedBytes: liveAllocated,
                    LiveFileCount: liveFiles,
                    LiveDirectoryCount: liveDirs,
                    HasReparseDescendants: hasLiveReparse,
                    LiveRootIdentity: liveRootId);
            }
        }

        return new TargetedMigrationVerificationResult(
            Outcome: TargetedVerificationOutcome.Passed,
            ReasonCode: TargetedVerificationReasonCode.None,
            Message: null,
            UsedUsnFastPath: false,
            LiveLogicalBytes: liveLogical,
            LiveAllocatedBytes: liveAllocated,
            LiveFileCount: liveFiles,
            LiveDirectoryCount: liveDirs,
            HasReparseDescendants: hasLiveReparse,
            LiveRootIdentity: liveRootId);
    }

    /// <summary>
    /// 垃圾清理执行前的指定路径严格复核（复用于未来垃圾清理模块）：
    /// - 仅扫描指定的待清理目录，绝不全盘重扫；
    /// - 遵循「宁漏勿错、绝不多删」原则：
    ///   * 出现新符号链接/重解析点、身份漂移、权限受限 → HardStop；
    ///   * 出现快照里不存在的新文件、文件修改时间晚于快照或大小变化 → DriftStop；
    ///   * 原本待删的文件已经消失（被系统或软件自行清理）→ 安全跳过（写入 SkippedMissingPaths）。
    /// </summary>
    public async Task<TargetedCleanupVerificationResult> VerifyDirectoryForCleanupAsync(
        CleanupDirectoryExpectation expectation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectation);

        string normalizedRoot;
        try
        {
            normalizedRoot = PathIdentityService.Normalize(expectation.RootDirectoryPath);
        }
        catch (Exception ex)
        {
            return new TargetedCleanupVerificationResult(
                TargetedVerificationOutcome.HardStop,
                TargetedVerificationReasonCode.PathMissingOrInaccessible,
                $"待清理目录路径无效：{ex.Message}",
                [], [], []);
        }

        DirectoryIdentity? liveRoot = _identity.ReadIfExists(normalizedRoot);
        if (liveRoot is null)
        {
            // 整个待清理目录已不存在 -> 全部视作已消失跳过，无需报错
            return new TargetedCleanupVerificationResult(
                TargetedVerificationOutcome.Passed,
                TargetedVerificationReasonCode.None,
                null,
                VerifiedReadyPaths: [],
                SkippedMissingPaths: expectation.ExpectedFiles.Select(f => f.FullPath).ToArray(),
                ConflictingPaths: []);
        }

        if (!liveRoot.IsDirectory || liveRoot.IsReparsePoint)
        {
            return new TargetedCleanupVerificationResult(
                TargetedVerificationOutcome.HardStop,
                TargetedVerificationReasonCode.RootBecameReparsePoint,
                $"待清理目录已变为符号链接/重解析点（{normalizedRoot}），为防止删穿真实数据已立刻停止。",
                [], [], [normalizedRoot]);
        }

        if (expectation.ExpectedRootIdentity is not null && liveRoot != expectation.ExpectedRootIdentity)
        {
            return new TargetedCleanupVerificationResult(
                TargetedVerificationOutcome.HardStop,
                TargetedVerificationReasonCode.IdentityChanged,
                $"待清理目录物理身份（FileId）较扫描时已变化（{normalizedRoot}），已停止清理。",
                [], [], [normalizedRoot]);
        }

        string driveRoot = VolumeSnapshotCache.NormalizeDriveRoot(normalizedRoot[..3]);
        ScanVolumeInfo vol = BuildMinimalVolumeInfo(driveRoot, liveRoot);
        ScanSnapshot liveSubtree = await _walker.ScanAsync(
            vol,
            rootDirectoryOverride: normalizedRoot,
            fallbackReason: "垃圾清理前定向复核",
            options: new DirectoryWalkOptions(UseBackgroundIoPriority: false),
            progress: null,
            ct: ct).ConfigureAwait(false);

        if (liveSubtree.Coverage == ScanCoverageStatus.Cancelled)
        {
            throw new OperationCanceledException(ct);
        }

        if (liveSubtree.Coverage == ScanCoverageStatus.AccessDenied || liveSubtree.ErrorCount > 0 || liveSubtree.IncompleteNodeCount > 0)
        {
            return new TargetedCleanupVerificationResult(
                TargetedVerificationOutcome.HardStop,
                TargetedVerificationReasonCode.SubtreeIncompleteOrAccessDenied,
                $"定向复扫待清理目录时存在访问受限或读取失败（{normalizedRoot}），已停止清理。",
                [], [], [normalizedRoot]);
        }

        var expectedByPath = new Dictionary<string, CleanupFileExpectation>(StringComparer.OrdinalIgnoreCase);
        foreach (CleanupFileExpectation item in expectation.ExpectedFiles)
        {
            expectedByPath[PathIdentityService.Normalize(item.FullPath)] = item;
        }

        var seenExpected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var readyPaths = new List<string>(expectedByPath.Count);
        var newFiles = new List<string>();
        var modifiedFiles = new List<string>();
        var sizeChangedFiles = new List<string>();
        var idChangedFiles = new List<string>();
        var reparseHits = new List<string>();

        for (int i = 1; i < liveSubtree.Nodes.Length; i++)
        {
            ref readonly ScanNodeRecord node = ref liveSubtree.Nodes[i];
            string fullPath = liveSubtree.GetFullPath(i);

            if (node.IsReparsePoint)
            {
                reparseHits.Add(fullPath);
                continue;
            }

            if (node.IsDirectory) continue;

            if (!expectedByPath.TryGetValue(fullPath, out CleanupFileExpectation? exp))
            {
                newFiles.Add(fullPath);
                continue;
            }

            seenExpected.Add(fullPath);

            ulong liveFileId = ComposeFileId(node);
            if (exp.ExpectedFileId != 0 && liveFileId != 0 && liveFileId != exp.ExpectedFileId)
            {
                idChangedFiles.Add(fullPath);
                continue;
            }

            if (node.LogicalBytes != exp.ExpectedLogicalBytes)
            {
                sizeChangedFiles.Add(fullPath);
                continue;
            }

            long maxAllowedWriteTicks = Math.Max(exp.ExpectedLastWriteUtcTicks, expectation.SnapshotCompletedAtUtc.UtcTicks);
            if (node.LastWriteUtcTicks > maxAllowedWriteTicks)
            {
                modifiedFiles.Add(fullPath);
                continue;
            }

            readyPaths.Add(fullPath);
        }

        if (reparseHits.Count > 0)
        {
            return new TargetedCleanupVerificationResult(
                TargetedVerificationOutcome.HardStop,
                TargetedVerificationReasonCode.NewReparsePointDetected,
                $"待清理目录内检测到符号链接/重解析点（如 {reparseHits[0]}），已停止清理以防删穿。",
                [], [], reparseHits.ToArray());
        }

        if (idChangedFiles.Count > 0)
        {
            return new TargetedCleanupVerificationResult(
                TargetedVerificationOutcome.HardStop,
                TargetedVerificationReasonCode.IdentityChanged,
                $"待清理目录内有 {idChangedFiles.Count} 个文件身份（FileId）发生变化（如 {idChangedFiles[0]}），已停止清理。",
                [], [], idChangedFiles.ToArray());
        }

        if (newFiles.Count > 0)
        {
            return new TargetedCleanupVerificationResult(
                TargetedVerificationOutcome.DriftStop,
                TargetedVerificationReasonCode.UnexpectedNewFileDetected,
                $"待清理目录内新增了 {newFiles.Count} 个扫描快照中不存在的文件（如 {newFiles[0]}），为防误删已停止，请重新扫描确认。",
                [], [], newFiles.ToArray());
        }

        if (sizeChangedFiles.Count > 0)
        {
            return new TargetedCleanupVerificationResult(
                TargetedVerificationOutcome.DriftStop,
                TargetedVerificationReasonCode.FileSizeChangedSinceSnapshot,
                $"待清理目录内有 {sizeChangedFiles.Count} 个文件大小已变化（如 {sizeChangedFiles[0]}），可能仍在被写入，已停止清理。",
                [], [], sizeChangedFiles.ToArray());
        }

        if (modifiedFiles.Count > 0)
        {
            return new TargetedCleanupVerificationResult(
                TargetedVerificationOutcome.DriftStop,
                TargetedVerificationReasonCode.FileModifiedSinceSnapshot,
                $"待清理目录内有 {modifiedFiles.Count} 个文件在扫描后被修改（如 {modifiedFiles[0]}），已停止清理以防误删活跃文件。",
                [], [], modifiedFiles.ToArray());
        }

        string[] skippedMissing = expectedByPath.Keys
            .Where(p => !seenExpected.Contains(p))
            .ToArray();

        return new TargetedCleanupVerificationResult(
            TargetedVerificationOutcome.Passed,
            TargetedVerificationReasonCode.None,
            null,
            VerifiedReadyPaths: readyPaths.ToArray(),
            SkippedMissingPaths: skippedMissing,
            ConflictingPaths: []);
    }

    private bool CanTrustUsnFastPath(ScanSnapshot? snapshot, string driveRoot)
    {
        if (snapshot is null
            || snapshot.RequiresLiveReverification
            || snapshot.Backend != ScanBackendKind.RawNtfsMft
            || !snapshot.UsnStartJournalId.HasValue
            || !snapshot.UsnEndNextUsn.HasValue)
        {
            return false;
        }

        if (!VolumeSnapshotCache.NormalizeDriveRoot(snapshot.Volume.DriveRoot)
                .Equals(driveRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!_usnQuery(driveRoot, out long liveJournalId, out long liveNextUsn))
        {
            return false;
        }

        return liveJournalId == snapshot.UsnStartJournalId.Value
            && liveNextUsn == snapshot.UsnEndNextUsn.Value;
    }

    private static HashSet<ulong> CollectSubtreeFileIds(ScanSnapshot snapshot, int rootNodeId)
    {
        var ids = new HashSet<ulong>();
        var stack = new Stack<int>();
        stack.Push(rootNodeId);
        while (stack.Count > 0)
        {
            int curr = stack.Pop();
            ref readonly ScanNodeRecord node = ref snapshot.Nodes[curr];
            if (!node.IsDirectory)
            {
                ulong fid = ComposeFileId(node);
                if (fid != 0) ids.Add(fid);
            }
            else if (!node.IsReparsePoint)
            {
                ReadOnlySpan<int> children = snapshot.GetChildrenSpan(curr);
                for (int i = 0; i < children.Length; i++)
                {
                    stack.Push(children[i]);
                }
            }
        }
        return ids;
    }

    public static ulong ComposeFileId(in ScanNodeRecord node) =>
        ((ulong)node.SequenceNumber << 48) | (node.MftRecordNumber & 0x0000_FFFF_FFFF_FFFFUL);

    private static ScanVolumeInfo BuildMinimalVolumeInfo(string driveRoot, DirectoryIdentity rootIdentity)
    {
        long total = 0;
        long free = 0;
        string fs = "NTFS";
        string label = string.Empty;
        try
        {
            var di = new DriveInfo(driveRoot);
            if (di.IsReady)
            {
                total = di.TotalSize;
                free = di.AvailableFreeSpace;
                fs = di.DriveFormat;
                label = di.VolumeLabel;
            }
        }
        catch
        {
        }

        return new ScanVolumeInfo(
            DriveRoot: driveRoot,
            VolumeLabel: label,
            VolumeGuid: rootIdentity.VolumeGuid,
            VolumeSerial: rootIdentity.VolumeSerial,
            FileSystem: fs,
            TotalBytes: total,
            FreeBytes: free,
            SupportsRawMft: false,
            CapabilityReason: "TargetedLiveVerify");
    }

    private static TargetedMigrationVerificationResult HardStop(
        TargetedVerificationReasonCode code,
        string message,
        DirectoryIdentity? liveId = null) =>
        new(
            Outcome: TargetedVerificationOutcome.HardStop,
            ReasonCode: code,
            Message: message,
            UsedUsnFastPath: false,
            LiveLogicalBytes: 0,
            LiveAllocatedBytes: 0,
            LiveFileCount: 0,
            LiveDirectoryCount: 0,
            HasReparseDescendants: false,
            LiveRootIdentity: liveId);
}
