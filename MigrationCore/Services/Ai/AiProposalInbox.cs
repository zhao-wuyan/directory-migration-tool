using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MigrationCore.Models.Ai;
using MigrationCore.Models.Cleanup;

namespace MigrationCore.Services.Ai;

/// <summary>
/// AI 建议收件箱：负责幂等性校验（clientRequestId + 规范化正文 SHA-256）、状态流转、
/// 快照变更/撤销时的自动失效作废，以及向 UI 提供只读审核列表。
/// 本组件只保存“建议”，严禁直接触发任何迁移或删除操作。
/// </summary>
public sealed class AiProposalInbox
{
    private sealed class StoredProposal
    {
        public required AiMigrationProposalReceipt Receipt { get; set; }
        public required string RequestDigest { get; init; }
        public DateTimeOffset? UpdatedAtUtc { get; set; }
        public string StatusNote { get; set; } = "已收到 AI 建议，等待用户人工审阅；绝不会自动执行。";
    }

    private readonly Lock _sync = new();
    private readonly Dictionary<Guid, StoredProposal> _byProposalId = new();
    private readonly Dictionary<string, (Guid ProposalId, string RequestDigest)> _byClientRequestId = new(StringComparer.Ordinal);
    private readonly List<Guid> _orderedIds = new();

    // ---- 三期：清理建议收件箱（规则排序建议 + 规则外 AI 疑似残留隔离候选 + 硬拦截审计记录） ----
    private readonly Dictionary<Guid, AiCleanupProposalReceipt> _cleanupReceipts = new();
    private readonly Dictionary<string, (Guid ProposalId, string RequestDigest)> _cleanupByClientRequestId = new(StringComparer.Ordinal);
    private readonly List<AiCleanupCandidateRecord> _cleanupResidualCandidates = new();
    private readonly List<AiCleanupRuleAdviceRecord> _cleanupRuleAdvices = new();

    public event EventHandler? InboxChanged;
    public event EventHandler? CleanupProposalsChanged;

    public IReadOnlyList<AiMigrationProposalReceipt> GetAllReceipts()
    {
        lock (_sync)
        {
            var list = new List<AiMigrationProposalReceipt>(_orderedIds.Count);
            for (int i = _orderedIds.Count - 1; i >= 0; i--)
            {
                if (_byProposalId.TryGetValue(_orderedIds[i], out StoredProposal? stored))
                {
                    list.Add(stored.Receipt);
                }
            }
            return list;
        }
    }

    public bool TryGetIdempotentOrConflict(
        string clientRequestId,
        string requestDigest,
        out AiMigrationProposalReceipt? existingReplay,
        out bool isConflict)
    {
        lock (_sync)
        {
            if (_byClientRequestId.TryGetValue(clientRequestId, out var hit)
                && _byProposalId.TryGetValue(hit.ProposalId, out StoredProposal? stored))
            {
                if (string.Equals(hit.RequestDigest, requestDigest, StringComparison.Ordinal))
                {
                    existingReplay = stored.Receipt with { IsIdempotentReplay = true };
                    isConflict = false;
                    return true;
                }

                existingReplay = null;
                isConflict = true;
                return true;
            }

            existingReplay = null;
            isConflict = false;
            return false;
        }
    }

    public void Record(AiMigrationProposalReceipt receipt, string requestDigest)
    {
        lock (_sync)
        {
            _byProposalId[receipt.ProposalId] = new StoredProposal
            {
                Receipt = receipt,
                RequestDigest = requestDigest,
                UpdatedAtUtc = receipt.ReceivedAtUtc,
                StatusNote = receipt.AcceptedCount > 0
                    ? $"已接收 {receipt.AcceptedCount} 条有效建议，等待用户审阅；拦截 {receipt.BlockedByPolicyCount} 条不合规项。"
                    : "建议中无符合安全策略的可采纳目录。"
            };

            if (!string.IsNullOrWhiteSpace(receipt.ClientRequestId))
            {
                _byClientRequestId[receipt.ClientRequestId] = (receipt.ProposalId, requestDigest);
            }

            _orderedIds.Add(receipt.ProposalId);
        }

        InboxChanged?.Invoke(this, EventArgs.Empty);
    }

    public AiProposalStatusResponse? GetStatus(Guid proposalId)
    {
        lock (_sync)
        {
            if (!_byProposalId.TryGetValue(proposalId, out StoredProposal? stored))
            {
                return null;
            }

            return new AiProposalStatusResponse(
                ProposalId: stored.Receipt.ProposalId,
                ScanId: stored.Receipt.ScanId,
                Status: stored.Receipt.Status,
                ReceivedAtUtc: stored.Receipt.ReceivedAtUtc,
                UpdatedAtUtc: stored.UpdatedAtUtc,
                AcceptedCount: stored.Receipt.AcceptedCount,
                BlockedByPolicyCount: stored.Receipt.BlockedByPolicyCount,
                ExecutionAuthorized: false,
                StatusNote: stored.StatusNote);
        }
    }

    public bool UpdateStatus(Guid proposalId, AiProposalStatus newStatus, string statusNote)
    {
        bool changed = false;
        lock (_sync)
        {
            if (_byProposalId.TryGetValue(proposalId, out StoredProposal? stored))
            {
                if (stored.Receipt.Status == AiProposalStatus.Invalidated && newStatus != AiProposalStatus.Invalidated)
                {
                    return false;
                }

                stored.Receipt = stored.Receipt with { Status = newStatus };
                stored.UpdatedAtUtc = DateTimeOffset.UtcNow;
                stored.StatusNote = statusNote;
                changed = true;
            }
        }

        if (changed)
        {
            InboxChanged?.Invoke(this, EventArgs.Empty);
        }
        return changed;
    }

    public int DismissAllActive()
    {
        int count = 0;
        lock (_sync)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (StoredProposal stored in _byProposalId.Values)
            {
                if (stored.Receipt.Status is AiProposalStatus.Received or AiProposalStatus.Reviewed)
                {
                    stored.Receipt = stored.Receipt with { Status = AiProposalStatus.Dismissed };
                    stored.UpdatedAtUtc = now;
                    stored.StatusNote = "用户已忽略本批 AI 建议。";
                    count++;
                }
            }
        }

        if (count > 0)
        {
            InboxChanged?.Invoke(this, EventArgs.Empty);
        }
        return count;
    }

    /// <summary>
    /// 当扫描快照更新、撤销授权或策略版本变更时，立即将受影响的旧建议置为 Invalidated。
    /// </summary>
    public int InvalidateForScan(Guid scanId, string reason)
    {
        int count = 0;
        lock (_sync)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (StoredProposal stored in _byProposalId.Values)
            {
                if (stored.Receipt.ScanId == scanId && stored.Receipt.Status != AiProposalStatus.Invalidated)
                {
                    stored.Receipt = stored.Receipt with { Status = AiProposalStatus.Invalidated };
                    stored.UpdatedAtUtc = now;
                    stored.StatusNote = reason;
                    count++;
                }
            }
        }

        if (count > 0)
        {
            InboxChanged?.Invoke(this, EventArgs.Empty);
        }
        return count;
    }

    public int InvalidateAllExceptScan(Guid activeScanId, string reason)
    {
        int count = 0;
        lock (_sync)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (StoredProposal stored in _byProposalId.Values)
            {
                if (stored.Receipt.ScanId != activeScanId && stored.Receipt.Status != AiProposalStatus.Invalidated)
                {
                    stored.Receipt = stored.Receipt with { Status = AiProposalStatus.Invalidated };
                    stored.UpdatedAtUtc = now;
                    stored.StatusNote = reason;
                    count++;
                }
            }
        }

        if (count > 0)
        {
            InboxChanged?.Invoke(this, EventArgs.Empty);
        }
        return count;
    }

    public static string ComputeRequestDigest(AiMigrationProposalRequest request)
    {
        var canonical = new
        {
            request.SchemaVersion,
            ClientRequestId = request.ClientRequestId.Trim(),
            request.ScanId,
            PolicyVersion = request.PolicyVersion.Trim(),
            Items = (request.Items ?? []).Select(i => new
            {
                i.NodeId,
                TargetCandidateId = i.TargetCandidateId?.Trim() ?? string.Empty,
                i.Priority,
                Reason = i.Reason?.Trim() ?? string.Empty,
                CompatibilityNotes = i.CompatibilityNotes?.Trim() ?? string.Empty,
                SuggestedCategory = i.SuggestedCategory?.Trim() ?? string.Empty
            }).ToArray()
        };

        string json = JsonSerializer.Serialize(canonical);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash);
    }

    // ==================== 三期：清理建议与「AI 疑似残留」收件箱 ====================

    public bool TryGetCleanupIdempotentOrConflict(
        string clientRequestId,
        string requestDigest,
        out AiCleanupProposalReceipt? existingReplay,
        out bool isConflict)
    {
        lock (_sync)
        {
            if (_cleanupByClientRequestId.TryGetValue(clientRequestId, out var hit)
                && _cleanupReceipts.TryGetValue(hit.ProposalId, out AiCleanupProposalReceipt? stored))
            {
                if (string.Equals(hit.RequestDigest, requestDigest, StringComparison.Ordinal))
                {
                    existingReplay = stored with { IsIdempotentReplay = true };
                    isConflict = false;
                    return true;
                }

                existingReplay = null;
                isConflict = true;
                return true;
            }

            existingReplay = null;
            isConflict = false;
            return false;
        }
    }

    public void RecordCleanupProposal(AiCleanupProposalReceipt receipt, string requestDigest)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        lock (_sync)
        {
            _cleanupReceipts[receipt.ProposalId] = receipt;
            if (!string.IsNullOrWhiteSpace(receipt.ClientRequestId))
            {
                _cleanupByClientRequestId[receipt.ClientRequestId] = (receipt.ProposalId, requestDigest);
            }

            foreach (AiCleanupProposalItemRecord item in receipt.Items)
            {
                if (item.Verdict is AiCleanupProposalItemVerdict.AcceptedResidualQuarantineOnly or AiCleanupProposalItemVerdict.HardBlockedByExclusion
                    && receipt.ScanId is Guid scanId
                    && item.NodeId is int nodeId)
                {
                    bool alreadyPresent = _cleanupResidualCandidates.Any(c =>
                        c.ScanId == scanId
                        && c.NodeId == nodeId
                        && c.Status is AiCleanupCandidateStatus.ProposedAwaitingAdoption
                            or AiCleanupCandidateStatus.PendingManualReview
                            or AiCleanupCandidateStatus.HardBlockedByExclusion);
                    if (!alreadyPresent)
                    {
                        _cleanupResidualCandidates.Add(new AiCleanupCandidateRecord(
                            CandidateId: item.ProposalItemId,
                            ProposalId: receipt.ProposalId,
                            ScanId: scanId,
                            NodeId: nodeId,
                            DisplayName: item.DisplayName,
                            SanitizedPath: item.SanitizedPath,
                            ActualLocalPath: item.ActualLocalPath,
                            SizeBytes: item.SizeBytes,
                            FileCount: item.FileCount,
                            Reason: item.Reason,
                            RiskWarning: item.RiskWarning,
                            Status: item.Verdict == AiCleanupProposalItemVerdict.HardBlockedByExclusion
                                ? AiCleanupCandidateStatus.HardBlockedByExclusion
                                : AiCleanupCandidateStatus.ProposedAwaitingAdoption,
                            ExclusionReasonCode: item.ExclusionReasonCode,
                            ExclusionCategoryTitle: item.ExclusionCategoryTitle,
                            ExclusionMessage: item.Verdict == AiCleanupProposalItemVerdict.HardBlockedByExclusion ? item.VerdictMessage : null,
                            CreatedAtUtc: receipt.ReceivedAtUtc));
                    }
                }
                else if (item.Verdict == AiCleanupProposalItemVerdict.AcceptedRuleAdvice
                    && receipt.CleanupScanId is Guid cScanId
                    && !string.IsNullOrWhiteSpace(item.RuleId)
                    && item.OriginalRuleTier is CleanupRecommendationTier tier)
                {
                    _cleanupRuleAdvices.RemoveAll(a => a.CleanupScanId == cScanId && a.RuleId.Equals(item.RuleId, StringComparison.OrdinalIgnoreCase));
                    _cleanupRuleAdvices.Add(new AiCleanupRuleAdviceRecord(
                        ProposalId: receipt.ProposalId,
                        CleanupScanId: cScanId,
                        RuleId: item.RuleId,
                        RuleDisplayName: item.DisplayName,
                        OriginalTier: tier,
                        Priority: item.Priority,
                        Reason: item.Reason,
                        RiskWarning: item.RiskWarning,
                        CreatedAtUtc: receipt.ReceivedAtUtc));
                }
            }
        }

        CleanupProposalsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<AiCleanupCandidateRecord> GetCleanupResidualCandidates(bool includeUnadopted = false)
    {
        lock (_sync)
        {
            return _cleanupResidualCandidates
                .Where(c => includeUnadopted || c.Status != AiCleanupCandidateStatus.ProposedAwaitingAdoption)
                .OrderBy(c => c.Status == AiCleanupCandidateStatus.HardBlockedByExclusion ? 1 : 0)
                .ThenByDescending(c => c.SizeBytes)
                .ToArray();
        }
    }

    /// <summary>
    /// 用户在审阅弹框中确认后，将单条「AI 疑似残留」候选从待采纳状态加入「AI 疑似残留」复核列表。
    /// </summary>
    public bool AdoptCleanupResidualCandidate(Guid candidateId, Guid? scanId = null, int? nodeId = null) =>
        AdoptCleanupResidualCandidates([(candidateId, scanId, nodeId)]) > 0;

    /// <summary>
    /// 用户在审阅弹框中批量确认后，将所选「AI 疑似残留」候选加入「AI 疑似残留」复核列表（单次触发事件）。
    /// </summary>
    public int AdoptCleanupResidualCandidates(IEnumerable<(Guid CandidateId, Guid? ScanId, int? NodeId)> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        int adoptedCount = 0;
        lock (_sync)
        {
            foreach ((Guid candidateId, Guid? scanId, int? nodeId) in targets)
            {
                int idx = FindCleanupResidualIndexLocked(candidateId, scanId, nodeId);
                if (idx < 0) continue;

                AiCleanupCandidateRecord current = _cleanupResidualCandidates[idx];
                if (current.Status == AiCleanupCandidateStatus.ProposedAwaitingAdoption)
                {
                    _cleanupResidualCandidates[idx] = current with { Status = AiCleanupCandidateStatus.PendingManualReview };
                    adoptedCount++;
                }
            }
        }

        if (adoptedCount > 0)
        {
            CleanupProposalsChanged?.Invoke(this, EventArgs.Empty);
        }
        return adoptedCount;
    }

    public AiCleanupCandidateStatus? GetCleanupResidualCandidateStatus(Guid candidateId, Guid? scanId = null, int? nodeId = null)
    {
        lock (_sync)
        {
            int idx = FindCleanupResidualIndexLocked(candidateId, scanId, nodeId);
            return idx >= 0 ? _cleanupResidualCandidates[idx].Status : null;
        }
    }

    public int GetPendingUnadoptedResidualCount()
    {
        lock (_sync)
        {
            return _cleanupResidualCandidates.Count(c => c.Status == AiCleanupCandidateStatus.ProposedAwaitingAdoption);
        }
    }

    private int FindCleanupResidualIndexLocked(Guid candidateId, Guid? scanId, int? nodeId)
    {
        if (candidateId != Guid.Empty)
        {
            for (int i = 0; i < _cleanupResidualCandidates.Count; i++)
            {
                if (_cleanupResidualCandidates[i].CandidateId == candidateId)
                {
                    return i;
                }
            }
        }

        if (scanId is Guid sid && sid != Guid.Empty && nodeId is int nid && nid > 0)
        {
            for (int i = _cleanupResidualCandidates.Count - 1; i >= 0; i--)
            {
                AiCleanupCandidateRecord c = _cleanupResidualCandidates[i];
                if (c.ScanId == sid && c.NodeId == nid)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    public IReadOnlyList<AiCleanupRuleAdviceRecord> GetCleanupRuleAdvices(Guid? cleanupScanId = null)
    {
        lock (_sync)
        {
            return _cleanupRuleAdvices
                .Where(a => cleanupScanId is null || a.CleanupScanId == cleanupScanId.Value)
                .OrderBy(a => a.Priority)
                .ToArray();
        }
    }

    public void MarkCleanupResidualQuarantined(Guid candidateId, string batchId)
    {
        bool changed = false;
        lock (_sync)
        {
            for (int i = 0; i < _cleanupResidualCandidates.Count; i++)
            {
                if (_cleanupResidualCandidates[i].CandidateId == candidateId)
                {
                    _cleanupResidualCandidates[i] = _cleanupResidualCandidates[i] with
                    {
                        Status = AiCleanupCandidateStatus.Quarantined,
                        QuarantineBatchId = batchId
                    };
                    changed = true;
                    break;
                }
            }
        }
        if (changed) CleanupProposalsChanged?.Invoke(this, EventArgs.Empty);
    }

    public int InvalidateCleanupCandidatesForScan(Guid scanId)
    {
        int count = 0;
        lock (_sync)
        {
            for (int i = 0; i < _cleanupResidualCandidates.Count; i++)
            {
                AiCleanupCandidateRecord c = _cleanupResidualCandidates[i];
                if (c.ScanId == scanId
                    && c.Status is AiCleanupCandidateStatus.PendingManualReview or AiCleanupCandidateStatus.ProposedAwaitingAdoption)
                {
                    _cleanupResidualCandidates[i] = c with { Status = AiCleanupCandidateStatus.Invalidated };
                    count++;
                }
            }
        }
        if (count > 0) CleanupProposalsChanged?.Invoke(this, EventArgs.Empty);
        return count;
    }

    public static string ComputeCleanupRequestDigest(AiCleanupProposalRequest request)
    {
        var canonical = new
        {
            request.SchemaVersion,
            ClientRequestId = request.ClientRequestId.Trim(),
            request.ScanId,
            request.CleanupScanId,
            PolicyVersion = request.PolicyVersion.Trim(),
            Items = (request.Items ?? []).Select(i => new
            {
                Kind = i.Kind?.Trim() ?? string.Empty,
                RuleId = i.RuleId?.Trim() ?? string.Empty,
                i.NodeId,
                SuggestedAction = i.SuggestedAction?.Trim() ?? string.Empty,
                i.Priority,
                Reason = i.Reason?.Trim() ?? string.Empty,
                RiskWarning = i.RiskWarning?.Trim() ?? string.Empty
            }).ToArray()
        };

        string json = JsonSerializer.Serialize(canonical);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash);
    }
}
