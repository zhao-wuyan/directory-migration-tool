using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MigrationCore.Models;
using MigrationCore.Models.Safety;
using MigrationCore.Models.Scanning;
using MigrationCore.Services.Safety;

namespace MigrationCore.Services;

/// <summary>
/// 批量迁移计划冻结与预检服务：负责目标路径防碰撞生成、去重、父子重叠检测、
/// 统一 4 级安全策略核验、目标卷保守容量核算、PlanRevision/PlanDigest 冻结及只读进程预检（绝不杀进程或代停服务）。
/// </summary>
public sealed class MigrationPlanService
{
    private const long ConservativeOverheadBytes = 16L * 1024 * 1024; // 16 MiB 固定保守余量
    private static long _revisionCounter;

    private readonly PathIdentityService _identity = new();
    private readonly ISafetyApplicationMatcher _matcher;
    private readonly SafetyPolicyEnvironment? _environment;

    public MigrationPlanService(ISafetyApplicationMatcher? matcher = null, SafetyPolicyEnvironment? environment = null)
    {
        _matcher = matcher ?? MigrationSafetyPolicy.DefaultApplicationMatcher ?? ApplicationCatalog.Shared;
        _environment = environment;
    }

    /// <summary>
    /// 基于统一目标根 + 应用 ID（或盘符+完整相对路径）生成无 basename 碰撞的目标路径。
    /// </summary>
    public static string BuildDefaultTargetPath(string sourcePath, string unifiedTargetRoot, string? applicationId = null)
    {
        string normSource = PathIdentityService.Normalize(sourcePath);
        string normRoot = PathIdentityService.Normalize(unifiedTargetRoot);

        string driveTag = char.ToUpperInvariant(normSource[0]) + "_Drive";
        string relativeFromDrive = normSource.Length > 3 ? normSource[3..] : string.Empty;

        string subPath = string.IsNullOrWhiteSpace(applicationId)
            ? Path.Combine(driveTag, relativeFromDrive)
            : Path.Combine(SanitizePathSegment(applicationId), driveTag, relativeFromDrive);

        return PathIdentityService.Normalize(Path.Combine(normRoot, subPath));
    }

    /// <summary>
    /// 按真实数据量、解压/稀疏膨胀保守系数与固定余量计算目标卷保守需求字节。
    /// </summary>
    public static long ComputeConservativeTargetBytes(long logicalBytes, long allocatedBytes, bool isCompressedOrSparse)
    {
        long baseBytes = Math.Max(0, Math.Max(logicalBytes, allocatedBytes));
        double multiplier = isCompressedOrSparse ? 1.25 : 1.10;
        long scaled = (long)Math.Ceiling(baseBytes * multiplier);
        return scaled > long.MaxValue - ConservativeOverheadBytes
            ? long.MaxValue
            : scaled + ConservativeOverheadBytes;
    }

    /// <summary>
    /// 校验并冻结批量迁移计划。任一源路径、目标路径、核对模式或顺序变更都会产生新的 PlanRevision 与 PlanDigest。
    /// </summary>
    public FrozenMigrationPlan FreezeAndValidatePlan(
        IReadOnlyList<MigrationPlanDraftItem> draftItems,
        VerificationMode verificationMode,
        string? unifiedTargetRoot = null,
        bool checkFilesystem = true,
        Func<string, long>? volumeAvailableBytesProvider = null,
        IReadOnlyList<MigrationTransactionRecord>? existingTransactions = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var globalBlocks = new List<string>();
        var items = new List<MigrationPlanItem>(draftItems.Count);
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSourceIdentities = new HashSet<DirectoryIdentity>();
        var targetPathUsage = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var requiredByVolume = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        if (draftItems.Count == 0)
        {
            globalBlocks.Add("迁移清单为空，请至少添加一个允许迁移的目录。");
        }

        string? normalizedUnifiedRoot = null;
        if (!string.IsNullOrWhiteSpace(unifiedTargetRoot))
        {
            try { normalizedUnifiedRoot = PathIdentityService.Normalize(unifiedTargetRoot); }
            catch (Exception ex) { globalBlocks.Add($"统一目标根目录无效：{ex.Message}"); }
        }

        foreach (MigrationPlanDraftItem draft in draftItems)
        {
            string sourcePath;
            try
            {
                sourcePath = PathIdentityService.Normalize(draft.SourcePath);
            }
            catch (Exception ex)
            {
                globalBlocks.Add($"无效源路径 \"{draft.SourcePath}\"：{ex.Message}");
                continue;
            }

            // 1. 按物理路径去重
            if (!seenSources.Add(sourcePath))
            {
                continue;
            }

            // 2. 统一 4 级安全策略评估
            SafetyPolicyDecision sourceDecision = MigrationSafetyPolicy.EvaluateSource(
                sourcePath,
                _matcher,
                SafetyEvaluationContext.NewMigration,
                checkFilesystem,
                _environment);

            string? resolvedTarget = draft.TargetPath;
            if (string.IsNullOrWhiteSpace(resolvedTarget) && normalizedUnifiedRoot is not null)
            {
                resolvedTarget = BuildDefaultTargetPath(sourcePath, normalizedUnifiedRoot, sourceDecision.MatchedApplicationId);
            }

            string targetPath = string.Empty;
            string? blockingReason = null;

            if (string.IsNullOrWhiteSpace(resolvedTarget))
            {
                blockingReason = "未设置目标路径。";
            }
            else
            {
                try { targetPath = PathIdentityService.Normalize(resolvedTarget); }
                catch (Exception ex) { blockingReason = $"目标路径无效：{ex.Message}"; }
            }

            DirectoryIdentity? sourceId = null;
            if (checkFilesystem && blockingReason is null && !sourceDecision.IsHardDenied)
            {
                try
                {
                    sourceId = _identity.ReadDirectory(sourcePath, followLink: false);
                    if (sourceId.IsReparsePoint)
                    {
                        blockingReason = "源目录本身是符号链接或重解析点，禁止作为新迁移源。";
                    }
                    else if (!seenSourceIdentities.Add(sourceId))
                    {
                        // 不同路径指向同一物理文件 ID，直接去重跳过
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    blockingReason = $"无法核实源目录身份：{ex.Message}";
                }
            }

            // 3. 策略等级准入校验
            if (blockingReason is null)
            {
                if (sourceDecision.IsHardDenied)
                {
                    blockingReason = $"[L0 禁止] {sourceDecision.Summary}";
                }
                else if (sourceDecision.Level == MigrationAdmissionLevel.VendorNativeRecommended)
                {
                    blockingReason = $"[L2 原生引导] {sourceDecision.Summary}";
                }
                else if (sourceDecision.Level == MigrationAdmissionLevel.ManualRiskConfirm && !draft.L3RiskAcknowledged)
                {
                    blockingReason = "[L3 需人工确认] 未勾选确认普通个人数据/未收录目录迁移风险。";
                }
            }

            // 4. 扫描子树外部硬链接 / 不完整数据拦截（纯子树内部硬链接支持在目标盘无损重建，不阻断迁移）
            if (blockingReason is null && draft.HasSubtreeHardLinks)
            {
                blockingReason = "目录包含指向目录外部的硬链接文件；跨卷迁移会破坏共享语义且无法释放源盘对应空间，已禁止迁移。";
            }
            if (blockingReason is null && draft.HasIncompleteScanData)
            {
                blockingReason = "该子树在扫描时存在无法读取或损坏元数据，禁止直接加入执行队列。";
            }

            // 5. 目标路径、空目录与双向关系校验
            if (blockingReason is null)
            {
                var rel = PathValidator.ValidatePathRelation(sourcePath, targetPath);
                if (!rel.IsValid)
                {
                    blockingReason = rel.Error;
                }
                else
                {
                    SafetyPolicyDecision targetDecision = MigrationSafetyPolicy.EvaluateTarget(targetPath, checkFilesystem, _environment);
                    if (targetDecision.IsHardDenied)
                    {
                        blockingReason = $"目标路径被安全策略拒绝：{targetDecision.Summary}";
                    }
                    else if (checkFilesystem)
                    {
                        var emptyCheck = PathValidator.IsTargetDirectoryEmpty(targetPath);
                        if (!emptyCheck.IsEmpty)
                        {
                            blockingReason = emptyCheck.Error;
                        }
                    }
                }
            }

            // 6. 与既有活动事务重叠检查
            if (blockingReason is null && existingTransactions is not null)
            {
                foreach (MigrationTransactionRecord active in existingTransactions.Where(t => t.IsActive))
                {
                    if (PathsOverlap(sourcePath, active.SourcePath) || PathsOverlap(sourcePath, active.TargetPath)
                        || PathsOverlap(targetPath, active.SourcePath) || PathsOverlap(targetPath, active.TargetPath))
                    {
                        blockingReason = $"与活动事务 {active.TransactionId:D} 存在路径重叠。";
                        break;
                    }
                }
            }

            long requiredBytes = ComputeConservativeTargetBytes(draft.LogicalBytes, draft.AllocatedBytes, draft.IsCompressedOrSparse);
            if (!string.IsNullOrEmpty(targetPath))
            {
                if (!targetPathUsage.TryGetValue(targetPath, out var srcs))
                {
                    srcs = [];
                    targetPathUsage[targetPath] = srcs;
                }
                srcs.Add(sourcePath);

                // 仅就绪项计入目标卷容量需求，避免被阻断的超大项产生误导性空间不足警告
                if (blockingReason is null)
                {
                    string volRoot = targetPath[..3];
                    requiredByVolume[volRoot] = requiredByVolume.TryGetValue(volRoot, out long acc)
                        ? SafeAdd(acc, requiredBytes)
                        : requiredBytes;
                }
            }

            items.Add(new MigrationPlanItem(
                PlanItemId: Guid.NewGuid(),
                ScanId: draft.ScanId,
                NodeId: draft.NodeId,
                SourceIdentity: sourceId,
                ApplicationId: sourceDecision.MatchedApplicationId,
                ApplicationDisplayName: sourceDecision.MatchedApplicationName,
                SourcePath: sourcePath,
                TargetPath: targetPath,
                LogicalBytes: draft.LogicalBytes,
                AllocatedBytes: draft.AllocatedBytes,
                RequiredTargetBytes: requiredBytes,
                Source: draft.Source,
                PolicyDecision: sourceDecision,
                L3RiskAcknowledged: draft.L3RiskAcknowledged,
                ValidatedAtUtc: now,
                Status: blockingReason is null ? BatchPlanItemStatus.Ready : BatchPlanItemStatus.Blocked,
                BlockingReason: blockingReason,
                IsCompressedOrSparse: draft.IsCompressedOrSparse,
                ExpectedFileCount: draft.ExpectedFileCount,
                HasReparseDescendants: draft.HasReparseDescendants));
        }

        // 7. 跨项检查：双向父子重叠（源-源、源-目标、目标-目标）与多源撞同一目标碰撞
        for (int i = 0; i < items.Count; i++)
        {
            for (int j = i + 1; j < items.Count; j++)
            {
                var a = items[i];
                var b = items[j];
                if (PathsOverlap(a.SourcePath, b.SourcePath))
                {
                    string msg = $"清单存在父子目录重叠：{a.SourcePath} 与 {b.SourcePath}，请仅保留父目录或子目录之一。";
                    globalBlocks.Add(msg);
                    items[i] = MarkBlocked(items[i], msg);
                    items[j] = MarkBlocked(items[j], msg);
                }
                if (!string.IsNullOrEmpty(a.TargetPath) && !string.IsNullOrEmpty(b.TargetPath)
                    && PathsOverlap(a.TargetPath, b.TargetPath))
                {
                    string msg = $"多个源映射至重叠或相同目标路径：{a.TargetPath} 与 {b.TargetPath}。";
                    globalBlocks.Add(msg);
                    items[i] = MarkBlocked(items[i], msg);
                    items[j] = MarkBlocked(items[j], msg);
                }
                if (!string.IsNullOrEmpty(b.TargetPath) && PathsOverlap(a.SourcePath, b.TargetPath)
                    || !string.IsNullOrEmpty(a.TargetPath) && PathsOverlap(b.SourcePath, a.TargetPath))
                {
                    string msg = $"清单跨项存在源与目标互相包含：{a.SourcePath} ↔ {b.TargetPath}。";
                    globalBlocks.Add(msg);
                    items[i] = MarkBlocked(items[i], msg);
                    items[j] = MarkBlocked(items[j], msg);
                }
            }
        }

        // 8. 按目标卷聚合容量核对
        foreach (var (volRoot, reqBytes) in requiredByVolume)
        {
            long available = GetVolumeAvailableBytes(volRoot, volumeAvailableBytesProvider);
            if (available < reqBytes)
            {
                globalBlocks.Add($"目标卷 {volRoot} 可用空间不足：预计保守需求 {FileStatsService.FormatBytes(reqBytes)}，当前可用 {FileStatsService.FormatBytes(available)}。");
            }
        }

        long revision = Interlocked.Increment(ref _revisionCounter);
        MigrationPlanItem[] finalItems = items.ToArray();
        string digest = ComputePlanDigest(verificationMode, normalizedUnifiedRoot, finalItems);

        return new FrozenMigrationPlan(
            PlanRevision: revision,
            PlanDigest: digest,
            VerificationMode: verificationMode,
            UnifiedTargetRoot: normalizedUnifiedRoot,
            FrozenAtUtc: now,
            Items: finalItems,
            BlockingIssues: globalBlocks.Distinct(StringComparer.Ordinal).ToArray(),
            RequiredBytesByTargetVolume: requiredByVolume);
    }

    /// <summary>
    /// 批量启动前的全队列只读预检：校验计划摘要、目标卷容量与关联应用进程运行状态（只检测阻断，绝不强杀或代停）。
    /// </summary>
    public BatchPreflightReport RunReadOnlyProcessPreflight(
        FrozenMigrationPlan plan,
        IReadOnlyList<ApplicationRuleDefinition>? rules = null,
        Func<string[]>? runningProcessProvider = null,
        Func<string, long>? volumeAvailableBytesProvider = null)
    {
        var issues = new List<string>(plan.BlockingIssues);

        string recomputed = ComputePlanDigest(plan.VerificationMode, plan.UnifiedTargetRoot, plan.Items);
        if (!string.Equals(recomputed, plan.PlanDigest, StringComparison.Ordinal))
        {
            issues.Add("迁移计划内容摘要不匹配（计划已被修改或失效），请重新冻结计划。");
        }

        // 只读检查关联软件进程（用户需自行关闭，工具绝不强杀、绝不代停容器）
        var activeRules = rules ?? BuiltInApplicationRules.All;
        var requiredProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MigrationPlanItem item in plan.Items)
        {
            if (item.PolicyDecision.RequiredStoppedProcesses is { Length: > 0 } fromDecision)
            {
                foreach (string p in fromDecision) requiredProcesses.Add(p);
            }
            if (!string.IsNullOrWhiteSpace(item.ApplicationId))
            {
                var matchedRule = activeRules.FirstOrDefault(r => r.ApplicationId.Equals(item.ApplicationId, StringComparison.OrdinalIgnoreCase));
                if (matchedRule is not null)
                {
                    foreach (string p in matchedRule.RequiredStoppedProcessNames) requiredProcesses.Add(p);
                }
            }
        }

        var runningHit = new List<string>();
        if (requiredProcesses.Count > 0)
        {
            string[] runningNames = runningProcessProvider is not null
                ? runningProcessProvider()
                : GetLiveProcessNamesReadOnly();

            foreach (string proc in runningNames)
            {
                if (requiredProcesses.Contains(proc) && !runningHit.Contains(proc, StringComparer.OrdinalIgnoreCase))
                {
                    runningHit.Add(proc);
                }
            }

            if (runningHit.Count > 0)
            {
                issues.Add($"检测到关联应用仍在运行，请您先手动退出后再启动迁移（工具不会代停或强杀进程）：{string.Join("、", runningHit)}");
            }
        }

        // 目标卷实时可用空间复核
        var capChecks = new List<VolumeCapacityCheck>();
        foreach (var (volRoot, reqBytes) in plan.RequiredBytesByTargetVolume)
        {
            long avail = GetVolumeAvailableBytes(volRoot, volumeAvailableBytesProvider);
            bool ok = avail >= reqBytes;
            capChecks.Add(new VolumeCapacityCheck(volRoot, reqBytes, avail, ok));
            if (!ok)
            {
                issues.Add($"目标卷 {volRoot} 空间不足（需要 {FileStatsService.FormatBytes(reqBytes)}，可用 {FileStatsService.FormatBytes(avail)}）。");
            }
        }

        bool canStart = issues.Count == 0 && plan.Items.Length > 0 && plan.Items.All(i => i.Status == BatchPlanItemStatus.Ready);
        return new BatchPreflightReport(
            CanStart: canStart,
            PlanRevision: plan.PlanRevision,
            PlanDigest: plan.PlanDigest,
            BlockingIssues: issues.Distinct(StringComparer.Ordinal).ToArray(),
            RunningBlockingProcesses: runningHit.ToArray(),
            VolumeCapacityChecks: capChecks.ToArray());
    }

    /// <summary>
    /// 逐项执行前的实时重验（复用本计划服务注入的 matcher/environment，复核源身份、L0/L2/L3 策略、目标空、关联运行进程及目标卷实时剩余空间）。
    /// </summary>
    public string? ReverifyItemBeforeExecution(
        MigrationPlanItem item,
        IReadOnlyList<ApplicationRuleDefinition>? rules = null,
        Func<string[]>? runningProcessProvider = null,
        Func<string, long>? volumeAvailableBytesProvider = null)
    {
        SafetyPolicyDecision liveSource = MigrationSafetyPolicy.EvaluateSource(
            item.SourcePath,
            matcher: _matcher,
            context: SafetyEvaluationContext.NewMigration,
            checkFilesystem: true,
            environment: _environment);

        if (liveSource.IsHardDenied)
        {
            return $"源路径实时重验被 L0 拒绝：{liveSource.Summary}";
        }

        if (liveSource.Level == MigrationAdmissionLevel.VendorNativeRecommended)
        {
            return $"源路径归属 L2 原生引导，禁止符号链接批量迁移：{liveSource.Summary}";
        }

        if (liveSource.Level == MigrationAdmissionLevel.ManualRiskConfirm && !item.L3RiskAcknowledged)
        {
            return "源路径缺少 L3 人工风险确认。";
        }

        if (item.SourceIdentity is not null)
        {
            try
            {
                _identity.RequireSame(item.SourcePath, item.SourceIdentity, followLink: false);
            }
            catch (Exception ex)
            {
                return $"源目录身份较冻结计划时已变化，拒绝迁移同名新目录：{ex.Message}";
            }
        }

        SafetyPolicyDecision liveTarget = MigrationSafetyPolicy.EvaluateTarget(item.TargetPath, checkFilesystem: true, environment: _environment);
        if (liveTarget.IsHardDenied)
        {
            return $"目标路径实时重验被拒绝：{liveTarget.Summary}";
        }

        var emptyCheck = PathValidator.IsTargetDirectoryEmpty(item.TargetPath);
        if (!emptyCheck.IsEmpty)
        {
            return emptyCheck.Error;
        }

        // 实时复核当前项关联进程（防止用户在执行前序项期间重新打开了关联软件）
        var activeRules = rules ?? BuiltInApplicationRules.All;
        var reqProcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (liveSource.RequiredStoppedProcesses is { Length: > 0 } fromDecision)
        {
            foreach (string p in fromDecision) reqProcs.Add(p);
        }
        if (!string.IsNullOrWhiteSpace(item.ApplicationId))
        {
            var matchedRule = activeRules.FirstOrDefault(r => r.ApplicationId.Equals(item.ApplicationId, StringComparison.OrdinalIgnoreCase));
            if (matchedRule is not null)
            {
                foreach (string p in matchedRule.RequiredStoppedProcessNames) reqProcs.Add(p);
            }
        }
        if (reqProcs.Count > 0)
        {
            string[] liveProcs = runningProcessProvider is not null ? runningProcessProvider() : GetLiveProcessNamesReadOnly();
            string[] hit = liveProcs.Where(p => reqProcs.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (hit.Length > 0)
            {
                return $"执行前检测到关联进程正在运行，请先手动关闭：{string.Join("、", hit)}";
            }
        }

        // 实时复核目标卷可用空间
        string volRoot = item.TargetPath[..3];
        long avail = GetVolumeAvailableBytes(volRoot, volumeAvailableBytesProvider);
        if (avail < item.RequiredTargetBytes)
        {
            return $"目标卷 {volRoot} 当前可用空间不足（需要 {FileStatsService.FormatBytes(item.RequiredTargetBytes)}，可用 {FileStatsService.FormatBytes(avail)}）。";
        }

        return null;
    }

    /// <summary>
    /// 按最新实时复扫得到的大小重新核验目标卷剩余空间。
    /// </summary>
    public string? CheckTargetVolumeHasSpace(
        string targetPath,
        long requiredBytes,
        Func<string, long>? volumeAvailableBytesProvider = null)
    {
        if (string.IsNullOrWhiteSpace(targetPath) || targetPath.Length < 3) return null;
        string volRoot = targetPath[..3];
        long avail = GetVolumeAvailableBytes(volRoot, volumeAvailableBytesProvider);
        if (avail < requiredBytes)
        {
            return $"按最新实时大小核算，目标卷 {volRoot} 可用空间不足（需要 {FileStatsService.FormatBytes(requiredBytes)}，当前可用 {FileStatsService.FormatBytes(avail)}）。";
        }
        return null;
    }

    public static string ComputePlanDigest(VerificationMode mode, string? unifiedTargetRoot, IReadOnlyList<MigrationPlanItem> items)
    {
        var sb = new StringBuilder();
        sb.Append("mode=").Append(mode).Append("|root=").Append(unifiedTargetRoot ?? "").Append('\n');
        foreach (MigrationPlanItem item in items)
        {
            sb.Append(item.SourcePath.ToUpperInvariant())
              .Append("->").Append(item.TargetPath.ToUpperInvariant())
              .Append("|lvl=").Append((int)item.PolicyDecision.Level)
              .Append("|ack=").Append(item.L3RiskAcknowledged)
              .Append("|id=").Append(item.SourceIdentity?.VolumeGuid ?? "").Append(':').Append(item.SourceIdentity?.FileId ?? 0)
              .Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string[] GetLiveProcessNamesReadOnly()
    {
        var names = new List<string>();
        foreach (Process p in Process.GetProcesses())
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
                // 忽略无权查看的系统受保护进程
            }
            finally
            {
                p.Dispose();
            }
        }
        return names.ToArray();
    }

    private static long GetVolumeAvailableBytes(string volRoot, Func<string, long>? provider)
    {
        if (provider is not null)
        {
            return provider(volRoot);
        }

        try
        {
            var di = new DriveInfo(volRoot);
            return di.IsReady ? di.AvailableFreeSpace : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static MigrationPlanItem MarkBlocked(MigrationPlanItem item, string reason) => item with
    {
        Status = BatchPlanItemStatus.Blocked,
        BlockingReason = string.IsNullOrWhiteSpace(item.BlockingReason) ? reason : $"{item.BlockingReason}；{reason}"
    };

    private static bool PathsOverlap(string a, string b) =>
        PathIdentityService.IsWithin(a, b) || PathIdentityService.IsWithin(b, a);

    private static string SanitizePathSegment(string input)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (char c in input)
        {
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        return sb.ToString().Trim();
    }

    private static long SafeAdd(long a, long b) => a > long.MaxValue - b ? long.MaxValue : a + b;
}
