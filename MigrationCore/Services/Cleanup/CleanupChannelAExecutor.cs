using System.Runtime.InteropServices;
using MigrationCore.Models;
using MigrationCore.Models.Cleanup;
using MigrationCore.Models.Scanning;
using MigrationCore.Services.Safety;
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
        CleanupFileAction effectiveAction = rule.ForceSameVolumeQuarantineOnly
            ? CleanupFileAction.QuarantineSameVolume
            : requestedAction;

        bool isBrowserRule = rule.IsBrowserCacheRule || rule.RuleId.Equals("browser-cache-only", StringComparison.OrdinalIgnoreCase);
        DateTimeOffset minCutoffUtc = rule.MinAgeHours > 0
            ? DateTimeOffset.UtcNow.AddHours(-rule.MinAgeHours)
            : DateTimeOffset.MaxValue;

        var verifiedReadyFiles = new List<(string FullPath, long SizeBytes, bool IsDirectory, ulong FileId)>();
        int skippedCount = 0;
        var details = new List<string>();

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

            foreach (CleanupFileExpectation exp in dirExp.ExpectedFiles)
            {
                ct.ThrowIfCancellationRequested();
                checkedFiles++;

                CleanupExclusionVerdict excl = CleanupExclusionPolicy.EvaluateFileForRuleCleanup(
                    exp.FullPath, protectedTxPaths, _policyEnvironment, isBrowserRule);
                if (excl.IsExcluded)
                {
                    skippedCount++;
                    details.Add($"永久排除跳过：{exp.FullPath}");
                    continue;
                }

                DirectoryIdentity? liveId = TryReadIdentitySafe(exp.FullPath);
                if (liveId is null)
                {
                    skippedCount++;
                    continue;
                }

                if (liveId.IsDirectory || liveId.IsReparsePoint)
                {
                    skippedCount++;
                    details.Add($"对象类型/重解析点变化已跳过：{exp.FullPath}");
                    continue;
                }

                if (exp.ExpectedFileId != 0 && liveId.FileId != 0
                    && (liveId.FileId & 0x0000_FFFF_FFFF_FFFFUL) != (exp.ExpectedFileId & 0x0000_FFFF_FFFF_FFFFUL))
                {
                    skippedCount++;
                    details.Add($"文件身份变化已跳过：{exp.FullPath}");
                    continue;
                }

                try
                {
                    var fi = new FileInfo(QuarantineStore.ToExtendedPath(exp.FullPath));
                    if (!fi.Exists
                        || fi.Length != exp.ExpectedLogicalBytes
                        || fi.LastWriteTimeUtc.Ticks > Math.Max(exp.ExpectedLastWriteUtcTicks, dirExp.SnapshotCompletedAtUtc.UtcTicks)
                        || (rule.MinAgeHours > 0 && fi.LastWriteTimeUtc > minCutoffUtc.UtcDateTime))
                    {
                        skippedCount++;
                        details.Add($"扫描后发生变更或为近期文件，已安全跳过：{exp.FullPath}");
                        continue;
                    }

                    verifiedReadyFiles.Add((exp.FullPath, fi.Length, false, liveId.FileId));
                    verifiedBytes += fi.Length;
                }
                catch
                {
                    skippedCount++;
                }

                if (totalExpectedFiles > 0)
                {
                    long nowTick = Environment.TickCount64;
                    if (checkedFiles == totalExpectedFiles || checkedFiles % 60 == 0 || nowTick - lastVerifyTick >= 80)
                    {
                        lastVerifyTick = nowTick;
                        double frac = 0.35 * checkedFiles / totalExpectedFiles;
                        ReportPhase(
                            "执行前快照复核",
                            checkedFiles,
                            totalExpectedFiles,
                            verifiedBytes,
                            frac,
                            $"正在执行前安全复核：{rule.DisplayName}（{checkedFiles:N0} / {totalExpectedFiles:N0} 文件）…");
                    }
                }
            }
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
                details.Take(20).ToArray());
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

            foreach (var volGroup in verifiedReadyFiles.GroupBy(f => PathIdentityService.Normalize(f.FullPath)[..3], StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var list = volGroup.ToList();
                string sourceSummary = $"{rule.DisplayName}（{list.Count:N0} 个文件 · {volGroup.Key}）";
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
                    .QuarantineItemsSameVolumeAsync(rule.DisplayName, sourceSummary, rule.RuleId, false, list, ct, moveProgress)
                    .ConfigureAwait(false);

                completedBeforeGroup += list.Count;
                movedBytesBeforeGroup += bytes;
                if (batch is not null) firstBatchId ??= batch.BatchId;
                totalMoved += moved;
                totalFailed += failed;
                totalMovedBytes += bytes;
                details.AddRange(qDetails.Take(20));
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
                details.Take(30).ToArray());
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

            details.AddRange(recycleDetails.Take(20));
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
                details.Take(30).ToArray());
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
}
