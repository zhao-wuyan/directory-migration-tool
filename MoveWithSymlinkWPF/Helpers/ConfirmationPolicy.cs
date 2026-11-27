using MigrationCore.Models;
using MigrationCore.Models.Cleanup;
using MigrationCore.Services;
using MoveWithSymlinkWPF.ViewModels;

namespace MoveWithSymlinkWPF.Helpers;

/// <summary>
/// 免打字分级二次确认档位。
/// </summary>
public enum ConfirmationTier
{
    /// <summary>常规单击确认（非高危操作）。</summary>
    Direct,
    /// <summary>L1 常规档：单步长按 1.2 秒确认（免勾选、免打字）。</summary>
    StandardHold,
    /// <summary>L2 高危档：勾选风险知情项 + 长按 1.5 秒确认（免打字）。</summary>
    HighRiskCheckboxAndHold
}

public sealed record QuarantineConfirmationPlan(
    ConfirmationTier Tier,
    double HoldSeconds,
    bool RequiresCheckbox,
    IReadOnlyList<string> HighRiskReasons);

public sealed record CleanupConfirmationPlan(
    ConfirmationTier Tier,
    double HoldSeconds,
    bool RequiresCheckbox,
    bool HasCautionItems,
    bool HasChannelB,
    string ExpectedCautionPhrase,
    bool HasDirectDeleteTargets = false);

public sealed record BackupConfirmationPlan(
    ConfirmationTier Tier,
    double HoldSeconds,
    bool RequiresCheckbox);

/// <summary>
/// 全项目统一的分级免打字确认策略。
/// </summary>
public static class ConfirmationPolicy
{
    public const double StandardHoldSeconds = 1.2;
    public const double HighRiskHoldSeconds = 1.5;
    public const long QuarantineHighRiskSizeBytes = 10L * 1024 * 1024 * 1024; // 10 GB

    /// <summary>
    /// 评估隔离区永久删除（单批或批量）的风险档位：
    /// 命中「批量 ≥ 2 批 / 含 AI 疑似残留 / 总占用 ≥ 10 GB」任一条件升级为 L2（勾选 + 长按 1.5s），
    /// 否则为 L1（直接长按 1.2s）。
    /// </summary>
    public static QuarantineConfirmationPlan EvaluateQuarantineDelete(IReadOnlyList<QuarantineBatchRecord> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);
        if (batches.Count == 0)
        {
            throw new ArgumentException("至少需要指定 1 个待删除的隔离批次。", nameof(batches));
        }

        List<string> reasons = [];
        if (batches.Count > 1)
        {
            reasons.Add($"批量删除 {batches.Count} 个隔离批次");
        }

        int aiCount = batches.Count(b => b.IsFromAiSuspectedResidual);
        if (aiCount > 0)
        {
            reasons.Add(batches.Count == 1
                ? "属于「AI 疑似残留」目录"
                : $"包含 {aiCount} 批「AI 疑似残留」目录");
        }

        long totalBytes = batches.Sum(b => b.TotalBytes);
        if (totalBytes >= QuarantineHighRiskSizeBytes)
        {
            reasons.Add($"占用空间达 {FileStatsService.FormatBytes(totalBytes)}（≥ 10 GB）");
        }

        if (reasons.Count > 0)
        {
            return new QuarantineConfirmationPlan(
                Tier: ConfirmationTier.HighRiskCheckboxAndHold,
                HoldSeconds: HighRiskHoldSeconds,
                RequiresCheckbox: true,
                HighRiskReasons: reasons);
        }

        return new QuarantineConfirmationPlan(
            Tier: ConfirmationTier.StandardHold,
            HoldSeconds: StandardHoldSeconds,
            RequiresCheckbox: false,
            HighRiskReasons: []);
    }

    /// <summary>
    /// 评估垃圾清理执行确认的风险档位：
    /// 不含 3 档谨慎项且走隔离/回收站 -> 常规单击；
    /// 不含 3 档谨慎项但用户显式选择「直接永久删除 1 档推荐项」-> L1 直接长按 1.2s 防误触；
    /// 含 3 档谨慎项且仅走通道 A -> L1 直接长按 1.2s；
    /// 含 3 档谨慎项且包含通道 B（官方接口不可恢复）-> L2 勾选 + 长按 1.5s。
    /// </summary>
    public static CleanupConfirmationPlan EvaluateCleanupConfirm(
        IReadOnlyList<CleanupRuleItemViewModel> selectedItems,
        CleanupFileAction fileAction = CleanupFileAction.QuarantineSameVolume)
    {
        ArgumentNullException.ThrowIfNull(selectedItems);

        bool hasChannelB = selectedItems.Any(i => i.Channel == CleanupExecutionChannel.OfficialSystemApi);
        bool hasDirectDeleteTargets = fileAction == CleanupFileAction.DirectPermanentDelete
            && selectedItems.Any(i => i.Rule.SupportsDirectPermanentDelete);
        CleanupRuleItemViewModel[] cautionItems = selectedItems
            .Where(i => i.IsCaution && i.Rule.CautionReason is { RequiresPhraseConfirmation: true })
            .ToArray();

        if (cautionItems.Length == 0)
        {
            if (hasDirectDeleteTargets)
            {
                return new CleanupConfirmationPlan(
                    Tier: ConfirmationTier.StandardHold,
                    HoldSeconds: StandardHoldSeconds,
                    RequiresCheckbox: false,
                    HasCautionItems: false,
                    HasChannelB: hasChannelB,
                    ExpectedCautionPhrase: string.Empty,
                    HasDirectDeleteTargets: true);
            }

            return new CleanupConfirmationPlan(
                Tier: ConfirmationTier.Direct,
                HoldSeconds: 0,
                RequiresCheckbox: false,
                HasCautionItems: false,
                HasChannelB: hasChannelB,
                ExpectedCautionPhrase: string.Empty,
                HasDirectDeleteTargets: false);
        }

        string expectedPhrase = cautionItems[0].Rule.CautionReason!.ConfirmationPhrase;
        if (hasChannelB)
        {
            return new CleanupConfirmationPlan(
                Tier: ConfirmationTier.HighRiskCheckboxAndHold,
                HoldSeconds: HighRiskHoldSeconds,
                RequiresCheckbox: true,
                HasCautionItems: true,
                HasChannelB: true,
                ExpectedCautionPhrase: expectedPhrase,
                HasDirectDeleteTargets: hasDirectDeleteTargets);
        }

        return new CleanupConfirmationPlan(
            Tier: ConfirmationTier.StandardHold,
            HoldSeconds: StandardHoldSeconds,
            RequiresCheckbox: false,
            HasCautionItems: true,
            HasChannelB: false,
            ExpectedCautionPhrase: expectedPhrase,
            HasDirectDeleteTargets: hasDirectDeleteTargets);
    }

    /// <summary>
    /// 评估迁移原始备份处理的确认档位：
    /// 第二步且为永久删除 -> L2 勾选核对原目录名 + 长按 1.5s；其余步骤或回收站模式保持单击。
    /// </summary>
    public static BackupConfirmationPlan EvaluateBackupConfirm(BackupDeletionMode mode, ConfirmationStep step)
    {
        if (step == ConfirmationStep.Second && mode == BackupDeletionMode.Permanent)
        {
            return new BackupConfirmationPlan(
                Tier: ConfirmationTier.HighRiskCheckboxAndHold,
                HoldSeconds: HighRiskHoldSeconds,
                RequiresCheckbox: true);
        }

        return new BackupConfirmationPlan(
            Tier: ConfirmationTier.Direct,
            HoldSeconds: 0,
            RequiresCheckbox: false);
    }
}
