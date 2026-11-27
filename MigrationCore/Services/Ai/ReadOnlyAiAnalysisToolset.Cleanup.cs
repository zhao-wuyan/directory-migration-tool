using System.Globalization;
using MigrationCore.Models.Ai;
using MigrationCore.Models.Cleanup;
using MigrationCore.Models.Scanning;
using MigrationCore.Services.Cleanup;
using MigrationCore.Services.Safety;

namespace MigrationCore.Services.Ai;

public sealed partial class ReadOnlyAiAnalysisToolset
{
    // ==================== 三期：垃圾清理只读分析与受限建议提交 ====================

    public AiCleanupScanSummaryResponse GetCleanupScanSummary(Guid cleanupScanId)
    {
        CleanupScanSnapshot snapshot = RequireAuthorizedCleanupSnapshot(cleanupScanId);
        return new AiCleanupScanSummaryResponse(
            CleanupScanId: snapshot.CleanupScanId,
            ScannedAtUtc: snapshot.ScannedAtUtc,
            PolicyVersion: snapshot.PolicyVersion,
            RuleVersion: snapshot.RuleVersion,
            TotalReclaimableBytes: snapshot.TotalReclaimableBytes,
            RecommendedBytes: snapshot.RecommendedBytes,
            OnDemandBytes: snapshot.OnDemandBytes,
            CautionBytes: snapshot.CautionBytes,
            RecommendedRuleCount: snapshot.Rules.Count(r => r.Rule.Tier == CleanupRecommendationTier.Recommended),
            OnDemandRuleCount: snapshot.Rules.Count(r => r.Rule.Tier == CleanupRecommendationTier.OnDemand),
            CautionRuleCount: snapshot.Rules.Count(r => r.Rule.Tier == CleanupRecommendationTier.Caution),
            AvailableRuleCount: snapshot.Rules.Count(r => r.IsAvailable),
            Disclaimer: MandatoryDisclaimer);
    }

    public AiCleanupItemsResponse ListCleanupItems(Guid cleanupScanId, string? tier = null, string? cursor = null, int pageSize = 30)
    {
        CleanupScanSnapshot snapshot = RequireAuthorizedCleanupSnapshot(cleanupScanId);
        int clampedPageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        int offset = ParseCursor(cursor);

        IEnumerable<CleanupRuleScanResult> query = snapshot.Rules;
        if (!string.IsNullOrWhiteSpace(tier) && Enum.TryParse(tier.Trim(), ignoreCase: true, out CleanupRecommendationTier parsedTier))
        {
            query = query.Where(r => r.Rule.Tier == parsedTier);
        }

        AiCleanupRuleSummaryDto[] all = query
            .OrderByDescending(r => r.IsAvailable)
            .ThenBy(r => (int)r.Rule.Tier)
            .ThenByDescending(r => r.TotalBytes)
            .Select(r => new AiCleanupRuleSummaryDto(
                RuleId: r.Rule.RuleId,
                DisplayName: r.Rule.DisplayName,
                Category: r.Rule.Category,
                Tier: r.Rule.Tier,
                Channel: r.Rule.Channel,
                TotalBytes: r.TotalBytes,
                MatchedCount: r.MatchedCount,
                IsAvailable: r.IsAvailable,
                HasBlockingProcesses: r.HasBlockingProcesses,
                BlockingProcesses: r.RunningBlockingProcesses,
                CostAndReclaimNote: r.Rule.CostAndReclaimNote,
                StatusNote: r.StatusNote,
                OneLineCautionImpact: r.Rule.CautionReason?.OneLineImpact))
            .ToArray();

        AiCleanupRuleSummaryDto[] page = all.Skip(offset).Take(clampedPageSize).ToArray();
        string? nextCursor = offset + page.Length < all.Length
            ? (offset + page.Length).ToString(CultureInfo.InvariantCulture)
            : null;

        return new AiCleanupItemsResponse(
            CleanupScanId: cleanupScanId,
            TierFilter: tier,
            Items: page,
            NextCursor: nextCursor,
            TotalCount: all.Length,
            Disclaimer: MandatoryDisclaimer);
    }

    public AiCleanupItemDetailsResponse GetCleanupItemDetails(Guid cleanupScanId, string ruleId)
    {
        CleanupScanSnapshot snapshot = RequireAuthorizedCleanupSnapshot(cleanupScanId);
        CleanupRuleScanResult? hit = snapshot.Rules.FirstOrDefault(r =>
            r.Rule.RuleId.Equals(ruleId?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (hit is null)
        {
            throw new KeyNotFoundException($"未找到清理规则 ID：{ruleId}。");
        }

        AiCleanupPreviewSampleDto[] samples = hit.PreviewItems
            .Take(20)
            .Select(p => new AiCleanupPreviewSampleDto(
                DisplayName: AiPathSanitizer.SanitizeUntrustedText(p.DisplayName, 120, false),
                SanitizedPath: AiPathSanitizer.SanitizePath(p.FullPath),
                SizeBytes: p.SizeBytes,
                KindTag: p.KindTag))
            .ToArray();

        return new AiCleanupItemDetailsResponse(
            CleanupScanId: cleanupScanId,
            RuleId: hit.Rule.RuleId,
            DisplayName: hit.Rule.DisplayName,
            ShortDescription: hit.Rule.ShortDescription,
            Category: hit.Rule.Category,
            Tier: hit.Rule.Tier,
            Channel: hit.Rule.Channel,
            TotalBytes: hit.TotalBytes,
            MatchedCount: hit.MatchedCount,
            SkippedRecentOrLockedCount: hit.SkippedRecentOrLockedCount,
            RunningBlockingProcesses: hit.RunningBlockingProcesses,
            CostAndReclaimNote: hit.Rule.CostAndReclaimNote,
            IsRebuildable: hit.Rule.IsRebuildable,
            CautionReason: hit.Rule.CautionReason,
            SamplePreviewItems: samples,
            Disclaimer: MandatoryDisclaimer);
    }

    public AiCleanupProposalReceipt SubmitCleanupProposal(AiCleanupProposalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string clientRequestId = AiPathSanitizer.SanitizeUntrustedText(request.ClientRequestId, 128, false);
        if (string.IsNullOrWhiteSpace(clientRequestId))
        {
            clientRequestId = $"creq-{Guid.NewGuid():N}";
        }

        AiCleanupProposalRequest normalized = request with
        {
            ClientRequestId = clientRequestId,
            PolicyVersion = (request.PolicyVersion ?? string.Empty).Trim(),
            Items = request.Items ?? []
        };

        string digest = AiProposalInbox.ComputeCleanupRequestDigest(normalized);
        if (Inbox.TryGetCleanupIdempotentOrConflict(clientRequestId, digest, out AiCleanupProposalReceipt? replay, out bool isConflict))
        {
            if (isConflict)
            {
                return BuildCleanupRejectedEnvelope(clientRequestId, request.ScanId, request.CleanupScanId,
                    $"幂等冲突：clientRequestId '{clientRequestId}' 已被不同清理建议正文使用，已拒绝重复提交。");
            }
            return replay!;
        }

        if (normalized.SchemaVersion != 1)
        {
            return BuildCleanupRejectedEnvelope(clientRequestId, request.ScanId, request.CleanupScanId,
                $"不支持的建议协议版本：{normalized.SchemaVersion}（仅支持 schemaVersion=1）。");
        }

        if (!string.IsNullOrEmpty(normalized.PolicyVersion)
            && !normalized.PolicyVersion.Equals(MigrationSafetyPolicy.CurrentPolicyVersion, StringComparison.OrdinalIgnoreCase))
        {
            return BuildCleanupRejectedEnvelope(clientRequestId, request.ScanId, request.CleanupScanId,
                $"策略版本不匹配（请求：{normalized.PolicyVersion}，当前：{MigrationSafetyPolicy.CurrentPolicyVersion}）。");
        }

        if (normalized.Items.Length == 0 || normalized.Items.Length > MaxCleanupProposalItems)
        {
            return BuildCleanupRejectedEnvelope(clientRequestId, request.ScanId, request.CleanupScanId,
                $"清理建议条目数必须在 1～{MaxCleanupProposalItems} 之间（当前：{normalized.Items.Length}）。");
        }

        ScanSnapshot? diskSnapshot = null;
        if (normalized.ScanId is Guid sid) TryGetAuthorizedSnapshot(sid, out diskSnapshot);
        diskSnapshot ??= GetLatestAuthorizedSnapshot();

        CleanupScanSnapshot? cleanupSnapshot = null;
        if (normalized.CleanupScanId is Guid cid) TryGetAuthorizedCleanupSnapshot(cid, out cleanupSnapshot);
        cleanupSnapshot ??= GetLatestAuthorizedCleanupSnapshot();

        IReadOnlyList<string> protectedTxPaths = CleanupExclusionPolicy.LoadProtectedTransactionPaths();
        var evaluated = new List<AiCleanupProposalItemRecord>(normalized.Items.Length);
        var seenRuleIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var acceptedResidualPaths = new List<string>();
        var seenResidualNodes = new HashSet<int>();

        int acceptedRuleCount = 0;
        int acceptedResidualCount = 0;
        int hardBlockedCount = 0;
        int rejectedCount = 0;

        foreach (AiCleanupProposalItemInput input in normalized.Items)
        {
            string kind = NormalizeCleanupProposalKind(input.Kind, input.RuleId, input.NodeId);
            string reason = AiPathSanitizer.SanitizeUntrustedText(input.Reason, MaxReasonLength, preserveNewLines: false);
            if (string.IsNullOrWhiteSpace(reason))
            {
                reason = "AI 未提供详细理由，请人工打开目录核实后再做决定。";
            }
            string riskWarning = AiPathSanitizer.SanitizeUntrustedText(input.RiskWarning, MaxReasonLength, preserveNewLines: false);
            if (string.IsNullOrWhiteSpace(riskWarning))
            {
                riskWarning = "AI 推测结论，可能存在未识别项目依赖，请自行打开目录核实后再决定是否隔离。";
            }

            if (kind == "RuleMatched")
            {
                if (cleanupSnapshot is null || string.IsNullOrWhiteSpace(input.RuleId))
                {
                    rejectedCount++;
                    evaluated.Add(new AiCleanupProposalItemRecord(
                        Guid.NewGuid(), kind, input.RuleId, null, input.RuleId ?? "(缺失 ruleId)",
                        string.Empty, string.Empty, 0, 0, null, "RuleReviewOnly",
                        Math.Clamp(input.Priority, 1, 99), reason, riskWarning,
                        AiCleanupProposalItemVerdict.RejectedInvalidTarget, null, null,
                        "未找到已授权的垃圾清理扫描快照或 ruleId 无效。"));
                    continue;
                }

                CleanupRuleScanResult? ruleHit = cleanupSnapshot.Rules.FirstOrDefault(r =>
                    r.Rule.RuleId.Equals(input.RuleId.Trim(), StringComparison.OrdinalIgnoreCase));
                if (ruleHit is null)
                {
                    rejectedCount++;
                    evaluated.Add(new AiCleanupProposalItemRecord(
                        Guid.NewGuid(), kind, input.RuleId, null, input.RuleId,
                        string.Empty, string.Empty, 0, 0, null, "RuleReviewOnly",
                        Math.Clamp(input.Priority, 1, 99), reason, riskWarning,
                        AiCleanupProposalItemVerdict.RejectedInvalidTarget, null, null,
                        $"规则 ID '{input.RuleId}' 不在内置受审规则库中，拒绝接收。"));
                    continue;
                }

                if (!seenRuleIds.Add(ruleHit.Rule.RuleId))
                {
                    rejectedCount++;
                    evaluated.Add(new AiCleanupProposalItemRecord(
                        Guid.NewGuid(), kind, ruleHit.Rule.RuleId, null, ruleHit.Rule.DisplayName,
                        string.Empty, string.Empty, ruleHit.TotalBytes, ruleHit.MatchedCount,
                        ruleHit.Rule.Tier, "RuleReviewOnly", Math.Clamp(input.Priority, 1, 99),
                        reason, riskWarning, AiCleanupProposalItemVerdict.RejectedDuplicateOrOverlap,
                        null, null, "同批建议中存在重复的清理规则项，已去重。"));
                    continue;
                }

                acceptedRuleCount++;
                evaluated.Add(new AiCleanupProposalItemRecord(
                    ProposalItemId: Guid.NewGuid(),
                    Kind: kind,
                    RuleId: ruleHit.Rule.RuleId,
                    NodeId: null,
                    DisplayName: ruleHit.Rule.DisplayName,
                    SanitizedPath: ruleHit.Rule.RuleId,
                    ActualLocalPath: string.Empty,
                    SizeBytes: ruleHit.TotalBytes,
                    FileCount: ruleHit.MatchedCount,
                    OriginalRuleTier: ruleHit.Rule.Tier,
                    EnforcedChannel: ruleHit.Rule.Channel == CleanupExecutionChannel.OfficialSystemApi ? "OfficialSystemApi" : "QuarantineSameVolume",
                    Priority: Math.Clamp(input.Priority, 1, 99),
                    Reason: reason,
                    RiskWarning: riskWarning,
                    Verdict: AiCleanupProposalItemVerdict.AcceptedRuleAdvice,
                    ExclusionReasonCode: null,
                    ExclusionCategoryTitle: null,
                    VerdictMessage: $"已接收规则解读建议（严格保留原档位：{ruleHit.Rule.Tier}，不自动勾选）。"));
            }
            else
            {
                if (diskSnapshot is null || input.NodeId is not int nid || (uint)nid >= (uint)diskSnapshot.Nodes.Length)
                {
                    rejectedCount++;
                    evaluated.Add(new AiCleanupProposalItemRecord(
                        Guid.NewGuid(), "AiSuspectedResidual", null, input.NodeId, $"无效节点 #{input.NodeId}",
                        string.Empty, string.Empty, 0, 0, null, "QuarantineSameVolume",
                        Math.Clamp(input.Priority, 1, 99), reason, riskWarning,
                        AiCleanupProposalItemVerdict.RejectedInvalidTarget, null, null,
                        "未找到已授权磁盘扫描快照或 nodeId 无效（禁止提交任意绝对路径）。"));
                    continue;
                }

                ref readonly ScanNodeRecord node = ref diskSnapshot.Nodes[nid];
                if (!node.IsDirectory || node.IsOrphaned)
                {
                    rejectedCount++;
                    evaluated.Add(new AiCleanupProposalItemRecord(
                        Guid.NewGuid(), "AiSuspectedResidual", null, nid, $"非目录节点 #{nid}",
                        string.Empty, string.Empty, 0, 0, null, "QuarantineSameVolume",
                        Math.Clamp(input.Priority, 1, 99), reason, riskWarning,
                        AiCleanupProposalItemVerdict.RejectedInvalidTarget, null, null,
                        "仅允许对有效目录提交 AI 疑似残留隔离建议。"));
                    continue;
                }

                string fullPath = diskSnapshot.GetFullPath(nid);
                string sanitizedPath = AiPathSanitizer.SanitizePath(fullPath);
                string displayName = BuildDisplayName(diskSnapshot, nid, null);
                long sizeBytes = Math.Max(node.UniqueAllocatedBytes, node.LogicalBytes);

                CleanupExclusionVerdict exclusion = CleanupExclusionPolicy.EvaluateAiSuspectedResidualDirectory(
                    fullPath,
                    snapshot: diskSnapshot,
                    nodeId: nid,
                    registeredTransactionPaths: protectedTxPaths,
                    environment: _policyEnvironment,
                    checkFilesystem: false);

                if (exclusion.IsExcluded)
                {
                    hardBlockedCount++;
                    evaluated.Add(new AiCleanupProposalItemRecord(
                        ProposalItemId: Guid.NewGuid(),
                        Kind: "AiSuspectedResidual",
                        RuleId: null,
                        NodeId: nid,
                        DisplayName: displayName,
                        SanitizedPath: sanitizedPath,
                        ActualLocalPath: fullPath,
                        SizeBytes: sizeBytes,
                        FileCount: node.FileCount,
                        OriginalRuleTier: null,
                        EnforcedChannel: "Blocked",
                        Priority: Math.Clamp(input.Priority, 1, 99),
                        Reason: reason,
                        RiskWarning: riskWarning,
                        Verdict: AiCleanupProposalItemVerdict.HardBlockedByExclusion,
                        ExclusionReasonCode: exclusion.ReasonCode,
                        ExclusionCategoryTitle: exclusion.CategoryTitle,
                        VerdictMessage: exclusion.Message ?? "命中永久排除区，强制拒收且永不提供勾选入口。"));
                    continue;
                }

                if (!seenResidualNodes.Add(nid) || acceptedResidualPaths.Any(p => IsSameOrAncestorOrDescendant(p, fullPath)))
                {
                    rejectedCount++;
                    evaluated.Add(new AiCleanupProposalItemRecord(
                        Guid.NewGuid(), "AiSuspectedResidual", null, nid, displayName,
                        sanitizedPath, fullPath, sizeBytes, node.FileCount, null, "QuarantineSameVolume",
                        Math.Clamp(input.Priority, 1, 99), reason, riskWarning,
                        AiCleanupProposalItemVerdict.RejectedDuplicateOrOverlap, null, null,
                        "与同批其他疑似残留目录重复或存在父子包含重叠，已自动去重。"));
                    continue;
                }

                acceptedResidualPaths.Add(fullPath);
                acceptedResidualCount++;
                evaluated.Add(new AiCleanupProposalItemRecord(
                    ProposalItemId: Guid.NewGuid(),
                    Kind: "AiSuspectedResidual",
                    RuleId: null,
                    NodeId: nid,
                    DisplayName: displayName,
                    SanitizedPath: sanitizedPath,
                    ActualLocalPath: fullPath,
                    SizeBytes: sizeBytes,
                    FileCount: node.FileCount,
                    OriginalRuleTier: null,
                    EnforcedChannel: "QuarantineSameVolume",
                    Priority: Math.Clamp(input.Priority, 1, 99),
                    Reason: reason,
                    RiskWarning: riskWarning,
                    Verdict: AiCleanupProposalItemVerdict.AcceptedResidualQuarantineOnly,
                    ExclusionReasonCode: null,
                    ExclusionCategoryTitle: null,
                    VerdictMessage: "已通过永久排除区核验，待用户在建议审阅弹框确认后加入「AI 疑似残留」列表（默认未勾选；仅限通道 A 同卷隔离且须独立免责二次确认）。"));
            }
        }

        var receipt = new AiCleanupProposalReceipt(
            ProposalId: Guid.NewGuid(),
            ClientRequestId: clientRequestId,
            ScanId: diskSnapshot?.ScanId ?? normalized.ScanId,
            CleanupScanId: cleanupSnapshot?.CleanupScanId ?? normalized.CleanupScanId,
            PolicyVersion: MigrationSafetyPolicy.CurrentPolicyVersion,
            ReceivedAtUtc: DateTimeOffset.UtcNow,
            Status: AiProposalStatus.Received,
            IsIdempotentReplay: false,
            AcceptedRuleAdviceCount: acceptedRuleCount,
            AcceptedResidualCount: acceptedResidualCount,
            HardBlockedCount: hardBlockedCount,
            RejectedCount: rejectedCount,
            Items: evaluated.ToArray(),
            Disclaimer: MandatoryDisclaimer);

        Inbox.RecordCleanupProposal(receipt, digest);
        return receipt;
    }

    private bool TryGetAuthorizedCleanupSnapshot(Guid cleanupScanId, out CleanupScanSnapshot? snapshot)
    {
        lock (_sync)
        {
            return _authorizedCleanupScans.TryGetValue(cleanupScanId, out snapshot);
        }
    }

    private CleanupScanSnapshot RequireAuthorizedCleanupSnapshot(Guid cleanupScanId)
    {
        if (!TryGetAuthorizedCleanupSnapshot(cleanupScanId, out CleanupScanSnapshot? snapshot) || snapshot is null)
        {
            throw new UnauthorizedAccessException("目标垃圾清理扫描快照未授权或已过期，请先在「垃圾清理」页执行扫描。");
        }
        return snapshot;
    }

    private static string NormalizeCleanupProposalKind(string? kind, string? ruleId, int? nodeId)
    {
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (kind.Trim().Equals("RuleMatched", StringComparison.OrdinalIgnoreCase)) return "RuleMatched";
            if (kind.Trim().Equals("AiSuspectedResidual", StringComparison.OrdinalIgnoreCase)) return "AiSuspectedResidual";
        }
        return !string.IsNullOrWhiteSpace(ruleId) && nodeId is null ? "RuleMatched" : "AiSuspectedResidual";
    }

    private static AiCleanupProposalReceipt BuildCleanupRejectedEnvelope(
        string clientRequestId,
        Guid? scanId,
        Guid? cleanupScanId,
        string message) =>
        new(
            ProposalId: Guid.NewGuid(),
            ClientRequestId: clientRequestId,
            ScanId: scanId,
            CleanupScanId: cleanupScanId,
            PolicyVersion: MigrationSafetyPolicy.CurrentPolicyVersion,
            ReceivedAtUtc: DateTimeOffset.UtcNow,
            Status: AiProposalStatus.Invalidated,
            IsIdempotentReplay: false,
            AcceptedRuleAdviceCount: 0,
            AcceptedResidualCount: 0,
            HardBlockedCount: 0,
            RejectedCount: 0,
            Items: [],
            Disclaimer: MandatoryDisclaimer,
            RejectionMessage: message);
}
