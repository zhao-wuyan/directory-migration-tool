using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using MigrationCore.Models.Ai;
using MigrationCore.Models.Safety;
using MigrationCore.Models.Scanning;
using MigrationCore.Services.Safety;

namespace MigrationCore.Services.Ai;

/// <summary>
/// 共享只读分析工具底座实现（二期内置 AI 助手与四期外部本地 MCP 复用）。
/// 安全红线：
/// 1. 仅依赖授权内存 ScanSnapshot、ApplicationCatalog、MigrationSafetyPolicy 与 AiProposalInbox；
/// 2. 物理上不引用 MigrationService、MigrationBatchService、BackupManagementService 或任意写/删服务；
/// 3. 绝不读取任何磁盘文件内容；所有路径均经 AiPathSanitizer 脱敏；
/// 4. submit_migration_proposal 强制策略复核、幂等校验、父子重叠拦截与免责声明。
/// </summary>
public sealed class ReadOnlyAiAnalysisToolset : IAiAnalysisToolset
{
    public const string ProtocolVersion = "2026.11-phase2-readonly";
    public const string MandatoryDisclaimer =
        "AI 建议仅供参考，不代表工具官方安全背书；用户须自行判断目录归属与业务影响，工具不对 AI 误判导致的后果负责。";

    public const int MaxPageSize = 50;
    public const int MaxProposalItems = 20;
    public const int MaxReasonLength = 400;

    public static readonly string[] Phase2ToolNames =
    [
        "get_capabilities",
        "get_system_environment",
        "list_scans",
        "get_scan_summary",
        "list_directory_children",
        "list_migration_candidates",
        "get_candidate_details",
        "inspect_unknown_directory_metadata",
        "get_migration_policy",
        "submit_migration_proposal",
        "get_proposal_status"
    ];

    private readonly object _sync = new();
    private readonly Dictionary<Guid, ScanSnapshot> _authorizedScans = new();
    private readonly List<Guid> _scanOrder = new();
    private readonly ApplicationCatalog _catalog;
    private readonly SafetyPolicyEnvironment? _policyEnvironment;
    private readonly Func<IReadOnlyList<AiTargetCandidateDto>> _targetCandidatesProvider;
    private readonly Func<AiSystemEnvironmentResponse>? _systemEnvironmentProvider;

    public AiProposalInbox Inbox { get; }

    public event EventHandler? AuthorizedScansChanged;

    public ReadOnlyAiAnalysisToolset(
        ApplicationCatalog? catalog = null,
        SafetyPolicyEnvironment? policyEnvironment = null,
        AiProposalInbox? inbox = null,
        Func<IReadOnlyList<AiTargetCandidateDto>>? targetCandidatesProvider = null,
        Func<AiSystemEnvironmentResponse>? systemEnvironmentProvider = null)
    {
        _catalog = catalog ?? ApplicationCatalog.Shared;
        _policyEnvironment = policyEnvironment;
        Inbox = inbox ?? new AiProposalInbox();
        _targetCandidatesProvider = targetCandidatesProvider ?? DefaultDiscoverTargetCandidates;
        _systemEnvironmentProvider = systemEnvironmentProvider;
    }

    public ScanSnapshot? GetLatestAuthorizedSnapshot()
    {
        lock (_sync)
        {
            if (_scanOrder.Count == 0) return null;
            Guid id = _scanOrder[^1];
            return _authorizedScans.TryGetValue(id, out ScanSnapshot? snap) ? snap : null;
        }
    }

    public bool TryGetAuthorizedSnapshot(Guid scanId, out ScanSnapshot? snapshot)
    {
        lock (_sync)
        {
            return _authorizedScans.TryGetValue(scanId, out snapshot);
        }
    }

    /// <summary>
    /// 将当前会话的扫描快照授权给只读 AI 底座分析（同时作废已被替代的旧同卷快照建议）。
    /// </summary>
    public void AuthorizeSnapshot(ScanSnapshot snapshot, bool replacePreviousSnapshots = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_sync)
        {
            if (replacePreviousSnapshots)
            {
                foreach (Guid oldId in _authorizedScans.Keys.ToArray())
                {
                    if (oldId != snapshot.ScanId)
                    {
                        _authorizedScans.Remove(oldId);
                        _scanOrder.Remove(oldId);
                    }
                }
            }

            _authorizedScans[snapshot.ScanId] = snapshot;
            _scanOrder.Remove(snapshot.ScanId);
            _scanOrder.Add(snapshot.ScanId);
        }

        if (replacePreviousSnapshots)
        {
            Inbox.InvalidateAllExceptScan(snapshot.ScanId, "扫描快照已更新，旧快照对应的 AI 建议已自动失效。");
        }

        AuthorizedScansChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 撤销指定或全部扫描快照的 AI 共享授权，并立即作废关联建议。
    /// </summary>
    public void RevokeSnapshot(Guid? scanId = null)
    {
        Guid[] revokedIds;
        lock (_sync)
        {
            if (scanId is Guid targetId)
            {
                revokedIds = _authorizedScans.Remove(targetId) ? [targetId] : [];
                _scanOrder.Remove(targetId);
            }
            else
            {
                revokedIds = _authorizedScans.Keys.ToArray();
                _authorizedScans.Clear();
                _scanOrder.Clear();
            }
        }

        foreach (Guid id in revokedIds)
        {
            Inbox.InvalidateForScan(id, "用户已撤销该扫描快照的 AI 授权，相关建议已失效。");
        }

        if (revokedIds.Length > 0)
        {
            AuthorizedScansChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public AiCapabilitiesResponse GetCapabilities()
    {
        bool hasScans;
        lock (_sync) { hasScans = _authorizedScans.Count > 0; }

        return new AiCapabilitiesResponse(
            ProtocolVersion: ProtocolVersion,
            PolicyVersion: MigrationSafetyPolicy.CurrentPolicyVersion,
            ReadOnlyAnalysisOnly: true,
            CanExecuteOperations: false,
            AuthorizedScopes: hasScans
                ? ["system_environment_readonly", "scan_metadata_readonly", "migration_policy_readonly", "proposal_inbox_submit"]
                : ["system_environment_readonly", "migration_policy_readonly"],
            AvailableTools: Phase2ToolNames,
            Disclaimer: MandatoryDisclaimer);
    }

    public AiSystemEnvironmentResponse GetSystemEnvironment()
    {
        if (_systemEnvironmentProvider is not null)
        {
            return _systemEnvironmentProvider();
        }

        int authorizedCount;
        lock (_sync) { authorizedCount = _authorizedScans.Count; }

        return CollectSystemEnvironment(authorizedCount);
    }

    public AiScanListResponse ListScans(string? cursor = null, int pageSize = 20)
    {
        int clampedPageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        int offset = ParseCursor(cursor);

        ScanSnapshot[] snapshots;
        lock (_sync)
        {
            snapshots = _scanOrder
                .AsEnumerable()
                .Reverse()
                .Select(id => _authorizedScans[id])
                .ToArray();
        }

        var page = snapshots
            .Skip(offset)
            .Take(clampedPageSize)
            .Select(BuildScanSummaryItem)
            .ToArray();

        string? nextCursor = offset + page.Length < snapshots.Length
            ? (offset + page.Length).ToString(CultureInfo.InvariantCulture)
            : null;

        return new AiScanListResponse(page, nextCursor, snapshots.Length);
    }

    public AiScanSummaryResponse GetScanSummary(Guid scanId)
    {
        ScanSnapshot snapshot = RequireAuthorizedSnapshot(scanId);
        int[] hotspots = CollectDisjointHotspots(snapshot, minAllocatedBytes: 50L * 1024 * 1024, maxCount: 80);
        if (hotspots.Length == 0)
        {
            hotspots = CollectDisjointHotspots(snapshot, minAllocatedBytes: 1L * 1024 * 1024, maxCount: 80);
        }

        int l1Count = 0;
        long l1Bytes = 0;
        int l2Count = 0;
        int l3Count = 0;

        foreach (int nodeId in hotspots)
        {
            string fullPath = snapshot.GetFullPath(nodeId);
            SafetyPolicyDecision decision = EvaluateSnapshotPath(fullPath);
            switch (decision.Level)
            {
                case MigrationAdmissionLevel.VerifiedEligible:
                    l1Count++;
                    l1Bytes += snapshot.Nodes[nodeId].UniqueAllocatedBytes;
                    break;
                case MigrationAdmissionLevel.VendorNativeRecommended:
                    l2Count++;
                    break;
                case MigrationAdmissionLevel.ManualRiskConfirm:
                    l3Count++;
                    break;
            }
        }

        AiTargetCandidateDto[] targets = GetTargetCandidatesForSourceDrive(snapshot.Volume.DriveRoot);
        AiDirectoryNodeDto[] topLevelDirs = ListDirectoryChildren(scanId, snapshot.RootNodeId, cursor: null, pageSize: 10).Children;

        return new AiScanSummaryResponse(
            Summary: BuildScanSummaryItem(snapshot),
            HotspotCount: hotspots.Length,
            VerifiedL1Count: l1Count,
            VerifiedL1Bytes: l1Bytes,
            NativeGuideL2Count: l2Count,
            ManualConfirmL3Count: l3Count,
            TargetCandidates: targets,
            Disclaimer: MandatoryDisclaimer,
            RootNodeId: snapshot.RootNodeId,
            TopLevelDirectories: topLevelDirs);
    }

    public AiDirectoryChildrenResponse ListDirectoryChildren(Guid scanId, int nodeId, string? cursor = null, int pageSize = 30, long minSizeMb = 0)
    {
        ScanSnapshot snapshot = RequireAuthorizedSnapshot(scanId);
        if ((uint)nodeId >= (uint)snapshot.Nodes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeId), $"无效的节点 ID：{nodeId}。");
        }

        ref readonly ScanNodeRecord parent = ref snapshot.Nodes[nodeId];
        if (!parent.IsDirectory)
        {
            throw new InvalidOperationException($"节点 {nodeId} 不是目录，无法列出子目录。");
        }

        string parentFullPath = snapshot.GetFullPath(nodeId);
        if (nodeId != snapshot.RootNodeId)
        {
            SafetyPolicyDecision parentDecision = EvaluateSnapshotPath(parentFullPath);
            if (parentDecision.IsHardDenied && !IsContainerToDescend(parentFullPath, parentDecision))
            {
                throw new UnauthorizedAccessException(
                    $"目录命中 L0 硬拒绝保护策略（{string.Join(", ", parentDecision.ReasonCodes)}），禁止向下钻入系统核心或高危目录内部。");
            }
        }

        int clampedPageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        int offset = ParseCursor(cursor);
        long minBytes = minSizeMb > 0
            ? Math.Min(minSizeMb, 1024L * 1024L) * 1024L * 1024L
            : 0L;

        // 仅返回目录级子节点（防止百万级单文件泄漏隐私与消耗 Token）
        int[] rawChildren = snapshot.GetChildren(nodeId);
        var dirChildren = new List<int>(rawChildren.Length);
        int filteredOutSmallCount = 0;
        for (int i = 0; i < rawChildren.Length; i++)
        {
            int cid = rawChildren[i];
            ref readonly ScanNodeRecord child = ref snapshot.Nodes[cid];
            if (child.IsDirectory && !child.IsOrphaned)
            {
                long childSize = Math.Max(child.UniqueAllocatedBytes, child.LogicalBytes);
                if (minBytes > 0 && childSize < minBytes)
                {
                    filteredOutSmallCount++;
                    continue;
                }
                dirChildren.Add(cid);
            }
        }

        dirChildren.Sort((a, b) =>
        {
            long sa = Math.Max(snapshot.Nodes[a].UniqueAllocatedBytes, snapshot.Nodes[a].LogicalBytes);
            long sb = Math.Max(snapshot.Nodes[b].UniqueAllocatedBytes, snapshot.Nodes[b].LogicalBytes);
            int cmp = sb.CompareTo(sa);
            return cmp != 0 ? cmp : a.CompareTo(b);
        });

        long parentTotal = Math.Max(1, Math.Max(parent.UniqueAllocatedBytes, parent.LogicalBytes));
        var slice = dirChildren.Skip(offset).Take(clampedPageSize).ToArray();
        var dtos = new AiDirectoryNodeDto[slice.Length];

        for (int i = 0; i < slice.Length; i++)
        {
            int cid = slice[i];
            ref readonly ScanNodeRecord child = ref snapshot.Nodes[cid];
            string fullPath = snapshot.GetFullPath(cid);
            SafetyPolicyDecision decision = EvaluateSnapshotPath(fullPath);
            DirectoryExplanation? exp = KnownDirectoryExplainer.Explain(fullPath, _catalog, decision);

            long childBytes = Math.Max(child.UniqueAllocatedBytes, child.LogicalBytes);
            double share = Math.Clamp(childBytes * 100.0 / parentTotal, 0, 100);
            DateTimeOffset? lastWrite = child.LastWriteUtcTicks > 0
                ? new DateTimeOffset(child.LastWriteUtcTicks, TimeSpan.Zero)
                : null;
            bool canDrillDown = HasChildDirectories(snapshot, cid)
                && !child.IsReparsePoint
                && (!decision.IsHardDenied || IsContainerToDescend(fullPath, decision));

            dtos[i] = new AiDirectoryNodeDto(
                NodeId: cid,
                ParentNodeId: child.ParentNodeId,
                Name: AiPathSanitizer.SanitizeUntrustedText(snapshot.GetNodeName(cid), 120, false),
                SanitizedPath: AiPathSanitizer.SanitizePath(fullPath),
                IsDirectory: true,
                LogicalBytes: child.LogicalBytes,
                AllocatedBytes: child.AllocatedBytes,
                UniqueAllocatedBytes: child.UniqueAllocatedBytes,
                FileCount: child.FileCount,
                DirectoryCount: child.DirectoryCount,
                ParentSharePercent: Math.Round(share, 1),
                AdmissionLevel: decision.Level,
                ReasonCodes: decision.ReasonCodes,
                MatchedApplicationId: decision.MatchedApplicationId,
                MatchedApplicationName: decision.MatchedApplicationName,
                DirectoryRoleTitle: exp?.RoleTitle,
                HasSpecialFsFlags: child.IsReparsePoint || child.IsCloudPlaceholder || child.IsWofCompressed || child.IsSparseOrCompressed || child.IsHardLinkSecondary,
                LastWriteUtc: lastWrite,
                CanDrillDown: canDrillDown);
        }

        string? nextCursor = offset + dtos.Length < dirChildren.Count
            ? (offset + dtos.Length).ToString(CultureInfo.InvariantCulture)
            : null;

        return new AiDirectoryChildrenResponse(
            ScanId: scanId,
            ParentNodeId: nodeId,
            ParentSanitizedPath: AiPathSanitizer.SanitizePath(parentFullPath),
            Children: dtos,
            NextCursor: nextCursor,
            TotalDirectoryCount: dirChildren.Count,
            TruncatedByPageLimit: nextCursor is not null,
            FilteredOutSmallCount: filteredOutSmallCount);
    }

    public AiMigrationCandidatesResponse ListMigrationCandidates(Guid scanId, string? cursor = null, int pageSize = 30, int? parentNodeId = null)
    {
        ScanSnapshot snapshot = RequireAuthorizedSnapshot(scanId);
        int clampedPageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        int offset = ParseCursor(cursor);

        int startNodeId = snapshot.RootNodeId;
        if (parentNodeId is int customParent)
        {
            if ((uint)customParent >= (uint)snapshot.Nodes.Length || !snapshot.Nodes[customParent].IsDirectory)
            {
                throw new ArgumentOutOfRangeException(nameof(parentNodeId), $"无效的父目录节点 ID：{customParent}。");
            }

            if (customParent != snapshot.RootNodeId)
            {
                string parentPath = snapshot.GetFullPath(customParent);
                SafetyPolicyDecision parentDecision = EvaluateSnapshotPath(parentPath);
                if (parentDecision.IsHardDenied && !IsContainerToDescend(parentPath, parentDecision))
                {
                    throw new UnauthorizedAccessException($"目录命中 L0 硬拒绝保护策略，禁止查询其内部迁移候选。");
                }
            }

            startNodeId = customParent;
        }

        int[] hotspots = CollectDisjointHotspots(snapshot, minAllocatedBytes: 50L * 1024 * 1024, maxCount: 120, startNodeId);
        if (hotspots.Length == 0)
        {
            hotspots = CollectDisjointHotspots(snapshot, minAllocatedBytes: 1L * 1024 * 1024, maxCount: 120, startNodeId);
        }
        if (hotspots.Length == 0)
        {
            // 小型测试卷回退：按深度 1-3 层扫描全部非空目录
            hotspots = CollectDisjointHotspots(snapshot, minAllocatedBytes: 1, maxCount: 80, startNodeId);
        }

        AiTargetCandidateDto[] targets = GetTargetCandidatesForSourceDrive(snapshot.Volume.DriveRoot);
        AiTargetCandidateDto? bestTarget = targets.FirstOrDefault(t => t.IsRecommended) ?? targets.FirstOrDefault();

        var candidates = new List<AiMigrationCandidateDto>();
        foreach (int nodeId in hotspots)
        {
            ref readonly ScanNodeRecord node = ref snapshot.Nodes[nodeId];
            if (!node.IsDirectory || node.IsOrphaned) continue;

            string fullPath = snapshot.GetFullPath(nodeId);
            SafetyPolicyDecision decision = EvaluateSnapshotPath(fullPath);

            // L0 硬拒绝绝不作为迁移候选返回
            if (decision.IsHardDenied) continue;

            SubtreeHazardReport hazards = snapshot.AnalyzeSubtreeHazards(nodeId, maxExternalSamples: 0);
            bool isCompressed = node.IsSparseOrCompressed || node.IsWofCompressed;
            long conservativeTarget = MigrationPlanService.ComputeConservativeTargetBytes(
                hazards.HasInternalOnlyHardLinks ? node.UniqueAllocatedBytes : node.LogicalBytes,
                node.UniqueAllocatedBytes,
                isCompressed);

            DirectoryExplanation? exp = KnownDirectoryExplainer.Explain(fullPath, _catalog, decision);
            string displayName = BuildDisplayName(snapshot, nodeId, decision.MatchedApplicationName);

            string? suggestedTargetDisplay = null;
            if (bestTarget is not null && decision.Level is MigrationAdmissionLevel.VerifiedEligible or MigrationAdmissionLevel.ManualRiskConfirm)
            {
                try
                {
                    string rawTarget = MigrationPlanService.BuildDefaultTargetPath(
                        fullPath, bestTarget.SuggestedRoot, decision.MatchedApplicationId);
                    suggestedTargetDisplay = AiPathSanitizer.SanitizePath(rawTarget);
                }
                catch { }
            }

            candidates.Add(new AiMigrationCandidateDto(
                NodeId: nodeId,
                DisplayName: displayName,
                SanitizedPath: AiPathSanitizer.SanitizePath(fullPath),
                LogicalBytes: node.LogicalBytes,
                AllocatedBytes: node.AllocatedBytes,
                UniqueAllocatedBytes: node.UniqueAllocatedBytes,
                ConservativeTargetBytes: conservativeTarget,
                AdmissionLevel: decision.Level,
                ReasonCodes: decision.ReasonCodes,
                MatchedApplicationId: decision.MatchedApplicationId,
                MatchedApplicationName: decision.MatchedApplicationName,
                DirectoryRoleTitle: exp?.RoleTitle,
                RecommendedTargetCandidateId: bestTarget?.TargetCandidateId,
                SuggestedTargetDisplay: suggestedTargetDisplay,
                RequiredStoppedProcesses: decision.RequiredStoppedProcesses ?? [],
                HasSubtreeHardLinks: hazards.HasHardLinks,
                HasIncompleteNodes: hazards.HasIncompleteNodes,
                Summary: decision.Summary,
                HardLinkKind: hazards.HardLinkKind,
                InternalHardLinkGroupCount: hazards.InternalHardLinkGroupCount,
                ExternalHardLinkFileCount: hazards.ExternalHardLinkFileCount));
        }

        // L1 推荐迁移优先，其次按占用降序
        candidates.Sort((a, b) =>
        {
            int levelRankA = a.AdmissionLevel == MigrationAdmissionLevel.VerifiedEligible ? 0
                : a.AdmissionLevel == MigrationAdmissionLevel.ManualRiskConfirm ? 1 : 2;
            int levelRankB = b.AdmissionLevel == MigrationAdmissionLevel.VerifiedEligible ? 0
                : b.AdmissionLevel == MigrationAdmissionLevel.ManualRiskConfirm ? 1 : 2;
            int cmp = levelRankA.CompareTo(levelRankB);
            if (cmp != 0) return cmp;
            return Math.Max(b.UniqueAllocatedBytes, b.LogicalBytes)
                .CompareTo(Math.Max(a.UniqueAllocatedBytes, a.LogicalBytes));
        });

        AiMigrationCandidateDto[] page = candidates.Skip(offset).Take(clampedPageSize).ToArray();
        string? nextCursor = offset + page.Length < candidates.Count
            ? (offset + page.Length).ToString(CultureInfo.InvariantCulture)
            : null;

        return new AiMigrationCandidatesResponse(
            ScanId: scanId,
            PolicyVersion: MigrationSafetyPolicy.CurrentPolicyVersion,
            Candidates: page,
            NextCursor: nextCursor,
            TotalCandidateCount: candidates.Count,
            Disclaimer: MandatoryDisclaimer);
    }

    public AiCandidateDetailsResponse GetCandidateDetails(Guid scanId, int nodeId)
    {
        ScanSnapshot snapshot = RequireAuthorizedSnapshot(scanId);
        if ((uint)nodeId >= (uint)snapshot.Nodes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeId), $"无效的节点 ID：{nodeId}。");
        }

        ref readonly ScanNodeRecord node = ref snapshot.Nodes[nodeId];
        if (!node.IsDirectory)
        {
            throw new InvalidOperationException($"节点 {nodeId} 不是目录，本工具仅支持分析目录。");
        }

        string fullPath = snapshot.GetFullPath(nodeId);
        SafetyPolicyDecision decision = EvaluateSnapshotPath(fullPath);
        DirectoryExplanation? exp = KnownDirectoryExplainer.Explain(fullPath, _catalog, decision);
        SubtreeHazardReport hazards = snapshot.AnalyzeSubtreeHazards(nodeId, maxExternalSamples: 3);
        string[] sanitizedExternalPeers = hazards.SampleExternalPeerPaths
            .Select(p => AiPathSanitizer.SanitizePath(p))
            .ToArray();

        bool isCompressed = node.IsSparseOrCompressed || node.IsWofCompressed;
        long conservativeTarget = MigrationPlanService.ComputeConservativeTargetBytes(
            hazards.HasInternalOnlyHardLinks ? node.UniqueAllocatedBytes : node.LogicalBytes,
            node.UniqueAllocatedBytes,
            isCompressed);

        string hardLinkNote = hazards.HardLinkKind switch
        {
            SubtreeHardLinkKind.InternalOnly =>
                $" 检测到 {hazards.InternalHardLinkGroupCount} 组目录内闭环硬链接（节省约 {FileStatsService.FormatBytes(hazards.HardLinkSharedBytes)} 重复空间）；本工具支持迁移时在目标盘无损重建内部硬链接，零空间膨胀，允许正常迁移。",
            SubtreeHardLinkKind.ExternalLinked =>
                $" 检测到 {hazards.ExternalHardLinkFileCount} 个文件存在指向目录外部的硬链接" +
                (sanitizedExternalPeers.Length > 0 ? $"（外部关联示例：{string.Join("；", sanitizedExternalPeers)}）" : string.Empty) +
                "，跨盘迁移必然断开共享关系且源盘对应空间无法释放，已自动拦截。",
            SubtreeHardLinkKind.UnverifiedExternal =>
                " 检测到未能验证归属闭环的硬链接文件，按安全原则已自动拦截。",
            _ => string.Empty
        };

        string spaceNote = $"逻辑大小 {FileStatsService.FormatBytes(node.LogicalBytes)}，卷内去重实际占用 {FileStatsService.FormatBytes(node.UniqueAllocatedBytes)}，" +
            $"目标盘保守预估需求 {FileStatsService.FormatBytes(conservativeTarget)}。迁移后原目录将在源盘改名保留为备份，在用户人工验证并二次确认删除前不会释放源盘物理空间。{hardLinkNote}";

        return new AiCandidateDetailsResponse(
            ScanId: scanId,
            NodeId: nodeId,
            DisplayName: BuildDisplayName(snapshot, nodeId, decision.MatchedApplicationName),
            SanitizedPath: AiPathSanitizer.SanitizePath(fullPath),
            LogicalBytes: node.LogicalBytes,
            AllocatedBytes: node.AllocatedBytes,
            UniqueAllocatedBytes: node.UniqueAllocatedBytes,
            ConservativeTargetBytes: conservativeTarget,
            FileCount: node.FileCount,
            DirectoryCount: node.DirectoryCount,
            AdmissionLevel: decision.Level,
            IsAllowedForNewMigration: decision.IsAllowedForNewMigration && !hazards.HasBlockingHardLinks && !hazards.HasIncompleteNodes,
            ReasonCodes: decision.ReasonCodes,
            PolicySummary: decision.Summary,
            MatchedApplicationId: decision.MatchedApplicationId,
            MatchedApplicationName: decision.MatchedApplicationName,
            NativeGuidance: decision.NativeGuidance,
            Evidence: decision.Evidence ?? [],
            Warnings: decision.Warnings ?? [],
            RequiredStoppedProcesses: decision.RequiredStoppedProcesses ?? [],
            DirectoryRoleTitle: exp?.RoleTitle,
            DirectoryPurpose: exp?.Purpose,
            DirectoryAdvice: exp?.MigrationAdvice,
            HasSubtreeHardLinks: hazards.HasHardLinks,
            HasIncompleteNodes: hazards.HasIncompleteNodes,
            HasReparseDescendants: hazards.HasReparseDescendants,
            SpaceAccountingNote: spaceNote,
            HardLinkKind: hazards.HardLinkKind,
            InternalHardLinkGroupCount: hazards.InternalHardLinkGroupCount,
            ExternalHardLinkFileCount: hazards.ExternalHardLinkFileCount,
            ExternalHardLinkSamplePaths: sanitizedExternalPeers);
    }

    public AiUnknownDirectoryMetadataResponse InspectUnknownDirectoryMetadata(Guid scanId, int nodeId)
    {
        ScanSnapshot snapshot = RequireAuthorizedSnapshot(scanId);
        if ((uint)nodeId >= (uint)snapshot.Nodes.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeId), $"无效的节点 ID：{nodeId}。");
        }

        ref readonly ScanNodeRecord root = ref snapshot.Nodes[nodeId];
        if (!root.IsDirectory)
        {
            throw new InvalidOperationException($"节点 {nodeId} 不是目录。");
        }

        string fullPath = snapshot.GetFullPath(nodeId);
        SafetyPolicyDecision decision = EvaluateSnapshotPath(fullPath);

        // L0 硬拒绝区禁止做深入元数据画像，防止探测敏感系统或凭据目录结构
        if (decision.IsHardDenied)
        {
            throw new UnauthorizedAccessException($"目录命中 L0 硬拒绝保护策略（{string.Join(", ", decision.ReasonCodes)}），禁止 AI 探测内部元数据。");
        }

        // 仅从内存 ScanSnapshot 统计浅层子目录名、扩展名分布与修改时间桶，绝不访问磁盘读取任何文件内容
        var topSubdirs = new List<(string Name, long Size)>();
        foreach (int cid in snapshot.GetChildren(nodeId))
        {
            ref readonly ScanNodeRecord child = ref snapshot.Nodes[cid];
            if (child.IsDirectory && !child.IsOrphaned)
            {
                topSubdirs.Add((AiPathSanitizer.SanitizeUntrustedText(snapshot.GetNodeName(cid), 80, false),
                    Math.Max(child.UniqueAllocatedBytes, child.LogicalBytes)));
            }
        }
        string[] subDirNames = topSubdirs
            .OrderByDescending(x => x.Size)
            .Take(12)
            .Select(x => x.Name)
            .ToArray();

        var extStats = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        long nowTicks = DateTimeOffset.UtcNow.UtcTicks;
        long ticks7d = TimeSpan.FromDays(7).Ticks;
        long ticks30d = TimeSpan.FromDays(30).Ticks;
        long ticks90d = TimeSpan.FromDays(90).Ticks;

        (int Count, long Bytes) bucket7d = default;
        (int Count, long Bytes) bucket30d = default;
        (int Count, long Bytes) bucket90d = default;
        (int Count, long Bytes) bucketOlder = default;
        (int Count, long Bytes) bucketUnknown = default;

        // 有界遍历内存快照子树（上限 50,000 节点，保证毫秒级完成）
        var stack = new Stack<int>();
        stack.Push(nodeId);
        int visited = 0;
        const int maxVisit = 50_000;

        while (stack.Count > 0 && visited < maxVisit)
        {
            int cur = stack.Pop();
            visited++;
            ref readonly ScanNodeRecord curNode = ref snapshot.Nodes[cur];

            if (curNode.IsDirectory)
            {
                if (!curNode.IsReparsePoint)
                {
                    ReadOnlySpan<int> children = snapshot.GetChildrenSpan(cur);
                    for (int i = 0; i < children.Length; i++)
                    {
                        stack.Push(children[i]);
                    }
                }
                continue;
            }

            string name = snapshot.GetNodeName(cur);
            string ext = Path.GetExtension(name);
            string extKey = string.IsNullOrWhiteSpace(ext) ? "(无扩展名)" : ext.ToLowerInvariant();
            if (extKey.Length > 16) extKey = extKey[..16];

            long bytes = Math.Max(curNode.UniqueAllocatedBytes, curNode.LogicalBytes);
            extStats.TryGetValue(extKey, out var agg);
            extStats[extKey] = (agg.Count + 1, agg.Bytes + bytes);

            if (curNode.LastWriteUtcTicks <= 0 || curNode.LastWriteUtcTicks > nowTicks)
            {
                bucketUnknown = (bucketUnknown.Count + 1, bucketUnknown.Bytes + bytes);
            }
            else
            {
                long age = nowTicks - curNode.LastWriteUtcTicks;
                if (age <= ticks7d) bucket7d = (bucket7d.Count + 1, bucket7d.Bytes + bytes);
                else if (age <= ticks30d) bucket30d = (bucket30d.Count + 1, bucket30d.Bytes + bytes);
                else if (age <= ticks90d) bucket90d = (bucket90d.Count + 1, bucket90d.Bytes + bytes);
                else bucketOlder = (bucketOlder.Count + 1, bucketOlder.Bytes + bytes);
            }
        }

        AiExtensionStatDto[] topExtensions = extStats
            .OrderByDescending(kv => kv.Value.Bytes)
            .ThenByDescending(kv => kv.Value.Count)
            .Take(10)
            .Select(kv => new AiExtensionStatDto(kv.Key, kv.Value.Count, kv.Value.Bytes))
            .ToArray();

        var ageBuckets = new List<AiAgeBucketDto>();
        if (bucket7d.Count > 0) ageBuckets.Add(new("7 天内修改", bucket7d.Count, bucket7d.Bytes));
        if (bucket30d.Count > 0) ageBuckets.Add(new("7–30 天修改", bucket30d.Count, bucket30d.Bytes));
        if (bucket90d.Count > 0) ageBuckets.Add(new("30–90 天修改", bucket90d.Count, bucket90d.Bytes));
        if (bucketOlder.Count > 0) ageBuckets.Add(new("90 天以上未修改", bucketOlder.Count, bucketOlder.Bytes));
        if (bucketUnknown.Count > 0) ageBuckets.Add(new("时间未知", bucketUnknown.Count, bucketUnknown.Bytes));

        DirectoryExplanation? exp = KnownDirectoryExplainer.Explain(fullPath, _catalog, decision);

        return new AiUnknownDirectoryMetadataResponse(
            ScanId: scanId,
            NodeId: nodeId,
            SanitizedPath: AiPathSanitizer.SanitizePath(fullPath),
            AdmissionLevel: decision.Level,
            UniqueAllocatedBytes: root.UniqueAllocatedBytes,
            LogicalBytes: root.LogicalBytes,
            FileCount: root.FileCount,
            DirectoryCount: root.DirectoryCount,
            TopSubdirectoryNames: subDirNames,
            TopExtensions: topExtensions,
            ModifiedAgeBuckets: ageBuckets.ToArray(),
            DirectoryRoleTitle: exp?.RoleTitle,
            DirectoryPurpose: exp?.Purpose,
            ReadOnlyNotice: "仅基于内存扫描快照元数据聚合，绝未读取任何文件内容；结论仅供参考，请自行在资源管理器核实。");
    }

    public AiMigrationPolicyResponse GetMigrationPolicy()
    {
        return new AiMigrationPolicyResponse(
            PolicyVersion: MigrationSafetyPolicy.CurrentPolicyVersion,
            IsReadOnly: true,
            Levels:
            [
                new("L0", MigrationAdmissionLevel.HardDenied, "硬拒绝（禁止迁移）",
                    "操作系统核心、启动/恢复区、驱动/杀软/EDR、系统容器根、特殊文件系统结构及工具自身事务目录，永远不可迁移，AI 或配置均不可绕过。"),
                new("L1", MigrationAdmissionLevel.VerifiedEligible, "推荐迁移（已验证规则）",
                    "命中内置已验证规则的常用软件独立数据目录（如 NovaStudio/NovaMax 业务数据、ComfyUI models、SteamLibrary、Blender assets 等），可直接推荐加入迁移清单。"),
                new("L2", MigrationAdmissionLevel.VendorNativeRecommended, "厂商原生引导",
                    "已有稳定官方迁移机制或直接建链存在结构风险的软件（如 Docker Desktop WSL2、OneDrive），不推荐符号链接迁移，应引导使用官方原生方案。"),
                new("L3", MigrationAdmissionLevel.ManualRiskConfirm, "人工风险确认",
                    "位于非 L0 区的普通个人数据或未收录第三方软件目录，允许推荐但必须由用户人工核对兼容性并勾选确认后才可迁移。")
            ],
            HardDeniedCategories:
            [
                "Windows 操作系统核心与维护目录（Windows / System32 / WinSxS / Installer / SoftwareDistribution / Windows.old）",
                "启动、恢复与卷元数据（Boot / Recovery / EFI / System Volume Information / $Recycle.Bin / .MoveFolderQuarantine）",
                "系统级容器根（卷根 / Program Files 根 / ProgramData 根 / Users 根 / 用户配置根 / AppData 根）",
                "Windows 组件与安全/驱动目录（WindowsApps / Common Files / Defender / 杀软 / EDR）",
                "特殊文件系统结构（重解析点 / 硬链接 / EFS 加密 / 云占位）",
                "迁移工具本体、事务日志、待验证备份目录"
            ],
            StableReasonCodes:
            [
                SafetyReasonCodes.L0DriveRoot,
                SafetyReasonCodes.L0OsCore,
                SafetyReasonCodes.L0BootRecovery,
                SafetyReasonCodes.L0VolumeMetadata,
                SafetyReasonCodes.L0SystemContainerRoot,
                SafetyReasonCodes.L0WindowsComponent,
                SafetyReasonCodes.L0HighRiskSecurityOrDriver,
                SafetyReasonCodes.L0SpecialFsStructure,
                SafetyReasonCodes.L0SelfOrActiveTransaction,
                SafetyReasonCodes.L0AccessDeniedOrUnverified,
                SafetyReasonCodes.L0InvalidOrUnresolvedPath,
                SafetyReasonCodes.L0UnsupportedVolume,
                SafetyReasonCodes.L1VerifiedAppRule,
                SafetyReasonCodes.L2VendorNativeGuidance,
                SafetyReasonCodes.L3PersonalOrUnlistedConfirm
            ]);
    }

    public AiMigrationProposalReceipt SubmitMigrationProposal(AiMigrationProposalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string clientRequestId = AiPathSanitizer.SanitizeUntrustedText(request.ClientRequestId, 128, false);
        if (string.IsNullOrWhiteSpace(clientRequestId))
        {
            clientRequestId = $"req-{Guid.NewGuid():N}";
        }

        AiMigrationProposalRequest normalizedRequest = request with
        {
            ClientRequestId = clientRequestId,
            PolicyVersion = (request.PolicyVersion ?? string.Empty).Trim(),
            Items = request.Items ?? []
        };

        string digest = AiProposalInbox.ComputeRequestDigest(normalizedRequest);
        if (Inbox.TryGetIdempotentOrConflict(clientRequestId, digest, out AiMigrationProposalReceipt? replay, out bool isConflict))
        {
            if (isConflict)
            {
                return new AiMigrationProposalReceipt(
                    ProposalId: Guid.NewGuid(),
                    ClientRequestId: clientRequestId,
                    ScanId: request.ScanId,
                    PolicyVersion: MigrationSafetyPolicy.CurrentPolicyVersion,
                    ReceivedAtUtc: DateTimeOffset.UtcNow,
                    Status: AiProposalStatus.Invalidated,
                    IsIdempotentReplay: false,
                    AcceptedCount: 0,
                    BlockedByPolicyCount: 0,
                    RejectedCount: normalizedRequest.Items.Length,
                    Items: [],
                    Disclaimer: MandatoryDisclaimer,
                    RejectionMessage: $"幂等冲突：clientRequestId '{clientRequestId}' 已被不同请求正文使用，已拒绝重复提交。");
            }
            return replay!;
        }

        if (normalizedRequest.SchemaVersion != 1)
        {
            return BuildRejectedEnvelope(clientRequestId, request.ScanId, $"不支持的建议协议版本：{normalizedRequest.SchemaVersion}（仅支持 schemaVersion=1）。");
        }

        if (!string.IsNullOrEmpty(normalizedRequest.PolicyVersion)
            && !normalizedRequest.PolicyVersion.Equals(MigrationSafetyPolicy.CurrentPolicyVersion, StringComparison.OrdinalIgnoreCase))
        {
            return BuildRejectedEnvelope(clientRequestId, request.ScanId,
                $"策略版本不匹配（请求：{normalizedRequest.PolicyVersion}，当前：{MigrationSafetyPolicy.CurrentPolicyVersion}），请重新获取策略与候选后再提交。");
        }

        if (!TryGetAuthorizedSnapshot(request.ScanId, out ScanSnapshot? snapshot) || snapshot is null)
        {
            return BuildRejectedEnvelope(clientRequestId, request.ScanId, "目标扫描快照未授权或已失效，拒绝接收建议。");
        }

        if (normalizedRequest.Items.Length == 0)
        {
            return BuildRejectedEnvelope(clientRequestId, request.ScanId, "建议列表不能为空。");
        }

        if (normalizedRequest.Items.Length > MaxProposalItems)
        {
            return BuildRejectedEnvelope(clientRequestId, request.ScanId, $"单次建议最多允许 {MaxProposalItems} 项（当前：{normalizedRequest.Items.Length}）。");
        }

        AiTargetCandidateDto[] targetCandidates = GetTargetCandidatesForSourceDrive(snapshot.Volume.DriveRoot);
        var targetMap = targetCandidates.ToDictionary(t => t.TargetCandidateId, StringComparer.OrdinalIgnoreCase);

        var evaluatedItems = new List<AiMigrationProposalItemRecord>(normalizedRequest.Items.Length);
        var acceptedNodeIds = new HashSet<int>();
        var acceptedPaths = new List<string>();

        int acceptedCount = 0;
        int blockedByPolicyCount = 0;
        int rejectedCount = 0;

        foreach (AiMigrationProposalItemInput input in normalizedRequest.Items)
        {
            string reason = AiPathSanitizer.SanitizeUntrustedText(input.Reason, MaxReasonLength, preserveNewLines: false);
            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = "AI 未提供详细理由，请人工核实目录用途。";
            }

            string? compatNotes = string.IsNullOrWhiteSpace(input.CompatibilityNotes)
                ? null
                : AiPathSanitizer.SanitizeUntrustedText(input.CompatibilityNotes, MaxReasonLength, preserveNewLines: false);

            string category = NormalizeCategory(input.SuggestedCategory);

            if ((uint)input.NodeId >= (uint)snapshot.Nodes.Length || !snapshot.Nodes[input.NodeId].IsDirectory || snapshot.Nodes[input.NodeId].IsOrphaned)
            {
                rejectedCount++;
                evaluatedItems.Add(new AiMigrationProposalItemRecord(
                    ProposalItemId: Guid.NewGuid(),
                    NodeId: input.NodeId,
                    DisplayName: $"无效节点 #{input.NodeId}",
                    SanitizedPath: string.Empty,
                    ActualLocalPath: string.Empty,
                    LogicalBytes: 0,
                    UniqueAllocatedBytes: 0,
                    AdmissionLevel: MigrationAdmissionLevel.HardDenied,
                    MatchedApplicationId: null,
                    MatchedApplicationName: null,
                    TargetCandidateId: input.TargetCandidateId,
                    SuggestedTargetPath: null,
                    Priority: Math.Clamp(input.Priority, 1, 99),
                    Reason: reason,
                    CompatibilityNotes: compatNotes,
                    SuggestedCategory: category,
                    Verdict: AiProposalItemVerdict.RejectedInvalidNode,
                    ReasonCodes: [SafetyReasonCodes.L0InvalidOrUnresolvedPath],
                    VerdictMessage: "节点 ID 不存在或不是有效目录（仅允许使用服务端颁发的目录 nodeId）。",
                    IsAdoptableToMigrationPlan: false,
                    IsAdvisoryOnly: true));
                continue;
            }

            ref readonly ScanNodeRecord node = ref snapshot.Nodes[input.NodeId];
            string fullPath = snapshot.GetFullPath(input.NodeId);
            string sanitizedPath = AiPathSanitizer.SanitizePath(fullPath);
            SafetyPolicyDecision decision = EvaluateSnapshotPath(fullPath);
            string displayName = BuildDisplayName(snapshot, input.NodeId, decision.MatchedApplicationName);

            // 1. 快照不完整/已取消拦截
            if (!snapshot.CanUseForMigrationPlanning)
            {
                rejectedCount++;
                evaluatedItems.Add(new AiMigrationProposalItemRecord(
                    Guid.NewGuid(), input.NodeId, displayName, sanitizedPath, fullPath,
                    node.LogicalBytes, node.UniqueAllocatedBytes, decision.Level,
                    decision.MatchedApplicationId, decision.MatchedApplicationName,
                    input.TargetCandidateId, null, Math.Clamp(input.Priority, 1, 99),
                    reason, compatNotes, category,
                    AiProposalItemVerdict.RejectedSnapshotInvalid,
                    decision.ReasonCodes,
                    "当前扫描快照处于已取消或访问受限状态，不能用于生成迁移建议。",
                    IsAdoptableToMigrationPlan: false,
                    IsAdvisoryOnly: true));
                continue;
            }

            // 2. L0 硬拒绝永远拦截（即使模型受 Prompt Injection 诱导）
            if (decision.IsHardDenied)
            {
                blockedByPolicyCount++;
                evaluatedItems.Add(new AiMigrationProposalItemRecord(
                    Guid.NewGuid(), input.NodeId, displayName, sanitizedPath, fullPath,
                    node.LogicalBytes, node.UniqueAllocatedBytes, MigrationAdmissionLevel.HardDenied,
                    decision.MatchedApplicationId, decision.MatchedApplicationName,
                    input.TargetCandidateId, null, Math.Clamp(input.Priority, 1, 99),
                    reason, compatNotes, category,
                    AiProposalItemVerdict.RejectedByPolicy,
                    decision.ReasonCodes,
                    $"命中 L0 硬拒绝策略（{decision.Summary}）；AI 无法绕过或强制加入清单。",
                    IsAdoptableToMigrationPlan: false,
                    IsAdvisoryOnly: true));
                continue;
            }

            // 3. 子树外部硬链接 / 不完整节点拦截（纯目录内闭环硬链接支持目标盘无损重建，允许放行）
            SubtreeHazardReport hazards = snapshot.AnalyzeSubtreeHazards(input.NodeId, maxExternalSamples: 2);
            if (hazards.HasBlockingHardLinks || hazards.HasIncompleteNodes)
            {
                blockedByPolicyCount++;
                string hazardMsg;
                if (hazards.HasBlockingHardLinks)
                {
                    string[] samplePeers = hazards.SampleExternalPeerPaths.Select(p => AiPathSanitizer.SanitizePath(p)).ToArray();
                    string peerHint = samplePeers.Length > 0 ? $"（外部关联如：{string.Join("；", samplePeers)}）" : string.Empty;
                    hazardMsg = hazards.HardLinkKind == SubtreeHardLinkKind.ExternalLinked
                        ? $"该目录存在 {hazards.ExternalHardLinkFileCount} 个指向目录外部的硬链接文件{peerHint}，跨卷迁移会断开共享语义且无法释放源盘对应空间，已自动拦截。"
                        : "该目录子树包含无法核实闭环归属的硬链接文件，为防止破坏共享语义，已自动拦截。";
                }
                else
                {
                    hazardMsg = "该目录子树扫描不完整或存在损坏节点，已自动拦截。";
                }
                evaluatedItems.Add(new AiMigrationProposalItemRecord(
                    Guid.NewGuid(), input.NodeId, displayName, sanitizedPath, fullPath,
                    node.LogicalBytes, node.UniqueAllocatedBytes, MigrationAdmissionLevel.HardDenied,
                    decision.MatchedApplicationId, decision.MatchedApplicationName,
                    input.TargetCandidateId, null, Math.Clamp(input.Priority, 1, 99),
                    reason, compatNotes, category,
                    AiProposalItemVerdict.RejectedByPolicy,
                    [SafetyReasonCodes.L0SpecialFsStructure],
                    hazardMsg,
                    IsAdoptableToMigrationPlan: false,
                    IsAdvisoryOnly: true));
                continue;
            }

            // 4. 重复项与父子重叠检测
            bool duplicateOrOverlap = !acceptedNodeIds.Add(input.NodeId)
                || acceptedPaths.Any(p => IsSameOrAncestorOrDescendant(p, fullPath));
            if (duplicateOrOverlap)
            {
                rejectedCount++;
                evaluatedItems.Add(new AiMigrationProposalItemRecord(
                    Guid.NewGuid(), input.NodeId, displayName, sanitizedPath, fullPath,
                    node.LogicalBytes, node.UniqueAllocatedBytes, decision.Level,
                    decision.MatchedApplicationId, decision.MatchedApplicationName,
                    input.TargetCandidateId, null, Math.Clamp(input.Priority, 1, 99),
                    reason, compatNotes, category,
                    AiProposalItemVerdict.RejectedDuplicateOrOverlap,
                    decision.ReasonCodes,
                    "与同批建议中的其他目录重复或存在父子包含重叠，已自动去重拦截。",
                    IsAdoptableToMigrationPlan: false,
                    IsAdvisoryOnly: true));
                continue;
            }

            // 5. 校验 targetCandidateId（若提供则必须是服务端颁发的候选卷 ID，禁止任意绝对路径）
            string? resolvedTargetId = null;
            string? suggestedTargetPath = null;
            if (!string.IsNullOrWhiteSpace(input.TargetCandidateId))
            {
                string trimmedTargetId = input.TargetCandidateId.Trim();
                if (!targetMap.TryGetValue(trimmedTargetId, out AiTargetCandidateDto? targetDto))
                {
                    rejectedCount++;
                    evaluatedItems.Add(new AiMigrationProposalItemRecord(
                        Guid.NewGuid(), input.NodeId, displayName, sanitizedPath, fullPath,
                        node.LogicalBytes, node.UniqueAllocatedBytes, decision.Level,
                        decision.MatchedApplicationId, decision.MatchedApplicationName,
                        trimmedTargetId, null, Math.Clamp(input.Priority, 1, 99),
                        reason, compatNotes, category,
                        AiProposalItemVerdict.RejectedInvalidTarget,
                        decision.ReasonCodes,
                        $"无效的目标候选 ID：{trimmedTargetId}（仅允许服务端颁发的 targetCandidateId）。",
                        IsAdoptableToMigrationPlan: false,
                        IsAdvisoryOnly: true));
                    continue;
                }
                resolvedTargetId = targetDto.TargetCandidateId;
                suggestedTargetPath = TryBuildTargetPath(fullPath, targetDto.SuggestedRoot, decision.MatchedApplicationId);
            }
            else if (targetCandidates.Length > 0)
            {
                AiTargetCandidateDto fallbackTarget = targetCandidates.FirstOrDefault(t => t.IsRecommended) ?? targetCandidates[0];
                resolvedTargetId = fallbackTarget.TargetCandidateId;
                suggestedTargetPath = TryBuildTargetPath(fullPath, fallbackTarget.SuggestedRoot, decision.MatchedApplicationId);
            }

            // L2 原生引导（含本地规则命中或 AI 识别的 NativeGuideAdvisory）与疑似缓存诊断项只作提示卡，绝不可加入符号链接迁移清单
            bool isSuspectedCache = category.Equals("SuspectedCacheAdvisory", StringComparison.OrdinalIgnoreCase);
            bool isNativeGuide = decision.Level == MigrationAdmissionLevel.VendorNativeRecommended
                || category.Equals("NativeGuideAdvisory", StringComparison.OrdinalIgnoreCase);
            if (isNativeGuide && !isSuspectedCache)
            {
                category = "NativeGuideAdvisory";
            }

            bool isAdoptable = decision.Level is MigrationAdmissionLevel.VerifiedEligible or MigrationAdmissionLevel.ManualRiskConfirm
                && !isSuspectedCache
                && !isNativeGuide;

            string verdictMsg = isSuspectedCache
                ? "AI 诊断为规则外疑似缓存/大占用目录（二期仅只读提示，请在资源管理器核实；三期垃圾清理上线后可转入同卷隔离区复核）。"
                : isNativeGuide
                    ? "属于厂商原生迁移引导项，不建议直接建符号链接迁移，请参考软件官方迁移方式。"
                    : decision.Level switch
                    {
                        MigrationAdmissionLevel.VerifiedEligible => "通过 L1 安全策略复核，可由用户人工审阅后加入待迁移清单（不立即执行）。",
                        _ => "通过 L3 非高危策略复核，加入迁移清单时需用户人工确认兼容性风险。"
                    };

            acceptedPaths.Add(fullPath);
            acceptedCount++;

            evaluatedItems.Add(new AiMigrationProposalItemRecord(
                ProposalItemId: Guid.NewGuid(),
                NodeId: input.NodeId,
                DisplayName: displayName,
                SanitizedPath: sanitizedPath,
                ActualLocalPath: fullPath,
                LogicalBytes: node.LogicalBytes,
                UniqueAllocatedBytes: node.UniqueAllocatedBytes,
                AdmissionLevel: decision.Level,
                MatchedApplicationId: decision.MatchedApplicationId,
                MatchedApplicationName: decision.MatchedApplicationName,
                TargetCandidateId: resolvedTargetId,
                SuggestedTargetPath: suggestedTargetPath,
                Priority: Math.Clamp(input.Priority, 1, 99),
                Reason: reason,
                CompatibilityNotes: compatNotes,
                SuggestedCategory: category,
                Verdict: AiProposalItemVerdict.Accepted,
                ReasonCodes: decision.ReasonCodes,
                VerdictMessage: verdictMsg,
                IsAdoptableToMigrationPlan: isAdoptable,
                IsAdvisoryOnly: !isAdoptable));
        }

        var receipt = new AiMigrationProposalReceipt(
            ProposalId: Guid.NewGuid(),
            ClientRequestId: clientRequestId,
            ScanId: request.ScanId,
            PolicyVersion: MigrationSafetyPolicy.CurrentPolicyVersion,
            ReceivedAtUtc: DateTimeOffset.UtcNow,
            Status: AiProposalStatus.Received,
            IsIdempotentReplay: false,
            AcceptedCount: acceptedCount,
            BlockedByPolicyCount: blockedByPolicyCount,
            RejectedCount: rejectedCount,
            Items: evaluatedItems.ToArray(),
            Disclaimer: MandatoryDisclaimer);

        Inbox.Record(receipt, digest);
        return receipt;
    }

    public AiProposalStatusResponse GetProposalStatus(Guid proposalId)
    {
        return Inbox.GetStatus(proposalId)
            ?? throw new KeyNotFoundException($"未找到建议记录：{proposalId}。");
    }

    private ScanSnapshot RequireAuthorizedSnapshot(Guid scanId)
    {
        if (!TryGetAuthorizedSnapshot(scanId, out ScanSnapshot? snapshot) || snapshot is null)
        {
            throw new UnauthorizedAccessException($"扫描快照 {scanId} 未在当前会话授权范围内或已撤销。");
        }
        return snapshot;
    }

    private SafetyPolicyDecision EvaluateSnapshotPath(string fullPath) =>
        MigrationSafetyPolicy.EvaluateSource(
            fullPath,
            _catalog,
            SafetyEvaluationContext.ScanPreview,
            checkFilesystem: false,
            environment: _policyEnvironment);

    private AiTargetCandidateDto[] GetTargetCandidatesForSourceDrive(string sourceDriveRoot)
    {
        IReadOnlyList<AiTargetCandidateDto> all = _targetCandidatesProvider();
        string normSource = sourceDriveRoot.TrimEnd('\\');
        return all
            .Where(t => !t.DriveLetter.TrimEnd('\\').Equals(normSource, StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static IReadOnlyList<AiTargetCandidateDto> DefaultDiscoverTargetCandidates()
    {
        var list = new List<AiTargetCandidateDto>();
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                if (!drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) continue;

                string letter = drive.RootDirectory.FullName.TrimEnd('\\');
                string driveKey = letter.TrimEnd(':').ToLowerInvariant();
                bool isC = letter.Equals("C:", StringComparison.OrdinalIgnoreCase);
                bool recommended = !isC && drive.AvailableFreeSpace >= 20L * 1024 * 1024 * 1024;

                list.Add(new AiTargetCandidateDto(
                    TargetCandidateId: $"target-vol-{driveKey}",
                    DriveLetter: letter,
                    SuggestedRoot: Path.Combine(drive.RootDirectory.FullName, "MigratedData"),
                    AvailableBytes: drive.AvailableFreeSpace,
                    IsRecommended: recommended));
            }
        }
        catch
        {
            // 忽略单个驱动器查询异常
        }

        return list.OrderByDescending(x => x.IsRecommended).ThenByDescending(x => x.AvailableBytes).ToArray();
    }

    private AiSystemEnvironmentResponse CollectSystemEnvironment(int authorizedScanCount)
    {
        string systemDir = Environment.SystemDirectory;
        string systemDrive = !string.IsNullOrWhiteSpace(systemDir)
            ? (Path.GetPathRoot(systemDir) ?? @"C:\").TrimEnd('\\')
            : "C:";

        bool isAdmin = PathValidator.IsAdministrator();
        bool isDevMode = IsWindowsDeveloperModeEnabled();
        bool canCreateSymlinks = isAdmin || isDevMode;

        IReadOnlyList<AiTargetCandidateDto> targetCandidates = _targetCandidatesProvider();
        var targetMap = targetCandidates
            .GroupBy(t => t.DriveLetter.TrimEnd('\\'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var drives = new List<AiDiskVolumeInfoDto>();
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady) continue;

                string letter = drive.RootDirectory.FullName.TrimEnd('\\');
                bool isSys = letter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase);
                string format = string.IsNullOrWhiteSpace(drive.DriveFormat) ? "Unknown" : drive.DriveFormat;
                bool isFixed = drive.DriveType == DriveType.Fixed;
                bool isNtfs = format.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
                long total = Math.Max(0, drive.TotalSize);
                long free = Math.Max(0, drive.AvailableFreeSpace);
                long used = Math.Max(0, total - free);
                double usedPct = total > 0 ? Math.Round(used * 100.0 / total, 1) : 0;

                targetMap.TryGetValue(letter, out AiTargetCandidateDto? candidate);
                bool supportsTarget = isFixed && isNtfs && !isSys;
                string? targetNote = isSys
                    ? "系统盘（通常作为迁出源盘，不建议作为迁移目标）"
                    : !isFixed
                        ? $"驱动器类型为 {drive.DriveType}（非本地固定磁盘，不适合作为符号链接目标）"
                        : !isNtfs
                            ? $"文件系统为 {format}（仅固定 NTFS 卷支持安全符号链接迁移）"
                            : free < 20L * 1024 * 1024 * 1024
                                ? "剩余空间低于 20 GB，迁入大目录前请确认余量"
                                : "本地固定 NTFS 卷，适合作为安全迁移目标";

                string safeLabel = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? string.Empty
                    : AiPathSanitizer.SanitizeUntrustedText(drive.VolumeLabel, 48, preserveNewLines: false);

                drives.Add(new AiDiskVolumeInfoDto(
                    DriveLetter: letter,
                    VolumeLabel: safeLabel,
                    DriveType: drive.DriveType.ToString(),
                    FileSystem: format,
                    TotalBytes: total,
                    UsedBytes: used,
                    FreeBytes: free,
                    UsedPercent: usedPct,
                    IsSystemDrive: isSys,
                    SupportsSymlinkMigrationTarget: supportsTarget,
                    TargetCandidateId: candidate?.TargetCandidateId,
                    TargetNote: targetNote));
            }
        }
        catch
        {
            // 忽略单个驱动器查询异常
        }

        var specialFolders = new List<AiSpecialFolderLocationDto>();
        AddSanitizedSpecialFolder(specialFolders, "Desktop", "桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), systemDrive);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            string downloads = Path.Combine(userProfile, "Downloads");
            if (Directory.Exists(downloads))
            {
                AddSanitizedSpecialFolder(specialFolders, "Downloads", "下载", downloads, systemDrive);
            }
        }
        AddSanitizedSpecialFolder(specialFolders, "Documents", "文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), systemDrive);
        AddSanitizedSpecialFolder(specialFolders, "LocalAppData", "本地应用数据 (AppData\\Local)", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), systemDrive);
        AddSanitizedSpecialFolder(specialFolders, "RoamingAppData", "漫游应用数据 (AppData\\Roaming)", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), systemDrive);
        AddSanitizedSpecialFolder(specialFolders, "Temp", "系统临时目录 (Temp)", Path.GetTempPath(), systemDrive);

        return new AiSystemEnvironmentResponse(
            OsDescription: RuntimeInformation.OSDescription,
            OsVersion: Environment.OSVersion.Version.ToString(),
            OsArchitecture: RuntimeInformation.OSArchitecture.ToString(),
            SystemDrive: systemDrive,
            IsAdministrator: isAdmin,
            IsDeveloperModeEnabled: isDevMode,
            CanCreateSymlinks: canCreateSymlinks,
            CanUseRawNtfsMftScan: isAdmin,
            AuthorizedScanCount: authorizedScanCount,
            Drives: drives.OrderBy(d => d.DriveLetter, StringComparer.OrdinalIgnoreCase).ToArray(),
            SpecialFolders: specialFolders.ToArray(),
            PrivacyNotice: "已严格脱敏：仅包含磁盘卷容量/文件系统、OS 版本、权限前提与 <USER> 脱敏目录位置，绝不含真实用户名、计算机名、网络地址、环境变量或任何文件内容。");
    }

    private static void AddSanitizedSpecialFolder(
        List<AiSpecialFolderLocationDto> list,
        string key,
        string displayName,
        string? rawPath,
        string systemDrive)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return;
        string trimmed = rawPath.TrimEnd('\\');
        string driveLetter = (Path.GetPathRoot(trimmed) ?? string.Empty).TrimEnd('\\');
        if (string.IsNullOrWhiteSpace(driveLetter)) return;

        string sanitized = AiPathSanitizer.SanitizePath(trimmed);
        bool onSys = driveLetter.Equals(systemDrive, StringComparison.OrdinalIgnoreCase);
        list.Add(new AiSpecialFolderLocationDto(key, displayName, driveLetter, sanitized, onSys));
    }

    private static bool IsWindowsDeveloperModeEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock",
                writable: false);
            object? val = key?.GetValue("AllowDevelopmentWithoutDevLicense");
            return val is int intVal && intVal == 1;
        }
        catch
        {
            return false;
        }
    }

    private static AiScanSummaryItem BuildScanSummaryItem(ScanSnapshot snapshot)
    {
        string letter = snapshot.Volume.DriveRoot.TrimEnd('\\');
        string label = string.IsNullOrWhiteSpace(snapshot.Volume.VolumeLabel)
            ? $"本地磁盘 ({letter})"
            : $"{snapshot.Volume.VolumeLabel} ({letter})";

        long ageSec = Math.Max(0, (long)(DateTimeOffset.UtcNow - snapshot.CompletedAtUtc).TotalSeconds);

        return new AiScanSummaryItem(
            ScanId: snapshot.ScanId,
            VolumeDisplay: label,
            DriveLetter: letter,
            Backend: snapshot.Backend,
            Coverage: snapshot.Coverage,
            CanUseForMigrationPlanning: snapshot.CanUseForMigrationPlanning,
            TotalBytes: snapshot.Volume.TotalBytes,
            UsedBytes: snapshot.Volume.UsedBytes,
            FreeBytes: snapshot.Volume.FreeBytes,
            PolicyVersion: snapshot.PolicyVersion,
            CompletedAtUtc: snapshot.CompletedAtUtc,
            ScanAgeSeconds: ageSec,
            NodeCount: snapshot.Nodes.Length,
            IncompleteNodeCount: snapshot.IncompleteNodeCount,
            RequiresLiveReverification: snapshot.RequiresLiveReverification);
    }

    private static bool HasChildDirectories(ScanSnapshot snapshot, int nodeId)
    {
        ReadOnlySpan<int> children = snapshot.GetChildrenSpan(nodeId);
        for (int i = 0; i < children.Length; i++)
        {
            ref readonly ScanNodeRecord c = ref snapshot.Nodes[children[i]];
            if (c.IsDirectory && !c.IsOrphaned)
            {
                return true;
            }
        }
        return false;
    }

    private int[] CollectDisjointHotspots(ScanSnapshot snapshot, long minAllocatedBytes, int maxCount, int? startNodeId = null)
    {
        var results = new List<(int NodeId, long Size)>();

        void Visit(int nodeId, int depth)
        {
            ReadOnlySpan<int> children = snapshot.GetChildrenSpan(nodeId);
            for (int i = 0; i < children.Length; i++)
            {
                int cid = children[i];
                ref readonly ScanNodeRecord c = ref snapshot.Nodes[cid];
                if (!c.IsDirectory || c.IsOrphaned) continue;

                long size = Math.Max(c.UniqueAllocatedBytes, c.LogicalBytes);
                if (size < minAllocatedBytes) continue;

                string path = snapshot.GetFullPath(cid);
                SafetyPolicyDecision decision = EvaluateSnapshotPath(path);

                if (decision.IsHardDenied)
                {
                    if (depth < 5 && !c.IsReparsePoint && IsContainerToDescend(path, decision))
                    {
                        Visit(cid, depth + 1);
                    }
                    continue;
                }

                if (decision.Level is MigrationAdmissionLevel.VerifiedEligible or MigrationAdmissionLevel.VendorNativeRecommended)
                {
                    results.Add((cid, size));
                    continue;
                }

                if (depth < 4 && !c.IsReparsePoint && ShouldDescendToHighlightChildren(snapshot, cid, size, minAllocatedBytes))
                {
                    Visit(cid, depth + 1);
                }
                else
                {
                    results.Add((cid, size));
                }
            }
        }

        Visit(startNodeId ?? snapshot.RootNodeId, 0);
        return results
            .OrderByDescending(x => x.Size)
            .Take(maxCount)
            .Select(x => x.NodeId)
            .ToArray();
    }

    private static bool IsContainerToDescend(string fullPath, SafetyPolicyDecision decision)
    {
        if (decision.ReasonCodes.Contains(SafetyReasonCodes.L0SystemContainerRoot, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }
        string leaf = Path.GetFileName(fullPath.TrimEnd('\\'));
        return leaf.Equals("Users", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("AppData", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("Local", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("Roaming", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("LocalLow", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("Program Files", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("ProgramData", StringComparison.OrdinalIgnoreCase)
            || leaf.Equals("Programs", StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldDescendToHighlightChildren(ScanSnapshot snapshot, int nodeId, long parentSize, long minAllocatedBytes)
    {
        ReadOnlySpan<int> children = snapshot.GetChildrenSpan(nodeId);
        if (children.Length == 0) return false;

        string name = snapshot.GetNodeName(nodeId);
        bool isWrapper = name.Equals("Programs", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Apps", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Games", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AI_Workspace", StringComparison.OrdinalIgnoreCase);

        long largestChild = 0;
        int qualifying = 0;
        for (int i = 0; i < children.Length; i++)
        {
            int cid = children[i];
            ref readonly ScanNodeRecord c = ref snapshot.Nodes[cid];
            if (!c.IsDirectory || c.IsOrphaned) continue;
            long cSize = Math.Max(c.UniqueAllocatedBytes, c.LogicalBytes);
            if (cSize < minAllocatedBytes) continue;

            qualifying++;
            if (cSize > largestChild) largestChild = cSize;

            SafetyPolicyDecision cd = EvaluateSnapshotPath(snapshot.GetFullPath(cid));
            if (cd.Level is MigrationAdmissionLevel.VerifiedEligible or MigrationAdmissionLevel.VendorNativeRecommended)
            {
                return true;
            }
        }

        if (qualifying == 0) return false;
        if (isWrapper) return true;
        return qualifying == 1 && parentSize > 0 && largestChild * 100.0 / parentSize >= 85.0;
    }

    private static string BuildDisplayName(ScanSnapshot snapshot, int nodeId, string? matchedAppName)
    {
        string leaf = AiPathSanitizer.SanitizeUntrustedText(snapshot.GetNodeName(nodeId), 80, false);
        if (!string.IsNullOrWhiteSpace(matchedAppName)
            && !leaf.Equals(matchedAppName, StringComparison.OrdinalIgnoreCase))
        {
            return $"{matchedAppName} / {leaf}";
        }

        int parentId = snapshot.Nodes[nodeId].ParentNodeId;
        if (parentId >= 0 && parentId != snapshot.RootNodeId && parentId != nodeId)
        {
            string parentName = AiPathSanitizer.SanitizeUntrustedText(snapshot.GetNodeName(parentId), 60, false);
            if (!string.IsNullOrWhiteSpace(parentName)
                && !parentName.Equals("Users", StringComparison.OrdinalIgnoreCase)
                && !parentName.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase))
            {
                return $"{parentName} / {leaf}";
            }
        }

        return leaf;
    }

    private static string NormalizeCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "Migration";
        string c = category.Trim();
        if (c.Equals("SuspectedCacheAdvisory", StringComparison.OrdinalIgnoreCase)
            || c.Equals("SuspectedResidual", StringComparison.OrdinalIgnoreCase)
            || c.Equals("CleanupAdvisory", StringComparison.OrdinalIgnoreCase))
        {
            return "SuspectedCacheAdvisory";
        }
        if (c.Equals("NativeGuideAdvisory", StringComparison.OrdinalIgnoreCase)
            || c.Equals("NativeGuide", StringComparison.OrdinalIgnoreCase)
            || c.Equals("VendorNative", StringComparison.OrdinalIgnoreCase)
            || c.Equals("VendorNativeRecommended", StringComparison.OrdinalIgnoreCase)
            || c.Equals("L2NativeGuide", StringComparison.OrdinalIgnoreCase))
        {
            return "NativeGuideAdvisory";
        }
        return "Migration";
    }

    private static string? TryBuildTargetPath(string sourcePath, string targetRoot, string? appId)
    {
        try
        {
            return MigrationPlanService.BuildDefaultTargetPath(sourcePath, targetRoot, appId);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSameOrAncestorOrDescendant(string pathA, string pathB)
    {
        string a = pathA.TrimEnd('\\');
        string b = pathB.TrimEnd('\\');
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        return a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase)
            || b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        return int.TryParse(cursor.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed >= 0
            ? parsed
            : 0;
    }

    private static AiMigrationProposalReceipt BuildRejectedEnvelope(string clientRequestId, Guid scanId, string message) =>
        new(
            ProposalId: Guid.NewGuid(),
            ClientRequestId: clientRequestId,
            ScanId: scanId,
            PolicyVersion: MigrationSafetyPolicy.CurrentPolicyVersion,
            ReceivedAtUtc: DateTimeOffset.UtcNow,
            Status: AiProposalStatus.Invalidated,
            IsIdempotentReplay: false,
            AcceptedCount: 0,
            BlockedByPolicyCount: 0,
            RejectedCount: 0,
            Items: [],
            Disclaimer: MandatoryDisclaimer,
            RejectionMessage: message);
}
