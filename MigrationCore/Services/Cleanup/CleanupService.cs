using MigrationCore.Models.Cleanup;
using MigrationCore.Services.Safety;
using MigrationCore.Services.Scanning;

namespace MigrationCore.Services.Cleanup;

/// <summary>
/// 独立垃圾清理服务门面（三期工作包 G1 核心）。
/// 内部按正交职责委托给 CleanupRootPathResolver、CleanupRuleScanner、CleanupOfficialApiHandlers 与 CleanupChannelAExecutor。
/// 架构与安全红线：
/// 1. 严格隔离双执行通道：通道 A（文件级同卷隔离 / 可选回收站）与通道 B（Windows 官方接口 DISM / pnputil / DO 等）；
/// 2. 所有写/删变更操作均获取 OperationExecutionGate.Shared 排他租约，与迁移/还原/备份管理严格互斥；
/// 3. 通道 A 执行前强制实时身份/时间戳/大小/重解析点/永久排除区复核（「宁漏勿错、绝不多删」）；
/// 4. 回收站模式走 IFileOperation + RecycleIfPossible 守卫，失败绝不自动降级为永久删除；
/// 5. 规则外「AI 疑似残留」物理锁定仅通道 A 同卷隔离，严禁调用通道 B 或永久删除，强制双勾选 + 永久排除区二次硬拦截；
/// 6. 绝不强杀运行中进程、不截断日志、不取得所有权或放宽 ACL。
/// </summary>
public sealed class CleanupService
{
    public const int MaxPreviewItemsPerRule = 200;
    public const int MaxFilesPerRuleScan = 50_000;

    private readonly QuarantineStore _quarantineStore;
    private readonly string? _transactionStateDirectory;
    private readonly CleanupRootPathResolver _rootResolver;
    private readonly CleanupRuleScanner _scanner;
    private readonly CleanupOfficialApiHandlers _officialHandlers;
    private readonly CleanupChannelAExecutor _channelAExecutor;

    public QuarantineStore QuarantineStore => _quarantineStore;

    public CleanupService(
        QuarantineStore? quarantineStore = null,
        PathIdentityService? identity = null,
        TargetedPathVerifier? verifier = null,
        SafetyPolicyEnvironment? policyEnvironment = null,
        string? transactionStateDirectory = null,
        Func<string, bool>? processRunningOverride = null,
        Func<CleanupOfficialApiKind, CleanupRuleScanResult, CancellationToken, Task<(bool Succeeded, long ReclaimedBytes, string Message, string[] Details)>>? officialApiExecutorOverride = null,
        VolumeSnapshotCache? snapshotCache = null)
    {
        PathIdentityService resolvedIdentity = identity ?? new PathIdentityService();
        _quarantineStore = quarantineStore ?? new QuarantineStore(resolvedIdentity);
        _ = verifier; // 保留构造参数兼容性
        _transactionStateDirectory = transactionStateDirectory;

        _rootResolver = new CleanupRootPathResolver(policyEnvironment);
        _scanner = new CleanupRuleScanner(
            resolvedIdentity,
            snapshotCache ?? VolumeSnapshotCache.Shared,
            policyEnvironment,
            _rootResolver,
            processRunningOverride);
        _officialHandlers = new CleanupOfficialApiHandlers(
            resolvedIdentity,
            policyEnvironment,
            _scanner,
            officialApiExecutorOverride);
        _channelAExecutor = new CleanupChannelAExecutor(
            _quarantineStore,
            resolvedIdentity,
            policyEnvironment);
    }

    /// <summary>
    /// 只读扫描内置声明式清理规则，产出冻结预览快照（不改写任何文件、不默认勾选任何项）。
    /// </summary>
    public async Task<CleanupScanSnapshot> ScanAsync(
        IReadOnlyList<CleanupRuleDefinition>? rules = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        IProgress<CleanupScanProgress>? detailedProgress = null)
    {
        IReadOnlyList<CleanupRuleDefinition> activeRules = rules ?? BuiltInCleanupRules.All;
        DateTimeOffset scannedAt = DateTimeOffset.UtcNow;
        var (protectedTxPaths, completedMigrations) = CleanupExclusionPolicy.LoadBuiltInRuleTransactionContext(_transactionStateDirectory);
        var rootCache = new CleanupRootResolutionCache();
        HashSet<string>? runningProcesses = _scanner.CaptureRunningProcessSnapshot();

        var results = new List<CleanupRuleScanResult>(activeRules.Count);
        int totalRules = activeRules.Count;

        for (int i = 0; i < totalRules; i++)
        {
            ct.ThrowIfCancellationRequested();
            CleanupRuleDefinition rule = activeRules[i];
            int stepIndex = i + 1;
            string startMsg = $"正在只读扫描 ({stepIndex}/{totalRules})：{rule.DisplayName}…";
            progress?.Report(startMsg);
            detailedProgress?.Report(new CleanupScanProgress(i, totalRules, rule.DisplayName, startMsg));

            IProgress<string>? combinedRuleProgress = (progress is not null || detailedProgress is not null)
                ? new Progress<string>(msg =>
                {
                    progress?.Report(msg);
                    detailedProgress?.Report(new CleanupScanProgress(i, totalRules, rule.DisplayName, msg));
                })
                : null;

            CleanupRuleScanResult ruleResult = await Task.Run(() =>
            {
                try
                {
                    return ScanSingleRule(
                        rule, protectedTxPaths, completedMigrations, scannedAt, stepIndex, totalRules,
                        rootCache, runningProcesses, combinedRuleProgress, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return CleanupRuleScanner.EmptyRuleResult(rule, [], scannedAt, $"该规则只读扫描受限已跳过：{ex.Message}");
                }
            }, ct).ConfigureAwait(false);
            results.Add(ruleResult);

            var liveHit = new CleanupLiveRuleHit(
                RuleId: rule.RuleId,
                DisplayName: rule.DisplayName,
                Category: rule.Category,
                Tier: rule.Tier,
                TotalBytes: ruleResult.TotalBytes,
                MatchedCount: ruleResult.MatchedCount,
                SkippedRecentOrLockedCount: ruleResult.SkippedRecentOrLockedCount,
                IsAvailable: ruleResult.IsAvailable,
                HasBlockingProcesses: ruleResult.HasBlockingProcesses,
                Drives: ruleResult.GetAffectedDrives());

            detailedProgress?.Report(new CleanupScanProgress(
                stepIndex,
                totalRules,
                rule.DisplayName,
                $"已完成 ({stepIndex}/{totalRules})：{rule.DisplayName}",
                CompletedRuleHit: liveHit));
        }

        return BuildScanSnapshot(results.ToArray(), scannedAt, MigrationSafetyPolicy.CurrentPolicyVersion, BuiltInCleanupRules.CurrentRuleVersion);
    }

    /// <summary>
    /// 清理完成后定向局部复扫指定规则，并将新结果原地合并到现有快照（毫秒~秒级，避免全盘 80 条规则重扫）。
    /// </summary>
    public async Task<CleanupScanSnapshot> RescanRulesAndMergeSnapshotAsync(
        CleanupScanSnapshot existingSnapshot,
        IReadOnlyList<string> ruleIdsToRescan,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(existingSnapshot);
        ArgumentNullException.ThrowIfNull(ruleIdsToRescan);

        var targetIdSet = new HashSet<string>(ruleIdsToRescan, StringComparer.OrdinalIgnoreCase);
        if (targetIdSet.Count == 0)
        {
            return existingSnapshot;
        }

        DateTimeOffset scannedAt = DateTimeOffset.UtcNow;
        var (protectedTxPaths, completedMigrations) = CleanupExclusionPolicy.LoadBuiltInRuleTransactionContext(_transactionStateDirectory);
        var rootCache = new CleanupRootResolutionCache();
        HashSet<string>? runningProcesses = _scanner.CaptureRunningProcessSnapshot();

        var updatedRules = new CleanupRuleScanResult[existingSnapshot.Rules.Length];
        int totalTargets = existingSnapshot.Rules.Count(r => targetIdSet.Contains(r.Rule.RuleId));
        int doneTargets = 0;

        for (int i = 0; i < existingSnapshot.Rules.Length; i++)
        {
            CleanupRuleScanResult existing = existingSnapshot.Rules[i];
            if (!targetIdSet.Contains(existing.Rule.RuleId))
            {
                updatedRules[i] = existing;
                continue;
            }

            ct.ThrowIfCancellationRequested();
            doneTargets++;
            int stepIndex = doneTargets;
            CleanupRuleDefinition rule = existing.Rule;

            updatedRules[i] = await Task.Run(() =>
            {
                try
                {
                    return ScanSingleRule(
                        rule, protectedTxPaths, completedMigrations, scannedAt, stepIndex, Math.Max(1, totalTargets),
                        rootCache, runningProcesses, progress: null, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return CleanupRuleScanner.EmptyRuleResult(rule, [], scannedAt, $"清理后局部复核受限：{ex.Message}");
                }
            }, ct).ConfigureAwait(false);
        }

        return BuildScanSnapshot(updatedRules, scannedAt, existingSnapshot.PolicyVersion, existingSnapshot.RuleVersion);
    }

    /// <summary>
    /// 扫描前极速（零文件遍历 + 纯内存前缀剪枝）探测已定位到的非系统盘缓存源（环境变量、Steam/IDE/Maven/npm 跨盘配置及本工具已登记的完成迁移目录）。
    /// </summary>
    public IReadOnlyList<CrossDriveDiscoveryItem> DiscoverCrossDriveTargets(
        IReadOnlyList<CleanupRuleDefinition>? rules = null,
        CancellationToken ct = default)
    {
        IReadOnlyList<CleanupRuleDefinition> activeRules = rules ?? BuiltInCleanupRules.All;
        var (protectedTxPaths, completedMigrations) = CleanupExclusionPolicy.LoadBuiltInRuleTransactionContext(_transactionStateDirectory);
        var rootCache = new CleanupRootResolutionCache();
        string sysDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\")[..2];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<CrossDriveDiscoveryItem>();

        foreach (CleanupRuleDefinition rule in activeRules)
        {
            ct.ThrowIfCancellationRequested();
            if (rule.TargetSpecs.Length == 0) continue;

            foreach (CleanupPathRuleSpec spec in rule.TargetSpecs)
            {
                bool isExplicitCrossSource = spec.RootKind is CleanupKnownRootKind.EnvironmentVariable
                    or CleanupKnownRootKind.SteamLibraries
                    or CleanupKnownRootKind.JetBrainsCustomSystem
                    or CleanupKnownRootKind.NpmConfigCache
                    or CleanupKnownRootKind.MavenCustomRepository;

                if (!isExplicitCrossSource)
                {
                    if (completedMigrations.Count == 0
                        || !_rootResolver.CanSpecIntersectCompletedMigrations(spec, completedMigrations, rootCache))
                    {
                        continue;
                    }
                }

                IReadOnlyList<string> dirs;
                try
                {
                    dirs = _rootResolver.ResolveRuleTargetDirectories(spec, protectedTxPaths, completedMigrations, rootCache);
                }
                catch
                {
                    continue;
                }

                foreach (string dir in dirs)
                {
                    if (dir.Length < 2 || dir[1] != ':') continue;
                    string driveLabel = char.ToUpperInvariant(dir[0]) + ":";
                    if (driveLabel.Equals(sysDrive, StringComparison.OrdinalIgnoreCase)) continue;

                    string ext = QuarantineStore.ToExtendedPath(dir);
                    if (!Directory.Exists(ext)) continue;

                    string key = $"{rule.RuleId}|{dir}";
                    if (!seen.Add(key)) continue;

                    string sourceKind = spec.RootKind switch
                    {
                        CleanupKnownRootKind.EnvironmentVariable => $"环境变量 {spec.CustomRootOverride}",
                        CleanupKnownRootKind.SteamLibraries => "Steam 跨盘库",
                        CleanupKnownRootKind.JetBrainsCustomSystem => "JetBrains 自定义路径",
                        CleanupKnownRootKind.NpmConfigCache => ".npmrc 自定义缓存",
                        CleanupKnownRootKind.MavenCustomRepository => "Maven settings.xml",
                        _ => "已登记符号链接迁移"
                    };

                    items.Add(new CrossDriveDiscoveryItem(driveLabel, sourceKind, rule.RuleId, rule.DisplayName, dir));
                }
            }
        }

        return items;
    }

    /// <summary>
    /// 执行用户已明确勾选并二次确认的规则清理（自动获取全局单执行排他锁）。
    /// </summary>
    public async Task<CleanupExecutionResult> ExecuteRuleCleanupAsync(
        CleanupScanSnapshot snapshot,
        IReadOnlyList<string> selectedRuleIds,
        CleanupFileAction fileAction = CleanupFileAction.QuarantineSameVolume,
        string? cautionConfirmationPhrase = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        IProgress<CleanupExecutionProgress>? detailedProgress = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selectedRuleIds);

        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid execId = Guid.NewGuid();

        CleanupRuleScanResult[] selectedRules = snapshot.Rules
            .Where(r => selectedRuleIds.Contains(r.Rule.RuleId, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        if (selectedRules.Length == 0)
        {
            return new CleanupExecutionResult(
                execId, started, DateTimeOffset.UtcNow, false, [],
                0, 0, 0, 0, 0, "未选择任何清理项。");
        }

        CleanupRuleScanResult? phraseRequired = selectedRules.FirstOrDefault(r =>
            r.Rule.Tier == CleanupRecommendationTier.Caution
            && r.Rule.CautionReason is { RequiresPhraseConfirmation: true });
        if (phraseRequired is not null)
        {
            string expectedPhrase = phraseRequired.Rule.CautionReason!.ConfirmationPhrase;
            if (!string.Equals(cautionConfirmationPhrase?.Trim(), expectedPhrase, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"所选包含 3 档谨慎清理项「{phraseRequired.Rule.DisplayName}」，必须准确输入确认短语「{expectedPhrase}」后才可执行。");
            }
        }

        string[] affectedPaths = selectedRules
            .SelectMany(r => r.VerificationExpectations.Select(e => e.RootDirectoryPath))
            .Concat(selectedRules.SelectMany(r => r.PreviewItems.Take(5).Select(p => p.FullPath)))
            .Where(p => !string.IsNullOrWhiteSpace(p) && p.Length >= 3 && p[1] == ':')
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (affectedPaths.Length == 0)
        {
            string sysRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            affectedPaths = [sysRoot];
        }

        using var gateLease = await OperationExecutionGate.Shared
            .AcquireOrThrowAsync("GarbageCleanup", $"垃圾清理（{selectedRules.Length} 项）", affectedPaths, ct)
            .ConfigureAwait(false);

        return await Task.Run(async () =>
        {
            IReadOnlyList<string> protectedTxPaths = CleanupExclusionPolicy.LoadBuiltInRuleTransactionContext(_transactionStateDirectory).ProtectedPaths;
            var itemResults = new List<CleanupExecutionItemResult>(selectedRules.Length);
            bool cancelled = false;
            int totalRules = selectedRules.Length;

            for (int ruleIndex = 0; ruleIndex < totalRules; ruleIndex++)
            {
                CleanupRuleScanResult ruleScan = selectedRules[ruleIndex];
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                CleanupRuleDefinition rule = ruleScan.Rule;
                double basePct = ruleIndex * 100.0 / totalRules;
                string startMsg = $"正在执行 ({ruleIndex + 1}/{totalRules})：{rule.DisplayName}…";
                progress?.Report(startMsg);
                detailedProgress?.Report(new CleanupExecutionProgress(
                    CompletedRules: ruleIndex,
                    TotalRules: totalRules,
                    CurrentRuleName: rule.DisplayName,
                    PhaseText: "准备执行",
                    ProcessedFiles: 0,
                    TotalFiles: ruleScan.MatchedCount,
                    ProcessedBytes: 0,
                    OverallPercent: basePct,
                    IsIndeterminate: rule.Channel == CleanupExecutionChannel.OfficialSystemApi,
                    Message: startMsg));

                // 执行前实时阻断检查（不使用旧缓存，确保即时发现新启动的关联进程）
                string[] blocking = _scanner.DetectRunningBlockingProcesses(rule.RequiredStoppedProcesses);
                if (blocking.Length > 0)
                {
                    itemResults.Add(new CleanupExecutionItemResult(
                        rule.RuleId,
                        rule.DisplayName,
                        rule.Channel,
                        CleanupItemExecutionStatus.Blocked,
                        0, 0, ruleScan.MatchedCount, 0, null,
                        $"关联进程正在运行（{string.Join("、", blocking)}），已安全阻止清理（工具不会强杀进程）。",
                        [$"请先关闭：{string.Join("、", blocking)}"]));
                    continue;
                }

                try
                {
                    CleanupExecutionItemResult res = rule.Channel == CleanupExecutionChannel.FileQuarantine
                        ? await _channelAExecutor.ExecuteChannelARuleAsync(ruleScan, fileAction, protectedTxPaths, progress, detailedProgress, ruleIndex, totalRules, ct).ConfigureAwait(false)
                        : await _officialHandlers.ExecuteChannelBRuleAsync(ruleScan, progress, detailedProgress, ruleIndex, totalRules, ct).ConfigureAwait(false);

                    itemResults.Add(res);

                    detailedProgress?.Report(new CleanupExecutionProgress(
                        CompletedRules: ruleIndex + 1,
                        TotalRules: totalRules,
                        CurrentRuleName: rule.DisplayName,
                        PhaseText: "已完成",
                        ProcessedFiles: res.ProcessedCount,
                        TotalFiles: Math.Max(res.ProcessedCount, ruleScan.MatchedCount),
                        ProcessedBytes: res.ProcessedBytes,
                        OverallPercent: (ruleIndex + 1) * 100.0 / totalRules,
                        IsIndeterminate: false,
                        Message: $"已完成 ({ruleIndex + 1}/{totalRules})：{rule.DisplayName}"));
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    itemResults.Add(new CleanupExecutionItemResult(
                        rule.RuleId,
                        rule.DisplayName,
                        rule.Channel,
                        CleanupItemExecutionStatus.Skipped,
                        0, 0, ruleScan.MatchedCount, 0, null,
                        "用户已取消，停止后续清理项。",
                        []));
                    break;
                }
                catch (Exception ex)
                {
                    itemResults.Add(new CleanupExecutionItemResult(
                        rule.RuleId,
                        rule.DisplayName,
                        rule.Channel,
                        CleanupItemExecutionStatus.Failed,
                        0, 0, 0, 1, null,
                        $"清理失败：{ex.Message}",
                        [ex.Message]));
                }
            }

            long quarantinedBytes = itemResults.Where(i => i.Status == CleanupItemExecutionStatus.Quarantined).Sum(i => i.ProcessedBytes);
            long recycledBytes = itemResults.Where(i => i.Status == CleanupItemExecutionStatus.Recycled).Sum(i => i.ProcessedBytes);
            long officialBytes = itemResults.Where(i => i.Status == CleanupItemExecutionStatus.ReclaimedByOfficialApi).Sum(i => i.ProcessedBytes);
            int skippedCount = itemResults.Sum(i => i.SkippedCount);
            int failedCount = itemResults.Sum(i => i.FailedCount);

            string summary = BuildExecutionSummary(cancelled, quarantinedBytes, recycledBytes, officialBytes, skippedCount, failedCount);
            return new CleanupExecutionResult(
                execId, started, DateTimeOffset.UtcNow, cancelled,
                itemResults.ToArray(), quarantinedBytes, recycledBytes, officialBytes, skippedCount, failedCount, summary);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 执行规则库外「AI 疑似残留」目录的受限同卷隔离（物理锁定通道 A 同卷隔离，绝不允许通道 B 或永久删除）。
    /// </summary>
    public async Task<CleanupExecutionResult> ExecuteAiSuspectedResidualQuarantineAsync(
        IReadOnlyList<AiCleanupCandidateRecord> candidates,
        bool userVerifiedContent,
        bool userAcknowledgedDisclaimer,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (!userVerifiedContent || !userAcknowledgedDisclaimer)
        {
            throw new InvalidOperationException("必须同时勾选「已自行核实目录内容」与「知晓 AI 建议免责须知」两项确认后，方可移入同卷隔离区。");
        }

        DateTimeOffset started = DateTimeOffset.UtcNow;
        Guid execId = Guid.NewGuid();

        AiCleanupCandidateRecord[] eligible = candidates
            .Where(c => c.Status == AiCleanupCandidateStatus.PendingManualReview)
            .ToArray();

        if (eligible.Length == 0)
        {
            return new CleanupExecutionResult(
                execId, started, DateTimeOffset.UtcNow, false, [],
                0, 0, 0, 0, 0, "没有可隔离的 AI 疑似残留候选项。");
        }

        string[] activePaths = eligible.Select(e => e.ActualLocalPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var gateLease = await OperationExecutionGate.Shared
            .AcquireOrThrowAsync("AiResidualQuarantine", $"AI 疑似残留同卷隔离（{eligible.Length} 项）", activePaths, ct)
            .ConfigureAwait(false);

        return await Task.Run(async () =>
        {
            IReadOnlyList<string> protectedTxPaths = CleanupExclusionPolicy.LoadProtectedTransactionPaths(_transactionStateDirectory);
            var (items, cancelled) = await _channelAExecutor
                .ExecuteAiResidualItemsAsync(eligible, protectedTxPaths, progress, ct)
                .ConfigureAwait(false);

            long quarantinedBytes = items.Where(i => i.Status == CleanupItemExecutionStatus.Quarantined).Sum(i => i.ProcessedBytes);
            int skipped = items.Sum(i => i.SkippedCount);
            int failed = items.Sum(i => i.FailedCount);

            string summary = quarantinedBytes > 0
                ? $"已将 {items.Count(i => i.Status == CleanupItemExecutionStatus.Quarantined)} 项 AI 疑似残留移入同卷隔离区（共 {FileStatsService.FormatBytes(quarantinedBytes)}，尚未释放物理空间；确认无误后请在隔离区人工二次确认释放）。"
                : "未能移入任何 AI 疑似残留目录，原目录均保持不动。";

            return new CleanupExecutionResult(
                execId, started, DateTimeOffset.UtcNow, cancelled,
                items, quarantinedBytes, 0, 0, skipped, failed, summary);
        }, ct).ConfigureAwait(false);
    }

    // ---- 单元测试内部兼容委托 ----
    internal static IReadOnlyList<string> ExpandSafeRelativeDirectories(string root, string relativeSubPath) =>
        CleanupRootPathResolver.ExpandSafeRelativeDirectories(root, relativeSubPath);

    internal static List<SystemToolRunner.ParsedDriverPackage> ParsePnpUtilDrivers(string output) =>
        SystemToolRunner.ParsePnpUtilDrivers(output);

    private CleanupRuleScanResult ScanSingleRule(
        CleanupRuleDefinition rule,
        IReadOnlyList<string> protectedTxPaths,
        IReadOnlyList<(string SourcePath, string TargetPath)> completedMigrations,
        DateTimeOffset scannedAt,
        int stepIndex,
        int totalRules,
        CleanupRootResolutionCache rootCache,
        IReadOnlySet<string>? runningProcesses,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        string[] blockingProcesses = _scanner.DetectRunningBlockingProcesses(rule.RequiredStoppedProcesses, runningProcesses);

        return rule.OfficialApiKind == CleanupOfficialApiKind.None
            ? _scanner.ScanChannelAFiles(rule, protectedTxPaths, completedMigrations, blockingProcesses, scannedAt, stepIndex, totalRules, rootCache, progress, ct)
            : _officialHandlers.ScanOfficialRule(rule, protectedTxPaths, blockingProcesses, scannedAt, stepIndex, totalRules, rootCache, progress, ct);
    }

    private static CleanupScanSnapshot BuildScanSnapshot(
        CleanupRuleScanResult[] rules,
        DateTimeOffset scannedAt,
        string policyVersion,
        string ruleVersion)
    {
        long recommendedBytes = rules.Where(r => r.Rule.Tier == CleanupRecommendationTier.Recommended).Sum(r => r.TotalBytes);
        long onDemandBytes = rules.Where(r => r.Rule.Tier == CleanupRecommendationTier.OnDemand).Sum(r => r.TotalBytes);
        long cautionBytes = rules.Where(r => r.Rule.Tier == CleanupRecommendationTier.Caution).Sum(r => r.TotalBytes);

        return new CleanupScanSnapshot(
            CleanupScanId: Guid.NewGuid(),
            ScannedAtUtc: scannedAt,
            PolicyVersion: policyVersion,
            RuleVersion: ruleVersion,
            Rules: rules,
            TotalReclaimableBytes: recommendedBytes + onDemandBytes + cautionBytes,
            RecommendedBytes: recommendedBytes,
            OnDemandBytes: onDemandBytes,
            CautionBytes: cautionBytes);
    }

    private static string BuildExecutionSummary(
        bool cancelled,
        long quarantinedBytes,
        long recycledBytes,
        long officialBytes,
        int skippedCount,
        int failedCount)
    {
        var parts = new List<string>();
        if (quarantinedBytes > 0)
            parts.Add($"已同卷隔离 {FileStatsService.FormatBytes(quarantinedBytes)}（尚未释放物理空间，可随时恢复）");
        if (recycledBytes > 0)
            parts.Add($"已移入回收站 {FileStatsService.FormatBytes(recycledBytes)}");
        if (officialBytes > 0)
            parts.Add($"官方接口已回收 {FileStatsService.FormatBytes(officialBytes)}");
        if (skippedCount > 0)
            parts.Add($"安全跳过 {skippedCount} 项");
        if (failedCount > 0)
            parts.Add($"失败/保留 {failedCount} 项");

        string prefix = cancelled ? "清理已由用户取消：" : "清理已完成：";
        return parts.Count == 0 ? prefix + "本次未修改任何文件。" : prefix + string.Join("；", parts) + "。";
    }
}
