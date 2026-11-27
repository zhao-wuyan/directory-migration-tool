using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using MigrationCore.Models;
using MigrationCore.Models.Cleanup;
using MigrationCore.Models.Scanning;
using MigrationCore.Services.Safety;
using MigrationCore.Services.Scanning;
using static MigrationCore.Services.ShellRecycleInterop;

namespace MigrationCore.Services.Cleanup;

/// <summary>
/// 通道 A（文件级同卷隔离 / 回收站不降级守卫）及 AI 疑似残留受限隔离执行器。
/// </summary>
internal sealed class CleanupChannelAExecutor
{
    private readonly QuarantineStore _quarantineStore;
    private readonly PathIdentityService _identity;
    private readonly SafetyPolicyEnvironment? _policyEnvironment;

    public CleanupChannelAExecutor(
        QuarantineStore quarantineStore,
        PathIdentityService identity,
        SafetyPolicyEnvironment? policyEnvironment = null)
    {
        _quarantineStore = quarantineStore;
        _identity = identity;
        _policyEnvironment = policyEnvironment;
    }

    public async Task<CleanupExecutionItemResult> ExecuteChannelARuleAsync(
        CleanupRuleScanResult ruleScan,
        CleanupFileAction requestedAction,
        IReadOnlyList<string> protectedTxPaths,
        IProgress<string>? progress,
        IProgress<CleanupExecutionProgress>? detailedProgress,
        int ruleIndex,
        int totalRules,
        CancellationToken ct)
    {
        CleanupRuleDefinition rule = ruleScan.Rule;
        CleanupFileAction effectiveAction = ResolveEffectiveAction(rule, requestedAction);

        bool isBrowserRule = rule.IsBrowserCacheRule || rule.RuleId.Equals("browser-cache-only", StringComparison.OrdinalIgnoreCase);
        DateTimeOffset minCutoffUtc = rule.MinAgeHours > 0
            ? DateTimeOffset.UtcNow.AddHours(-rule.MinAgeHours)
            : DateTimeOffset.MaxValue;

        var verifiedReadyFiles = new List<(string FullPath, long SizeBytes, bool IsDirectory, ulong FileId)>();
        var quarantineMoveUnits = new List<QuarantineMoveUnit>();
        int skippedCount = 0;
        var details = new List<string>();
        bool enableSubtreeFolding = effectiveAction == CleanupFileAction.QuarantineSameVolume;

        int totalExpectedFiles = ruleScan.VerificationExpectations.Sum(e => e.ExpectedFiles.Count);
        int checkedFiles = 0;
        long verifiedBytes = 0;
        long lastVerifyTick = Environment.TickCount64;
        double ruleSliceStart = ruleIndex * 100.0 / Math.Max(1, totalRules);
        double ruleSliceSpan = 100.0 / Math.Max(1, totalRules);

        void ReportPhase(string phaseText, int done, int total, long bytes, double localFraction, string msg)
        {
            double pct = Math.Clamp(ruleSliceStart + ruleSliceSpan * Math.Clamp(localFraction, 0d, 1d), 0d, 100d);
            progress?.Report(msg);
            detailedProgress?.Report(new CleanupExecutionProgress(
                CompletedRules: ruleIndex,
                TotalRules: totalRules,
                CurrentRuleName: rule.DisplayName,
                PhaseText: phaseText,
                ProcessedFiles: done,
                TotalFiles: total,
                ProcessedBytes: bytes,
                OverallPercent: pct,
                IsIndeterminate: false,
                Message: msg));
        }

        if (totalExpectedFiles > 0)
        {
            ReportPhase(
                "执行前快照复核",
                0,
                totalExpectedFiles,
                0,
                0.02,
                $"正在执行前安全复核：{rule.DisplayName}（0 / {totalExpectedFiles:N0} 文件）…");
        }

        long minCutoffUtcTicks = rule.MinAgeHours > 0 ? minCutoffUtc.UtcTicks : long.MaxValue;
        byte[] enumBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        var sliceBatch = new List<ParsedDirEntrySlice>(256);
        try
        {
            foreach (CleanupDirectoryExpectation dirExp in ruleScan.VerificationExpectations)
            {
                ct.ThrowIfCancellationRequested();

                DirectoryIdentity? liveRoot = _identity.ReadIfExists(dirExp.RootDirectoryPath);
                if (liveRoot is null)
                {
                    skippedCount += dirExp.ExpectedFiles.Count;
                    checkedFiles += dirExp.ExpectedFiles.Count;
                    continue;
                }
                if (!liveRoot.IsDirectory || liveRoot.IsReparsePoint
                    || (dirExp.ExpectedRootIdentity is not null && liveRoot != dirExp.ExpectedRootIdentity))
                {
                    return new CleanupExecutionItemResult(
                        rule.RuleId,
                        rule.DisplayName,
                        rule.Channel,
                        CleanupItemExecutionStatus.Blocked,
                        0, 0, dirExp.ExpectedFiles.Count, 0, null,
                        $"执行前复核发现目标目录身份变化或变为重解析点（{dirExp.RootDirectoryPath}），已安全中止。",
                        [$"HardStop：{dirExp.RootDirectoryPath}"]);
                }

                string normalizedRoot = PathIdentityService.Normalize(dirExp.RootDirectoryPath);
                var verifiedSubdirs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                {
                    [normalizedRoot] = true
                };

                // 按父目录分组，避免对数万文件重复校验公共祖先链并支持每目录单次批量 FileIdBothDirectoryInfo 核验
                var parentGroups = new List<(string ParentDir, List<(CleanupFileExpectation Exp, string FileName)> Items)>();
                var parentGroupIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (CleanupFileExpectation exp in dirExp.ExpectedFiles)
                {
                    ct.ThrowIfCancellationRequested();

                    CleanupExclusionVerdict excl = CleanupExclusionPolicy.EvaluateFileForRuleCleanup(
                        exp.FullPath, protectedTxPaths, _policyEnvironment, isBrowserRule);
                    if (excl.IsExcluded)
                    {
                        checkedFiles++;
                        skippedCount++;
                        details.Add($"永久排除跳过：{exp.FullPath}");
                        continue;
                    }

                    string normalizedFile;
                    try
                    {
                        normalizedFile = PathIdentityService.Normalize(exp.FullPath);
                    }
                    catch
                    {
                        checkedFiles++;
                        skippedCount++;
                        continue;
                    }

                    string? parentDir = Path.GetDirectoryName(normalizedFile);
                    string fileName = Path.GetFileName(normalizedFile);
                    if (string.IsNullOrEmpty(parentDir) || string.IsNullOrEmpty(fileName))
                    {
                        checkedFiles++;
                        skippedCount++;
                        continue;
                    }

                    if (!parentGroupIndex.TryGetValue(parentDir, out int groupIdx))
                    {
                        groupIdx = parentGroups.Count;
                        parentGroupIndex[parentDir] = groupIdx;
                        parentGroups.Add((parentDir, new List<(CleanupFileExpectation, string)>()));
                    }
                    parentGroups[groupIdx].Items.Add((exp, fileName));
                }

                long snapshotTicks = dirExp.SnapshotCompletedAtUtc.UtcTicks;
                var dirAudits = enableSubtreeFolding
                    ? new Dictionary<string, DirFoldAudit>(StringComparer.OrdinalIgnoreCase)
                    : null;
                var rootVerifiedFiles = enableSubtreeFolding
                    ? new List<(string FullPath, long SizeBytes, ulong FileId)>()
                    : null;

                foreach (var (parentDir, groupItems) in parentGroups)
                {
                    ct.ThrowIfCancellationRequested();

                    if (!EnsureVerifiedSubtreeDirectory(parentDir, normalizedRoot, liveRoot.VolumeGuid, verifiedSubdirs))
                    {
                        skippedCount += groupItems.Count;
                        checkedFiles += groupItems.Count;
                        ReportVerifyProgressIfDue(
                            checkedFiles, totalExpectedFiles, verifiedBytes, ref lastVerifyTick,
                            rule.DisplayName, ReportPhase);
                        continue;
                    }

                    bool canAuditForFolding = dirAudits is not null
                        && !parentDir.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                        && PathIdentityService.IsWithin(parentDir, normalizedRoot);

                    // 极小目录（<= 2 个待核验文件）直接单句柄探测，避免在含上万无关文件的目录里全量枚举；
                    // 批量文件目录通过单次 GetFileInformationByHandleEx(FileIdBothDirectoryInfo) 批量拿到 FileId + Attributes + Size + LastWriteTime
                    Dictionary<string, LiveDirFileInfo>? liveByName = null;
                    DirFoldAudit? currentAudit = null;
                    bool batchEnumComplete = false;
                    if (groupItems.Count >= 3)
                    {
                        var expectedNames = new HashSet<string>(groupItems.Count, StringComparer.OrdinalIgnoreCase);
                        foreach (var (_, name) in groupItems)
                        {
                            expectedNames.Add(name);
                        }

                        if (canAuditForFolding)
                        {
                            currentAudit = new DirFoldAudit();
                        }

                        var matchedLive = new Dictionary<string, LiveDirFileInfo>(Math.Min(groupItems.Count, 256), StringComparer.OrdinalIgnoreCase);
                        DirectoryEnumStatus enumStatus = Win32DirectoryEnumerator.EnumerateDirectoryChunked(
                            parentDir,
                            isRootDirectory: false,
                            ref enumBuffer,
                            sliceBatch,
                            (rawBuf, slices) =>
                            {
                                for (int i = 0; i < slices.Count; i++)
                                {
                                    ParsedDirEntrySlice slice = slices[i];
                                    string entryName = Encoding.Unicode.GetString(rawBuf, slice.NameOffset, slice.NameByteLength);
                                    if (currentAudit is not null)
                                    {
                                        if (slice.IsReparsePoint)
                                        {
                                            currentAudit.HasKeptOrUnsafeLiveEntry = true;
                                        }
                                        else if (slice.IsDirectory)
                                        {
                                            currentAudit.LiveChildDirs.Add(Path.Combine(parentDir, entryName));
                                        }
                                        else if (!expectedNames.Contains(entryName))
                                        {
                                            // 目录内存在未进入待清快照的实时文件（如 <24h 活跃文件、新增文件或排除文件），严禁折叠整目录
                                            currentAudit.HasKeptOrUnsafeLiveEntry = true;
                                        }
                                    }

                                    if (expectedNames.Contains(entryName))
                                    {
                                        matchedLive[entryName] = new LiveDirFileInfo(
                                            slice.IsDirectory,
                                            slice.IsReparsePoint,
                                            slice.LogicalBytes,
                                            slice.LastWriteUtcTicks,
                                            slice.FileId);
                                    }
                                }
                            },
                            ct);

                        if (enumStatus == DirectoryEnumStatus.Cancelled)
                        {
                            throw new OperationCanceledException(ct);
                        }

                        liveByName = matchedLive;
                        batchEnumComplete = enumStatus == DirectoryEnumStatus.Ok;
                        if (currentAudit is not null)
                        {
                            currentAudit.EnumComplete = batchEnumComplete;
                            if (CleanupExclusionPolicy.EvaluateFileForRuleCleanup(parentDir, protectedTxPaths, _policyEnvironment, isBrowserRule).IsExcluded)
                            {
                                currentAudit.HasKeptOrUnsafeLiveEntry = true;
                            }
                            dirAudits![parentDir] = currentAudit;
                        }
                    }

                    foreach (var (exp, fileName) in groupItems)
                    {
                        ct.ThrowIfCancellationRequested();
                        checkedFiles++;

                        bool hasLive = false;
                        LiveDirFileInfo live = default;

                        if (liveByName is not null && liveByName.TryGetValue(fileName, out LiveDirFileInfo batchHit))
                        {
                            // 若文件系统不支持 FileIdBothDirectoryInfo 而降级为 FileFullDirectoryInfo（FileId == 0），且快照绑定了 ExpectedFileId，则补做单文件探测以维持同等安全级别
                            if (exp.ExpectedFileId != 0 && batchHit.FileId == 0)
                            {
                                hasLive = TryReadDirectLiveFileInfo(exp.FullPath, liveRoot.VolumeGuid, out live);
                            }
                            else
                            {
                                hasLive = true;
                                live = batchHit;
                            }
                        }
                        else if (!batchEnumComplete)
                        {
                            hasLive = TryReadDirectLiveFileInfo(exp.FullPath, liveRoot.VolumeGuid, out live);
                        }

                        if (!hasLive)
                        {
                            skippedCount++;
                            ReportVerifyProgressIfDue(
                                checkedFiles, totalExpectedFiles, verifiedBytes, ref lastVerifyTick,
                                rule.DisplayName, ReportPhase);
                            continue;
                        }

                        if (live.IsDirectory || live.IsReparsePoint)
                        {
                            if (currentAudit is not null) currentAudit.HasKeptOrUnsafeLiveEntry = true;
                            skippedCount++;
                            details.Add($"对象类型/重解析点变化已跳过：{exp.FullPath}");
                            ReportVerifyProgressIfDue(
                                checkedFiles, totalExpectedFiles, verifiedBytes, ref lastVerifyTick,
                                rule.DisplayName, ReportPhase);
                            continue;
                        }

                        if (exp.ExpectedFileId != 0 && live.FileId != 0
                            && (live.FileId & 0x0000_FFFF_FFFF_FFFFUL) != (exp.ExpectedFileId & 0x0000_FFFF_FFFF_FFFFUL))
                        {
                            if (currentAudit is not null) currentAudit.HasKeptOrUnsafeLiveEntry = true;
                            skippedCount++;
                            details.Add($"文件身份变化已跳过：{exp.FullPath}");
                            ReportVerifyProgressIfDue(
                                checkedFiles, totalExpectedFiles, verifiedBytes, ref lastVerifyTick,
                                rule.DisplayName, ReportPhase);
                            continue;
                        }

                        long maxAllowedWriteTicks = Math.Max(exp.ExpectedLastWriteUtcTicks, snapshotTicks);
                        if (live.LogicalBytes != exp.ExpectedLogicalBytes
                            || live.LastWriteUtcTicks > maxAllowedWriteTicks
                            || (rule.MinAgeHours > 0 && live.LastWriteUtcTicks > minCutoffUtcTicks))
                        {
                            if (currentAudit is not null) currentAudit.HasKeptOrUnsafeLiveEntry = true;
                            skippedCount++;
                            details.Add($"扫描后发生变更或为近期文件，已安全跳过：{exp.FullPath}");
                            ReportVerifyProgressIfDue(
                                checkedFiles, totalExpectedFiles, verifiedBytes, ref lastVerifyTick,
                                rule.DisplayName, ReportPhase);
                            continue;
                        }

                        verifiedReadyFiles.Add((exp.FullPath, live.LogicalBytes, false, live.FileId));
                        verifiedBytes += live.LogicalBytes;
                        currentAudit?.VerifiedFiles.Add((exp.FullPath, live.LogicalBytes, live.FileId));
                        rootVerifiedFiles?.Add((exp.FullPath, live.LogicalBytes, live.FileId));

                        ReportVerifyProgressIfDue(
                            checkedFiles, totalExpectedFiles, verifiedBytes, ref lastVerifyTick,
                            rule.DisplayName, ReportPhase);
                    }
                }

                if (enableSubtreeFolding && rootVerifiedFiles is { Count: > 0 })
                {
                    AppendFoldedQuarantineUnits(
                        normalizedRoot,
                        liveRoot.VolumeGuid,
                        protectedTxPaths,
                        isBrowserRule,
                        dirAudits!,
                        rootVerifiedFiles,
                        ref enumBuffer,
                        sliceBatch,
                        quarantineMoveUnits,
                        ct);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(enumBuffer);
        }

        if (verifiedReadyFiles.Count == 0)
        {
            return new CleanupExecutionItemResult(
                rule.RuleId,
                rule.DisplayName,
                rule.Channel,
                CleanupItemExecutionStatus.Skipped,
                0, 0, skippedCount, 0, null,
                skippedCount > 0
                    ? $"执行前复核已跳过 {skippedCount} 个消失/变更/近期文件，无需处理。"
                    : "没有待处理文件。",
                CapDetailsSafely(details));
        }

        if (effectiveAction == CleanupFileAction.QuarantineSameVolume)
        {
            int totalMoved = 0;
            int totalFailed = 0;
            long totalMovedBytes = 0;
            int totalReadyFiles = verifiedReadyFiles.Count;
            int completedBeforeGroup = 0;
            long movedBytesBeforeGroup = 0;
            string? firstBatchId = null;

            foreach (var volGroup in quarantineMoveUnits.GroupBy(u => PathIdentityService.Normalize(u.FullPath)[..3], StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var units = volGroup.ToList();
                int volLogicalFiles = units.Sum(u => Math.Max(1, u.LogicalFileCount));
                string sourceSummary = $"{rule.DisplayName}（{volLogicalFiles:N0} 个文件 · {volGroup.Key}）";
                var moveProgress = new Progress<(int Done, int Total, long MovedBytes)>(p =>
                {
                    int overallDone = completedBeforeGroup + p.Done;
                    long overallBytes = movedBytesBeforeGroup + p.MovedBytes;
                    double frac = 0.35 + 0.65 * overallDone / Math.Max(1, totalReadyFiles);
                    ReportPhase(
                        "移入同卷隔离区",
                        overallDone,
                        totalReadyFiles,
                        overallBytes,
                        frac,
                        $"正在移入同卷隔离区：{rule.DisplayName}（{overallDone:N0} / {totalReadyFiles:N0} 文件 · {FileStatsService.FormatBytes(overallBytes)}）…");
                });

                var (batch, moved, failed, bytes, qDetails) = await _quarantineStore
                    .QuarantineMoveUnitsSameVolumeAsync(rule.DisplayName, sourceSummary, rule.RuleId, false, units, ct, moveProgress)
                    .ConfigureAwait(false);

                completedBeforeGroup += volLogicalFiles;
                movedBytesBeforeGroup += bytes;
                if (batch is not null) firstBatchId ??= batch.BatchId;
                totalMoved += moved;
                totalFailed += failed;
                totalMovedBytes += bytes;
                details.AddRange(qDetails);
            }

            CleanupItemExecutionStatus status = totalMoved > 0
                ? CleanupItemExecutionStatus.Quarantined
                : totalFailed > 0 ? CleanupItemExecutionStatus.Failed : CleanupItemExecutionStatus.Skipped;

            string msg = status == CleanupItemExecutionStatus.Quarantined
                ? $"已移入同卷隔离区 {totalMoved:N0} 个文件（{FileStatsService.FormatBytes(totalMovedBytes)}，尚未释放物理空间；可随时恢复）。"
                : $"未能隔离文件（跳过 {skippedCount}，失败 {totalFailed}），原文件已保留。";

            return new CleanupExecutionItemResult(
                rule.RuleId,
                rule.DisplayName,
                rule.Channel,
                status,
                totalMovedBytes,
                totalMoved,
                skippedCount,
                totalFailed,
                firstBatchId,
                msg,
                CapDetailsSafely(details));
        }
        else if (effectiveAction == CleanupFileAction.DirectPermanentDelete)
        {
            int totalReadyFiles = verifiedReadyFiles.Count;
            ReportPhase(
                "直接永久删除",
                0,
                totalReadyFiles,
                0,
                0.35,
                $"正在直接永久删除 1 档推荐缓存：{rule.DisplayName}（0 / {totalReadyFiles:N0} 文件）…");

            var deleteProgress = new Progress<(int Done, int Total, long DeletedBytes)>(p =>
            {
                double frac = 0.35 + 0.65 * p.Done / Math.Max(1, p.Total);
                ReportPhase(
                    "直接永久删除",
                    p.Done,
                    p.Total,
                    p.DeletedBytes,
                    frac,
                    $"正在直接永久删除：{rule.DisplayName}（{p.Done:N0} / {p.Total:N0} 文件 · 已释放 {FileStatsService.FormatBytes(p.DeletedBytes)}）…");
            });

            var (deletedCount, extraSkipped, failedCount, deletedBytes, deleteDetails) = await Task.Run(
                () => DeleteVerifiedFilesDirectly(verifiedReadyFiles, ct, deleteProgress),
                ct).ConfigureAwait(false);

            skippedCount += extraSkipped;
            details.AddRange(deleteDetails);
            CleanupItemExecutionStatus status = deletedCount > 0
                ? CleanupItemExecutionStatus.PermanentlyDeleted
                : failedCount > 0 ? CleanupItemExecutionStatus.Failed : CleanupItemExecutionStatus.Skipped;

            string msg = status == CleanupItemExecutionStatus.PermanentlyDeleted
                ? $"已直接永久删除 {deletedCount:N0} 个文件，立即释放 {FileStatsService.FormatBytes(deletedBytes)} 物理空间。"
                : $"未能删除文件（跳过 {skippedCount}，失败 {failedCount}），原文件已保留。";

            return new CleanupExecutionItemResult(
                rule.RuleId,
                rule.DisplayName,
                rule.Channel,
                status,
                deletedBytes,
                deletedCount,
                skippedCount,
                failedCount,
                null,
                msg,
                CapDetailsSafely(details));
        }
        else
        {
            int totalReadyFiles = verifiedReadyFiles.Count;
            ReportPhase(
                "移入系统回收站",
                0,
                totalReadyFiles,
                0,
                0.35,
                $"正在通过系统回收站安全回收：{rule.DisplayName}（0 / {totalReadyFiles:N0} 文件）…");

            var recycleProgress = new Progress<(int Done, int Total, long RecycledBytes)>(p =>
            {
                double frac = 0.35 + 0.65 * p.Done / Math.Max(1, p.Total);
                ReportPhase(
                    "移入系统回收站",
                    p.Done,
                    p.Total,
                    p.RecycledBytes,
                    frac,
                    $"正在移入系统回收站：{rule.DisplayName}（{p.Done:N0} / {p.Total:N0} 文件 · {FileStatsService.FormatBytes(p.RecycledBytes)}）…");
            });

            var (recycledCount, failedCount, recycledBytes, recycleDetails) = await Task.Run(
                () => RecycleVerifiedFilesNoPermanentFallback(verifiedReadyFiles, ct, recycleProgress),
                ct).ConfigureAwait(false);

            details.AddRange(recycleDetails);
            CleanupItemExecutionStatus status = recycledCount > 0
                ? CleanupItemExecutionStatus.Recycled
                : failedCount > 0 ? CleanupItemExecutionStatus.Failed : CleanupItemExecutionStatus.Skipped;

            string msg = status == CleanupItemExecutionStatus.Recycled
                ? $"已移入回收站 {recycledCount:N0} 个文件（{FileStatsService.FormatBytes(recycledBytes)}，清空回收站前通常不立即释放空间）。"
                : $"回收站未能接收文件（跳过 {skippedCount}，未回收 {failedCount}），已拒绝永久删除并保留原文件。";

            return new CleanupExecutionItemResult(
                rule.RuleId,
                rule.DisplayName,
                rule.Channel,
                status,
                recycledBytes,
                recycledCount,
                skippedCount,
                failedCount,
                null,
                msg,
                CapDetailsSafely(details));
        }
    }

    public async Task<(CleanupExecutionItemResult[] Items, bool Cancelled)> ExecuteAiResidualItemsAsync(
        AiCleanupCandidateRecord[] eligible,
        IReadOnlyList<string> protectedTxPaths,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var itemResults = new List<CleanupExecutionItemResult>(eligible.Length);
        bool cancelled = false;

        foreach (AiCleanupCandidateRecord cand in eligible)
        {
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            progress?.Report($"正在二次核验并同卷隔离：{cand.DisplayName}…");

            CleanupExclusionVerdict exclusion = CleanupExclusionPolicy.EvaluateAiSuspectedResidualDirectory(
                cand.ActualLocalPath,
                snapshot: null,
                nodeId: null,
                registeredTransactionPaths: protectedTxPaths,
                environment: _policyEnvironment,
                checkFilesystem: true);

            if (exclusion.IsExcluded)
            {
                itemResults.Add(new CleanupExecutionItemResult(
                    cand.CandidateId.ToString("D"),
                    cand.DisplayName,
                    CleanupExecutionChannel.FileQuarantine,
                    CleanupItemExecutionStatus.Blocked,
                    0, 0, 0, 1, null,
                    exclusion.Message ?? "命中永久排除区，强制拒收。",
                    [exclusion.ReasonCode ?? CleanupExclusionPolicy.ReasonCodes.SystemCoreOrProtected]));
                continue;
            }

            DirectoryIdentity? dirId = _identity.ReadIfExists(cand.ActualLocalPath);
            if (dirId is null)
            {
                itemResults.Add(new CleanupExecutionItemResult(
                    cand.CandidateId.ToString("D"),
                    cand.DisplayName,
                    CleanupExecutionChannel.FileQuarantine,
                    CleanupItemExecutionStatus.Skipped,
                    0, 0, 1, 0, null,
                    "目录已不存在，已安全跳过。",
                    []));
                continue;
            }

            if (!dirId.IsDirectory || dirId.IsReparsePoint)
            {
                itemResults.Add(new CleanupExecutionItemResult(
                    cand.CandidateId.ToString("D"),
                    cand.DisplayName,
                    CleanupExecutionChannel.FileQuarantine,
                    CleanupItemExecutionStatus.Blocked,
                    0, 0, 0, 1, null,
                    "目录已变为符号链接/重解析点或普通文件，已停止隔离以防删穿。",
                    []));
                continue;
            }

            if (ContainsReparsePointInSubtree(cand.ActualLocalPath, ct))
            {
                itemResults.Add(new CleanupExecutionItemResult(
                    cand.CandidateId.ToString("D"),
                    cand.DisplayName,
                    CleanupExecutionChannel.FileQuarantine,
                    CleanupItemExecutionStatus.Blocked,
                    0, 0, 0, 1, null,
                    "执行前复核发现目录子树内包含符号链接/重解析点，已安全阻断。",
                    []));
                continue;
            }

            var (batch, movedCount, failedCount, movedBytes, details) =
                await _quarantineStore.QuarantineItemsSameVolumeAsync(
                    displayTitle: $"AI 疑似残留 · {cand.DisplayName}",
                    sourceDisplaySummary: $"{cand.ActualLocalPath}（AI 疑似残留 · 可恢复）",
                    ruleId: null,
                    isFromAiSuspectedResidual: true,
                    items: [(cand.ActualLocalPath, cand.SizeBytes, true, dirId.FileId)],
                    ct: ct).ConfigureAwait(false);

            if (batch is not null && movedCount > 0)
            {
                itemResults.Add(new CleanupExecutionItemResult(
                    cand.CandidateId.ToString("D"),
                    cand.DisplayName,
                    CleanupExecutionChannel.FileQuarantine,
                    CleanupItemExecutionStatus.Quarantined,
                    movedBytes,
                    movedCount,
                    0,
                    failedCount,
                    batch.BatchId,
                    $"已原子移入同卷隔离区（{batch.BatchId}，尚未释放物理空间；可随时一键恢复）。",
                    details));
            }
            else
            {
                itemResults.Add(new CleanupExecutionItemResult(
                    cand.CandidateId.ToString("D"),
                    cand.DisplayName,
                    CleanupExecutionChannel.FileQuarantine,
                    CleanupItemExecutionStatus.Failed,
                    0, 0, 0, Math.Max(1, failedCount), null,
                    details.FirstOrDefault() ?? "未能移入同卷隔离区（可能正被占用），原目录已完整保留。",
                    details));
            }
        }

        return (itemResults.ToArray(), cancelled);
    }

    private const int MinFilesToFoldDirectory = 3;

    private sealed class DirFoldAudit
    {
        public bool EnumComplete;
        public bool HasKeptOrUnsafeLiveEntry;
        public List<string> LiveChildDirs { get; } = [];
        public List<(string FullPath, long SizeBytes, ulong FileId)> VerifiedFiles { get; } = [];
    }

    /// <summary>
    /// 自底向上识别 100% 干净的可折叠子目录（不含根目录本身、0 个重解析点、0 个保留/活跃/新增/排除文件、且所有实时子目录均同样干净），
    /// 将数百~数万文件折叠为单次 O(1) 整目录 MoveFileExW，并携带逐文件 Fallback 清单以防目录内个别文件被占用。
    /// </summary>
    private void AppendFoldedQuarantineUnits(
        string normalizedRoot,
        string expectedVolumeGuid,
        IReadOnlyList<string> protectedTxPaths,
        bool isBrowserRule,
        Dictionary<string, DirFoldAudit> dirAudits,
        List<(string FullPath, long SizeBytes, ulong FileId)> rootVerifiedFiles,
        ref byte[] enumBuffer,
        List<ParsedDirEntrySlice> sliceBatch,
        List<QuarantineMoveUnit> outputUnits,
        CancellationToken ct)
    {
        // 收集所有候选目录及其位于 normalizedRoot 之下的中间祖先目录（向上尝试折叠仅含干净子目录的父目录）
        var candidateDirs = new HashSet<string>(dirAudits.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in dirAudits)
        {
            if (!kv.Value.EnumComplete || kv.Value.HasKeptOrUnsafeLiveEntry) continue;
            string? ancestor = Path.GetDirectoryName(kv.Key);
            while (!string.IsNullOrEmpty(ancestor)
                && !ancestor.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
                && PathIdentityService.IsWithin(ancestor, normalizedRoot))
            {
                if (!candidateDirs.Add(ancestor)) break;
                ancestor = Path.GetDirectoryName(ancestor);
            }
        }

        // 按路径长度降序（最深子目录优先）逐层向上汇总干净子树
        string[] orderedDirs = candidateDirs
            .OrderByDescending(d => d.Length)
            .ThenBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var cleanSubtrees = new Dictionary<string, (List<(string FullPath, long SizeBytes, ulong FileId)> Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);

        foreach (string dir in orderedDirs)
        {
            ct.ThrowIfCancellationRequested();

            if (!dirAudits.TryGetValue(dir, out DirFoldAudit? audit))
            {
                // 中间目录（本身未在 parentGroups 中直接命中 >=3 文件）：仅做 1 次快速实时枚举确认其无任何额外文件或重解析点
                audit = new DirFoldAudit();
                if (CleanupExclusionPolicy.EvaluateFileForRuleCleanup(dir, protectedTxPaths, _policyEnvironment, isBrowserRule).IsExcluded)
                {
                    audit.HasKeptOrUnsafeLiveEntry = true;
                }
                else
                {
                    DirectoryEnumStatus status = Win32DirectoryEnumerator.EnumerateDirectoryChunked(
                        dir,
                        isRootDirectory: false,
                        ref enumBuffer,
                        sliceBatch,
                        (rawBuf, slices) =>
                        {
                            for (int i = 0; i < slices.Count; i++)
                            {
                                ParsedDirEntrySlice slice = slices[i];
                                if (slice.IsReparsePoint)
                                {
                                    audit.HasKeptOrUnsafeLiveEntry = true;
                                }
                                else if (slice.IsDirectory)
                                {
                                    string name = Encoding.Unicode.GetString(rawBuf, slice.NameOffset, slice.NameByteLength);
                                    audit.LiveChildDirs.Add(Path.Combine(dir, name));
                                }
                                else
                                {
                                    // 中间目录本身若含有任何直接文件（未在批量目录审计中覆盖），保守视为不可折叠
                                    audit.HasKeptOrUnsafeLiveEntry = true;
                                }
                            }
                        },
                        ct);

                    if (status == DirectoryEnumStatus.Cancelled)
                    {
                        throw new OperationCanceledException(ct);
                    }
                    audit.EnumComplete = status == DirectoryEnumStatus.Ok;
                }
                dirAudits[dir] = audit;
            }

            if (!audit.EnumComplete || audit.HasKeptOrUnsafeLiveEntry)
            {
                continue;
            }

            bool allChildrenClean = true;
            for (int i = 0; i < audit.LiveChildDirs.Count; i++)
            {
                if (!cleanSubtrees.ContainsKey(audit.LiveChildDirs[i]))
                {
                    allChildrenClean = false;
                    break;
                }
            }
            if (!allChildrenClean)
            {
                continue;
            }

            var subtreeFiles = new List<(string FullPath, long SizeBytes, ulong FileId)>(audit.VerifiedFiles.Count);
            long subtreeBytes = 0;
            for (int i = 0; i < audit.VerifiedFiles.Count; i++)
            {
                var f = audit.VerifiedFiles[i];
                subtreeFiles.Add(f);
                subtreeBytes += f.SizeBytes;
            }

            for (int i = 0; i < audit.LiveChildDirs.Count; i++)
            {
                var child = cleanSubtrees[audit.LiveChildDirs[i]];
                subtreeFiles.AddRange(child.Files);
                subtreeBytes += child.Bytes;
            }

            if (subtreeFiles.Count > 0)
            {
                cleanSubtrees[dir] = (subtreeFiles, subtreeBytes);
            }
        }

        // 选出极大可折叠目录（父目录未被折叠且子树文件数 >= MinFilesToFoldDirectory）
        var foldedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in cleanSubtrees)
        {
            string dir = kv.Key;
            string? parent = Path.GetDirectoryName(dir);
            if (!string.IsNullOrEmpty(parent) && cleanSubtrees.ContainsKey(parent))
            {
                continue;
            }

            if (kv.Value.Files.Count < MinFilesToFoldDirectory)
            {
                continue;
            }

            DirectoryIdentity? dirId;
            try
            {
                dirId = _identity.ReadDirectIfExists(dir, followLink: false);
            }
            catch
            {
                dirId = null;
            }

            if (dirId is null
                || !dirId.IsDirectory
                || dirId.IsReparsePoint
                || !string.Equals(dirId.VolumeGuid, expectedVolumeGuid, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            outputUnits.Add(new QuarantineMoveUnit(
                FullPath: dir,
                SizeBytes: kv.Value.Bytes,
                IsDirectory: true,
                FileId: dirId.FileId,
                LogicalFileCount: kv.Value.Files.Count,
                FallbackFiles: kv.Value.Files));

            for (int i = 0; i < kv.Value.Files.Count; i++)
            {
                foldedFilePaths.Add(kv.Value.Files[i].FullPath);
            }
        }

        // 未被折叠的散落文件保持逐文件移入
        for (int i = 0; i < rootVerifiedFiles.Count; i++)
        {
            var f = rootVerifiedFiles[i];
            if (!foldedFilePaths.Contains(f.FullPath))
            {
                outputUnits.Add(new QuarantineMoveUnit(f.FullPath, f.SizeBytes, IsDirectory: false, f.FileId, LogicalFileCount: 1));
            }
        }
    }

    private readonly record struct LiveDirFileInfo(
        bool IsDirectory,
        bool IsReparsePoint,
        long LogicalBytes,
        long LastWriteUtcTicks,
        ulong FileId);

    private static void ReportVerifyProgressIfDue(
        int checkedFiles,
        int totalExpectedFiles,
        long verifiedBytes,
        ref long lastVerifyTick,
        string ruleDisplayName,
        Action<string, int, int, long, double, string> reportPhase)
    {
        if (totalExpectedFiles <= 0) return;
        long nowTick = Environment.TickCount64;
        if (checkedFiles == totalExpectedFiles || checkedFiles % 200 == 0 || nowTick - lastVerifyTick >= 80)
        {
            lastVerifyTick = nowTick;
            double frac = 0.35 * checkedFiles / totalExpectedFiles;
            reportPhase(
                "执行前快照复核",
                checkedFiles,
                totalExpectedFiles,
                verifiedBytes,
                frac,
                $"正在执行前安全复核：{ruleDisplayName}（{checkedFiles:N0} / {totalExpectedFiles:N0} 文件）…");
        }
    }

    private bool EnsureVerifiedSubtreeDirectory(
        string parentDir,
        string normalizedRoot,
        string expectedVolumeGuid,
        Dictionary<string, bool> verifiedSubdirs)
    {
        if (verifiedSubdirs.TryGetValue(parentDir, out bool cached))
        {
            return cached;
        }

        // 若该目录不在已核验的 normalizedRoot 子树内，走完整祖先链核验并缓存结果
        if (!PathIdentityService.IsWithin(parentDir, normalizedRoot))
        {
            DirectoryIdentity? fullId = TryReadIdentitySafe(parentDir);
            bool ok = fullId is not null
                && fullId.IsDirectory
                && !fullId.IsReparsePoint
                && string.Equals(fullId.VolumeGuid, expectedVolumeGuid, StringComparison.OrdinalIgnoreCase);
            verifiedSubdirs[parentDir] = ok;
            return ok;
        }

        // 向上收集尚未核验的相对中间目录（保证祖先先于子目录完成单次校验，绝不重复查根路径以上的祖先链）
        var pending = new Stack<string>();
        string? cursor = parentDir;
        while (!string.IsNullOrEmpty(cursor) && !verifiedSubdirs.TryGetValue(cursor, out bool ancestorOk))
        {
            pending.Push(cursor);
            cursor = Path.GetDirectoryName(cursor);
        }

        if (string.IsNullOrEmpty(cursor) || !verifiedSubdirs[cursor])
        {
            while (pending.Count > 0)
            {
                verifiedSubdirs[pending.Pop()] = false;
            }
            return false;
        }

        bool chainValid = true;
        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            if (!chainValid)
            {
                verifiedSubdirs[dir] = false;
                continue;
            }

            DirectoryIdentity? subId;
            try
            {
                subId = _identity.ReadDirectIfExists(dir, followLink: false);
            }
            catch
            {
                subId = null;
            }

            chainValid = subId is not null
                && subId.IsDirectory
                && !subId.IsReparsePoint
                && string.Equals(subId.VolumeGuid, expectedVolumeGuid, StringComparison.OrdinalIgnoreCase);
            verifiedSubdirs[dir] = chainValid;
        }

        return chainValid;
    }

    private bool TryReadDirectLiveFileInfo(string fullPath, string expectedVolumeGuid, out LiveDirFileInfo live)
    {
        live = default;
        if (!_identity.TryReadDirectFileMetadata(fullPath, out DirectoryIdentity? id, out long length, out long writeTicks)
            || id is null)
        {
            return false;
        }

        if (!string.Equals(id.VolumeGuid, expectedVolumeGuid, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        live = new LiveDirFileInfo(id.IsDirectory, id.IsReparsePoint, length, writeTicks, id.FileId);
        return true;
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

    private static bool ContainsReparsePointInSubtree(string rootDir, CancellationToken ct)
    {
        var stack = new Stack<string>();
        stack.Push(rootDir);
        int visited = 0;

        while (stack.Count > 0 && visited < 50_000)
        {
            ct.ThrowIfCancellationRequested();
            string cur = stack.Pop();
            visited++;

            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(QuarantineStore.ToExtendedPath(cur)))
                {
                    FileAttributes attrs = File.GetAttributes(entry);
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }
                    if ((attrs & FileAttributes.Directory) != 0)
                    {
                        string stripped = entry.StartsWith(@"\\?\", StringComparison.Ordinal) ? entry[4..] : entry;
                        stack.Push(stripped);
                    }
                }
            }
            catch
            {
                return true;
            }
        }
        return false;
    }

    private static (int RecycledCount, int FailedCount, long RecycledBytes, string[] Details) RecycleVerifiedFilesNoPermanentFallback(
        IReadOnlyList<(string FullPath, long SizeBytes, bool IsDirectory, ulong FileId)> files,
        CancellationToken ct,
        IProgress<(int Done, int Total, long RecycledBytes)>? progress = null)
    {
        int recycled = 0;
        int failed = 0;
        long bytes = 0;
        int processed = 0;
        int totalFiles = files.Count;
        long lastTick = Environment.TickCount64;
        var details = new List<string>();

        var thread = new Thread(() =>
        {
            bool comInit = false;
            try
            {
                Check(CoInitializeEx(IntPtr.Zero, 2)); // STA
                comInit = true;

                Type opType = Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09"), true)!;
                foreach (var item in files)
                {
                    if (ct.IsCancellationRequested) break;
                    processed++;
                    var sink = new SingleFileRecycleSink();
                    IFileOperation? op = null;
                    IShellItem? shellItem = null;
                    uint cookie = 0;
                    bool advised = false;
                    try
                    {
                        op = (IFileOperation)Activator.CreateInstance(opType)!;
                        const uint noUi = 0x00000004 | 0x00000010 | 0x00000200 | 0x00000400;
                        Check(op.SetOperationFlags(0x00080000 | 0x00100000 | noUi));
                        Check(op.Advise(sink, out cookie));
                        advised = true;

                        Guid iid = typeof(IShellItem).GUID;
                        Check(SHCreateItemFromParsingName(item.FullPath, IntPtr.Zero, in iid, out shellItem));
                        Check(op.DeleteItem(shellItem, IntPtr.Zero));
                        Check(op.PerformOperations());
                        Check(op.GetAnyOperationsAborted(out bool aborted));

                        if (!aborted && sink.RecycledConfirmed && !File.Exists(QuarantineStore.ToExtendedPath(item.FullPath)))
                        {
                            recycled++;
                            bytes += item.SizeBytes;
                        }
                        else
                        {
                            failed++;
                            details.Add($"拒绝永久删除并保留原文件：{item.FullPath}（{sink.Error ?? "Shell 无法移入回收站"}）");
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        details.Add($"未回收（已保留）：{item.FullPath}（{ex.Message}）");
                    }
                    finally
                    {
                        if (op is not null && advised) { try { op.Unadvise(cookie); } catch { } }
                        if (shellItem is not null) Marshal.ReleaseComObject(shellItem);
                        if (op is not null) Marshal.ReleaseComObject(op);
                        GC.KeepAlive(sink);
                    }

                    if (progress is not null)
                    {
                        long nowTick = Environment.TickCount64;
                        if (processed == totalFiles || processed % 10 == 0 || nowTick - lastTick >= 80)
                        {
                            lastTick = nowTick;
                            progress.Report((processed, totalFiles, bytes));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                details.Add($"回收站 COM 初始化失败：{ex.Message}");
                failed += Math.Max(0, files.Count - recycled - failed);
            }
            finally
            {
                if (comInit) CoUninitialize();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        return (recycled, failed, bytes, details.ToArray());
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class SingleFileRecycleSink : IFileOperationProgressSink
    {
        public bool RecycledConfirmed { get; private set; }
        public string? Error { get; private set; }

        public int PreDeleteItem(uint flags, IntPtr item)
        {
            if ((flags & RecycleIfPossible) == 0)
            {
                Error = "Shell 判定不可回收，已拒绝降级永久删除";
                return Abort;
            }
            return 0;
        }

        public int PostDeleteItem(uint flags, IntPtr item, int result, IntPtr created)
        {
            if ((flags & RecycleIfPossible) == 0 || result < 0 || created == IntPtr.Zero)
            {
                Error = $"无实际回收站项目证据 (0x{result:X8})";
                return Abort;
            }
            RecycledConfirmed = true;
            return 0;
        }

        public int StartOperations() => 0;
        public int FinishOperations(int result) => 0;
        public int PreRenameItem(uint flags, IntPtr item, IntPtr name) => Abort;
        public int PostRenameItem(uint flags, IntPtr item, IntPtr name, int result, IntPtr created) => Abort;
        public int PreMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr name) => Abort;
        public int PostMoveItem(uint flags, IntPtr item, IntPtr destination, IntPtr name, int result, IntPtr created) => Abort;
        public int PreCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr name) => Abort;
        public int PostCopyItem(uint flags, IntPtr item, IntPtr destination, IntPtr name, int result, IntPtr created) => Abort;
        public int PreNewItem(uint flags, IntPtr destination, IntPtr name) => Abort;
        public int PostNewItem(uint flags, IntPtr destination, IntPtr name, IntPtr template, uint attributes, int result, IntPtr created) => Abort;
        public int UpdateProgress(uint total, uint completed) => 0;
        public int ResetTimer() => 0;
        public int PauseTimer() => 0;
        public int ResumeTimer() => 0;
    }

    /// <summary>
    /// 服务端硬安全分流：仅 1 档可重建推荐清理项允许执行 DirectPermanentDelete；
    /// 2/3 档或强制隔离项即使收到 DirectPermanentDelete 请求也强制回退为同卷隔离区（方式 A 分档分流）。
    /// </summary>
    internal static CleanupFileAction ResolveEffectiveAction(CleanupRuleDefinition rule, CleanupFileAction requestedAction)
    {
        if (rule.ForceSameVolumeQuarantineOnly)
        {
            return CleanupFileAction.QuarantineSameVolume;
        }

        if (requestedAction == CleanupFileAction.DirectPermanentDelete)
        {
            return rule.SupportsDirectPermanentDelete
                ? CleanupFileAction.DirectPermanentDelete
                : CleanupFileAction.QuarantineSameVolume;
        }

        return requestedAction;
    }

    private static (int DeletedCount, int ExtraSkippedCount, int FailedCount, long DeletedBytes, string[] Details) DeleteVerifiedFilesDirectly(
        IReadOnlyList<(string FullPath, long SizeBytes, bool IsDirectory, ulong FileId)> files,
        CancellationToken ct,
        IProgress<(int Done, int Total, long DeletedBytes)>? progress = null)
    {
        const int MaxLockDiagnostics = 10;
        int deleted = 0;
        int extraSkipped = 0;
        int failed = 0;
        int lockDiagCount = 0;
        long bytes = 0;
        int processed = 0;
        int totalFiles = files.Count;
        long lastTick = Environment.TickCount64;
        var details = new List<string>();

        foreach (var item in files)
        {
            if (ct.IsCancellationRequested) break;
            processed++;

            if (item.IsDirectory)
            {
                extraSkipped++;
                continue;
            }

            string extPath = QuarantineStore.ToExtendedPath(item.FullPath);
            try
            {
                if (!File.Exists(extPath))
                {
                    extraSkipped++;
                    continue;
                }

                try
                {
                    File.Delete(extPath);
                }
                catch (UnauthorizedAccessException)
                {
                    FileAttributes attrs = File.GetAttributes(extPath);
                    if ((attrs & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                        && (attrs & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(extPath, attrs & ~FileAttributes.ReadOnly);
                        File.Delete(extPath);
                    }
                    else
                    {
                        throw;
                    }
                }

                deleted++;
                bytes += item.SizeBytes;
            }
            catch (FileNotFoundException)
            {
                extraSkipped++;
            }
            catch (DirectoryNotFoundException)
            {
                extraSkipped++;
            }
            catch (Exception ex)
            {
                failed++;
                string lockSuffix = string.Empty;
                if (lockDiagCount < MaxLockDiagnostics && (ex is IOException or UnauthorizedAccessException))
                {
                    lockDiagCount++;
                    LockingProcessInfo[] lockers = FileLockInspector.FindLockingProcesses(item.FullPath, maxResults: 2);
                    if (lockers.Length > 0)
                    {
                        lockSuffix = $"；占用进程：{string.Join("、", lockers.Select(l => l.DisplayLabel))}";
                    }
                }
                details.Add($"未能删除（已保留原文件）：{item.FullPath}（{ex.Message}{lockSuffix}）");
            }

            if (progress is not null)
            {
                long nowTick = Environment.TickCount64;
                if (processed == totalFiles || processed % 20 == 0 || nowTick - lastTick >= 80)
                {
                    lastTick = nowTick;
                    progress.Report((processed, totalFiles, bytes));
                }
            }
        }

        return (deleted, extraSkipped, failed, bytes, details.ToArray());
    }

    private const int MaxReceiptDetailsPerRule = 20_000;

    private static string[] CapDetailsSafely(List<string> details)
    {
        if (details.Count <= MaxReceiptDetailsPerRule)
        {
            return details.ToArray();
        }

        var capped = new string[MaxReceiptDetailsPerRule + 1];
        details.CopyTo(0, capped, 0, MaxReceiptDetailsPerRule);
        capped[MaxReceiptDetailsPerRule] = $"… 共记录 {details.Count:N0} 条明细，已保留前 {MaxReceiptDetailsPerRule:N0} 条完整明细以防内存溢出。";
        return capped;
    }
}
