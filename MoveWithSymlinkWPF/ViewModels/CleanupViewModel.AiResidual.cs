using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MigrationCore.Models.Cleanup;
using MigrationCore.Services;
using MigrationCore.Services.Cleanup;
using MoveWithSymlinkWPF.Helpers;
using MoveWithSymlinkWPF.Views;

namespace MoveWithSymlinkWPF.ViewModels;

public partial class CleanupViewModel
{
    [RelayCommand]
    private void CancelAiResidualSelection()
    {
        foreach (AiResidualCandidateRowViewModel item in AiResidualCandidates)
        {
            item.IsChecked = false;
        }
        IsUserVerifiedContentChecked = false;
        IsUserAcknowledgedDisclaimerChecked = false;
        RecalculateAiResidualSummary();
    }

    [RelayCommand]
    private async Task ConfirmAiResidualQuarantineAsync()
    {
        if (!CanConfirmAiQuarantine) return;

        AiResidualCandidateRowViewModel[] chosen = AiResidualCandidates.Where(c => c.IsChecked).ToArray();
        if (chosen.Length == 0) return;

        IsExecuting = true;
        ExecutionProgressIndeterminate = true;
        ExecutionProgressHeader = $"正在同卷隔离 AI 疑似残留（{chosen.Length} 项）";
        ExecutionProgressMessage = "正在二次核验永久排除区并移入同卷隔离区…";
        StatusMessage = ExecutionProgressMessage;
        try
        {
            var aiProgress = new Progress<string>(msg => DispatchToUi(() =>
            {
                ExecutionProgressMessage = msg;
                StatusMessage = msg;
            }));
            CleanupExecutionResult result = await _cleanupService.ExecuteAiSuspectedResidualQuarantineAsync(
                chosen.Select(c => c.Record).ToArray(),
                userVerifiedContent: IsUserVerifiedContentChecked,
                userAcknowledgedDisclaimer: IsUserAcknowledgedDisclaimerChecked,
                progress: aiProgress);

            DispatchToUi(() =>
            {
                foreach (CleanupExecutionItemResult item in result.Items)
                {
                    if (item.Status == CleanupItemExecutionStatus.Quarantined
                        && Guid.TryParse(item.TargetId, out Guid cid)
                        && !string.IsNullOrWhiteSpace(item.QuarantineBatchId))
                    {
                        _aiAssistant?.Toolset.Inbox.MarkCleanupResidualQuarantined(cid, item.QuarantineBatchId);
                    }
                }

                IsUserVerifiedContentChecked = false;
                IsUserAcknowledgedDisclaimerChecked = false;
                _quarantineDirty = true;
                _overviewDirty = true;
                SharedWorkspaceStateCache.Shared.Invalidate();
                _ = RefreshQuarantineListAsync(checkRestoreConflicts: ActiveSubView == CleanupSubView.QuarantineManager, force: true);
                _ = OnEnteredCleanupPageAsync(force: true);
                SyncAiResidualCandidatesFromInbox();
                StatusMessage = result.SummaryMessage;
                CustomMessageBox.ShowInformation(FormatExecutionResultDetail(result), "AI 疑似残留隔离结果");
            });
        }
        catch (Exception ex)
        {
            DispatchToUi(() => CustomMessageBox.ShowWarning(ex.Message, "同卷隔离失败"));
        }
        finally
        {
            IsExecuting = false;
        }
    }

    public void RefreshQuarantineList()
    {
        IReadOnlyList<QuarantineBatchInspection> inspections = _quarantineStore.ListAllInspections();
        ApplyQuarantineInspections(inspections, isFullConflictInspection: true);
    }

    /// <summary>
    /// 后台异步刷新隔离区批次（默认进入隔离区子视图时执行完整恢复冲突核验，零阻塞 UI 线程）。
    /// </summary>
    public async Task RefreshQuarantineListAsync(bool checkRestoreConflicts = true, bool force = false)
    {
        if (checkRestoreConflicts
            && !force
            && !_quarantineDirty
            && _lastQuarantineFullInspectUtc != DateTimeOffset.MinValue
            && (DateTimeOffset.UtcNow - _lastQuarantineFullInspectUtc) < QuarantineInspectTtl
            && QuarantineBatches.Count == QuarantineBatchCount)
        {
            return;
        }

        CancellationTokenSource? oldCts = Interlocked.Exchange(ref _quarantineRefreshCts, new CancellationTokenSource());
        try { oldCts?.Cancel(); } catch { }
        oldCts?.Dispose();

        CancellationToken ct = _quarantineRefreshCts!.Token;
        long version = Interlocked.Increment(ref _quarantineRefreshVersion);

        DispatchToUi(() =>
        {
            if (version == Volatile.Read(ref _quarantineRefreshVersion))
            {
                QuarantineLoadingMessage = checkRestoreConflicts
                    ? "正在后台核验隔离区批次与原路径恢复冲突…"
                    : "正在后台读取隔离区批次…";
                IsRefreshingQuarantine = true;
            }
        });

        try
        {
            IReadOnlyList<QuarantineBatchInspection> inspections = await _quarantineStore
                .ListAllInspectionsAsync(checkRestoreConflicts: checkRestoreConflicts, ct: ct)
                .ConfigureAwait(false);

            await DispatchToUiAsync(() =>
            {
                if (ct.IsCancellationRequested || version != Volatile.Read(ref _quarantineRefreshVersion))
                {
                    return;
                }

                ApplyQuarantineInspections(inspections, isFullConflictInspection: checkRestoreConflicts);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await DispatchToUiAsync(() =>
            {
                if (version == Volatile.Read(ref _quarantineRefreshVersion))
                {
                    IsRefreshingQuarantine = false;
                }
            }).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 主清理页后台轻量刷盘回填隔离区摘要：若当前尚无完整冲突核验结果或批次列表已变化，则同步更新基础行，
    /// 否则保留已完成的冲突标签避免误清。
    /// </summary>
    private void ApplyQuarantineSummaryFromOverview(IReadOnlyList<QuarantineBatchInspection> liteInspections)
    {
        bool sameBatchSet = QuarantineBatches.Count == liteInspections.Count
            && QuarantineBatches.Zip(liteInspections).All(pair =>
                pair.First.BatchId.Equals(pair.Second.Batch.BatchId, StringComparison.OrdinalIgnoreCase)
                && pair.First.Batch.ItemCount == pair.Second.Batch.ItemCount
                && pair.First.Batch.TotalBytes == pair.Second.Batch.TotalBytes);

        if (sameBatchSet && !_quarantineDirty && _lastQuarantineFullInspectUtc != DateTimeOffset.MinValue)
        {
            QuarantineBatchCount = liteInspections.Count;
            QuarantineTotalBytes = liteInspections.Sum(i => i.Batch.TotalBytes);
            return;
        }

        if (!sameBatchSet)
        {
            _quarantineDirty = true;
        }

        ApplyQuarantineInspections(liteInspections, isFullConflictInspection: false);
    }

    private void ApplyQuarantineInspections(
        IReadOnlyList<QuarantineBatchInspection> inspections,
        bool isFullConflictInspection)
    {
        var preservedCheckedIds = new HashSet<string>(
            QuarantineBatches.Where(b => b.IsChecked).Select(b => b.BatchId),
            StringComparer.OrdinalIgnoreCase);

        _isBatchUpdatingQuarantineSelection = true;
        try
        {
            QuarantineBatches.Clear();
            long totalBytes = 0;
            var conflictTitles = new List<string>();

            foreach (QuarantineBatchInspection insp in inspections)
            {
                var row = new QuarantineBatchRowViewModel(insp, RecalculateQuarantineSelectionSummary);
                if (preservedCheckedIds.Contains(row.BatchId))
                {
                    row.IsChecked = true;
                }
                QuarantineBatches.Add(row);
                totalBytes += insp.Batch.TotalBytes;
                if (insp.HasRestoreConflict)
                {
                    conflictTitles.Add(insp.Batch.DisplayTitle);
                }
            }

            QuarantineBatchCount = QuarantineBatches.Count;
            QuarantineTotalBytes = totalBytes;
            HasAnyQuarantineConflict = conflictTitles.Count > 0;
            QuarantineConflictNote = HasAnyQuarantineConflict
                ? $"恢复冲突保护：检测到「{string.Join("、", conflictTitles.Take(3))}」原路径已生成同名新文件。点击「对比后恢复」将保留双方文件（隔离项自动加后缀恢复），绝不静默覆盖当前正在使用的文件。"
                : string.Empty;
        }
        finally
        {
            _isBatchUpdatingQuarantineSelection = false;
        }

        RecalculateQuarantineSelectionSummary();

        if (isFullConflictInspection)
        {
            _quarantineDirty = false;
            _lastQuarantineFullInspectUtc = DateTimeOffset.UtcNow;
        }
    }

    private void RecalculateQuarantineSelectionSummary()
    {
        if (_isBatchUpdatingQuarantineSelection) return;

        int total = QuarantineBatches.Count;
        int checkedCount = 0;
        long checkedBytes = 0;
        foreach (QuarantineBatchRowViewModel row in QuarantineBatches)
        {
            if (row.IsChecked)
            {
                checkedCount++;
                checkedBytes += row.Batch.TotalBytes;
            }
        }

        CheckedQuarantineBatchCount = checkedCount;
        CheckedQuarantineTotalBytes = checkedBytes;
        AllQuarantineSelectedState = total == 0 || checkedCount == 0
            ? false
            : checkedCount >= total
                ? true
                : null;
        OnPropertyChanged(nameof(QuarantineSelectionSummaryText));
    }

    public void SyncAiResidualCandidatesFromInbox()
    {
        AiResidualCandidates.Clear();
        AiHardBlockedCandidates.Clear();

        if (_aiAssistant is null)
        {
            PendingAiResidualCount = 0;
            HardBlockedAiResidualCount = 0;
            RecalculateAiResidualSummary();
            return;
        }

        IReadOnlyList<AiCleanupCandidateRecord> all = _aiAssistant.Toolset.Inbox.GetCleanupResidualCandidates();
        foreach (AiCleanupCandidateRecord rec in all)
        {
            if (rec.Status == AiCleanupCandidateStatus.HardBlockedByExclusion)
            {
                AiHardBlockedCandidates.Add(new AiHardBlockedRowViewModel
                {
                    Title = $"已自动拦截：{rec.DisplayName}（{FileStatsService.FormatBytes(rec.SizeBytes)}）",
                    PathText = rec.SanitizedPath,
                    ExclusionMessage = rec.ExclusionMessage ?? "命中「永久排除区」；即使 AI 建议清理，核心安全策略也强制拒收且永不提供勾选入口。"
                });
            }
            else
            {
                AiResidualCandidates.Add(new AiResidualCandidateRowViewModel(rec, RecalculateAiResidualSummary));
            }
        }

        PendingAiResidualCount = AiResidualCandidates.Count(c => c.CanCheck);
        HardBlockedAiResidualCount = AiHardBlockedCandidates.Count;
        IsUserVerifiedContentChecked = false;
        IsUserAcknowledgedDisclaimerChecked = false;
        RecalculateAiResidualSummary();
    }

    private void OnAiCleanupProposalsChanged(object? sender, EventArgs e)
    {
        DispatchToUi(SyncAiResidualCandidatesFromInbox);
    }

    private void RecalculateAiResidualSummary()
    {
        AiResidualCandidateRowViewModel[] chosen = AiResidualCandidates.Where(c => c.IsChecked).ToArray();
        SelectedAiResidualCount = chosen.Length;
        SelectedAiResidualBytes = chosen.Sum(c => c.SizeBytes);

        // 每次勾选集合变化都重置双勾选，强制重新审阅当前冻结范围
        IsUserVerifiedContentChecked = false;
        IsUserAcknowledgedDisclaimerChecked = false;

        if (chosen.Length == 0)
        {
            AiConfirmSourcePreview = "请在左侧勾选经您人工核实后的 AI 疑似残留目录（默认全不勾选）。";
            AiConfirmTargetPreview = "隔离区：<Drive>:\\.MoveFolderQuarantine\\ai-yyyyMMdd-xxxxxx（同卷原子移入）";
            return;
        }

        AiConfirmSourcePreview = chosen.Length == 1
            ? $"源路径：{chosen[0].ActualLocalPath}"
            : $"源路径：{chosen[0].ActualLocalPath} 等共 {chosen.Length} 个目录";

        string drive = chosen[0].ActualLocalPath.Length >= 3 ? chosen[0].ActualLocalPath[..3] : @"C:\";
        AiConfirmTargetPreview = $"隔离区：{drive}.MoveFolderQuarantine\\ai-{DateTime.Now:yyyyMMdd}-xxxxxx（同卷原子移入）";
    }

}
