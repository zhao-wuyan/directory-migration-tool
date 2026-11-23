using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MigrationCore.Models.Ai;

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

    private readonly object _sync = new();
    private readonly Dictionary<Guid, StoredProposal> _byProposalId = new();
    private readonly Dictionary<string, (Guid ProposalId, string RequestDigest)> _byClientRequestId = new(StringComparer.Ordinal);
    private readonly List<Guid> _orderedIds = new();

    public event EventHandler? InboxChanged;

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
}
